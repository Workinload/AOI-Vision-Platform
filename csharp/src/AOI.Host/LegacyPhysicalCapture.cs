using AOI.Contracts;
using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc;
using AOI.Runtime.Vision;
using AOI.Solutions;
using OpenCvSharp;
using System.Security.Cryptography;
using AgentTicket = AOI.Runtime.Agent01.CaptureTicket;
using VisionTicket = AOI.Runtime.Vision.CaptureTicket;

namespace AOI.Host;

/// <summary>
/// One per-Core event-driven physical image pipeline for all seven area cameras
/// and the two line-scan parents (nine physical requests, eleven channel results).
/// The external sensor event comes from Master and retains an immutable CaptureId
/// even when processed by Slave over durable PeerLink. D4300 is a SIM-only PLC
/// stimulus; D1006..D1014 FINS request/ACK are real loopback UDP datagrams.
/// </summary>
internal sealed class LegacyPhysicalCapture
{
    private readonly MachineRole _owner;
    private readonly CaptureHandshakeCoordinator _captures;
    private readonly PngFileFrameSource _source;
    private readonly TriggeredVisionEngine _inspection;
    private readonly ProductionTimingJournal _timeline;
    private readonly LegacyFinsLoopbackClient _plc;
    private readonly ProductionStore _store;
    private readonly SimProductRuntime _product;
    private readonly bool _frameFirst;
    private long _resultSequence;

    public LegacyPhysicalCapture(string nodeFolder, MachineRole owner,
        ProductionStore store, SimProductRuntime product,
        LegacyFinsLoopbackClient plc)
    {
        _owner = owner;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _product = product ?? throw new ArgumentNullException(nameof(product));
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        var imageRoot = Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_ROOT");
        if (string.IsNullOrWhiteSpace(imageRoot))
            throw new InvalidOperationException(
                "External PLC shadow requires actual PNG frame inputs (no invented pixels).");
        _frameFirst = Environment.GetEnvironmentVariable("AOI_SIM_ST05_ORDER") switch
        {
            null or "" or "REQUEST_FIRST" => false,
            "FRAME_FIRST" => true,
            _ => throw new InvalidOperationException("Unknown PLC/frame arrival ordering.")
        };
        _source = new PngFileFrameSource(imageRoot,
            flatLab: Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_LAYOUT") == "FLAT");
        _captures = new CaptureHandshakeCoordinator(
            Path.Combine(nodeFolder, "capture-handshake.sqlite"));
        _inspection = new TriggeredVisionEngine(
            new VisionAssetLedger(Path.Combine(nodeFolder, "vision")),
            new OpenCvInspectionProcessor());
        _timeline = new ProductionTimingJournal(nodeFolder,
            owner == MachineRole.Master ? "master" : "slave");
    }

    public async Task HandleAsync(PhysicalCaptureTrigger trigger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        trigger.Validate();
        if (trigger.OwnerRole != _owner)
            throw new InvalidDataException("Physical camera request routed to non-owning Core.");
        var item = trigger.Workpiece;
        _product.VerifyWorkpiece(item);
        if (_store.FindWorkpiece(item.WorkpieceId) != item)
            throw new InvalidDataException("Capture token cannot be bound to registered workpiece.");

        var previous = _captures.Latest(item.WorkpieceId, trigger.PhysicalDeviceId);
        if (previous is not null)
        {
            if (previous.Ticket.CaptureId == trigger.CaptureId &&
                previous.ReadyForProductionResult &&
                trigger.Channels.All(c => _store.ResultsFor(item.WorkpieceId).Any(r =>
                    r.ChannelId == c.ChannelId && r.CaptureId == trigger.CaptureId)))
                return; // already committed; safe idempotent handling of a replayed trigger
            throw new InvalidDataException("Duplicate or unfinished physical capture attempt.");
        }

        bool line = trigger.Channels.Count == 2;
        TimeSpan timeout = line
            ? LegacyPlcTiming.HistoricalReference.LineCameraDeadline
            : LegacyPlcTiming.HistoricalReference.AreaCameraDeadline;
        ushort dm = ushort.Parse(trigger.PlcAddress.AsSpan(1));
        var ticket = new AgentTicket(item, trigger.CaptureId, trigger.AttemptNo,
            trigger.PhysicalDeviceId, DateTimeOffset.UtcNow + timeout, IsSimulation: true);
        var frameTicket = new VisionTicket(item, trigger.PhysicalDeviceId,
            trigger.CaptureId, trigger.AttemptNo);
        _captures.ExpectCapture(ticket);
        FrameRead? read = null;
        bool valid = false;

        async Task ReceiveActualFrameAsync()
        {
            read = await _source.ReadAsync(frameTicket, timeout, ct);
            if (read.Status != ChannelOutcome.Ok || read.Frame is null) return;
            // The original source Mat is encoded losslessly BEFORE any algorithm.
            // A1 verifies its source SHA against the persisted A3 ChannelResult.
            byte[] png;
            try
            {
                if (!Cv2.ImEncode(".png", read.Frame.Pixels, out png) || png.Length == 0)
                    return;
            }
            catch (OpenCVException) { return; }
            string sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var observed = new CaptureFrameEvidence(item.WorkpieceId, item.Epoch,
                item.ReleaseId, item.TraceId, trigger.CaptureId, trigger.AttemptNo,
                trigger.PhysicalDeviceId, read.Frame.FrameId, sha,
                read.Frame.FrameReadyUtc);
            if (!_captures.TryFrameAcquired(observed)) return;
            _timeline.Append(new TimingObservation(item, TimingStage.FrameArrived,
                trigger.CaptureId, trigger.PhysicalDeviceId,
                AttemptNo: trigger.AttemptNo, FrameId: read.Frame.FrameId,
                Status: "REAL_PNG_FRAME_READY"));
            valid = _captures.FrameValidated(trigger.CaptureId, read.Frame.FrameId);
            if (valid)
                _timeline.Append(new TimingObservation(item, TimingStage.FrameReady,
                    trigger.CaptureId, trigger.PhysicalDeviceId,
                    AttemptNo: trigger.AttemptNo, FrameId: read.Frame.FrameId,
                    Status: "PIXELS_VERIFIED"));
        }

        if (_frameFirst) await ReceiveActualFrameAsync();
        // External PLC stimulates D1006..D1014. It is NEVER a camera soft trigger.
        await _plc.WriteAsync(LegacyPlcShadow.SimOfferCaptureAddress,
            [trigger.Token, dm], ct);
        var request = await _plc.ReadAsync(dm, 1, ct);
        if (request.Length != 1 || request[0] != 1)
            throw new InvalidDataException("External PLC capture request was not observed.");
        _captures.ObservePlcRequest(trigger.CaptureId, trigger.PlcAddress);
        _timeline.Append(new TimingObservation(item,
            TimingStage.PlcCaptureRequestObserved, trigger.CaptureId,
            trigger.PhysicalDeviceId, AttemptNo: trigger.AttemptNo,
            PlcDmAddress: trigger.PlcAddress, PlcValue: 1,
            Status: "FINS_D1006_1014_READ_1"));
        if (!_frameFirst) await ReceiveActualFrameAsync();

        if (!valid)
            _captures.FailCapture(trigger.CaptureId,
                read?.Reason ?? "FRAME_INVALID_OR_MISSING");
        ushort? ack = _captures.Get(trigger.CaptureId).PendingCamDone;
        if (ack is not (2 or 3))
            throw new InvalidDataException("Camera completion without attributable frame or request.");
        await _plc.WriteAsync(dm, [ack.Value], ct);
        var feedback = await _plc.ReadAsync(dm, 1, ct);
        if (feedback.Length != 1 || feedback[0] != ack.Value)
            throw new InvalidDataException("Camera FINS response/readback not verified.");
        _captures.CamDoneConfirmed(trigger.CaptureId, ack.Value);
        Console.WriteLine($"SIM_PHYSICAL_CAM_DONE_PASS role={_owner} " +
            $"token={trigger.Token} device={trigger.PhysicalDeviceId} " +
            $"capture={trigger.CaptureId} D{dm}={ack.Value} " +
            $"ordering={(_frameFirst ? "FRAME_FIRST" : "REQUEST_FIRST")}");
        if (trigger.PhysicalDeviceId == "HIK-M-2")
            Console.WriteLine("SIM_ST05_REAL_FINS_CAM_DONE_PASS token=" + trigger.Token);

        if (ack == 3)
        {
            read?.Frame?.Dispose();
            _store.RecordWorkpieceHold(item, "PHYSICAL_FRAME_MISSING",
                $"{trigger.PhysicalDeviceId}: frame unavailable or invalid; D{dm}=3 confirmed.");
            _timeline.Append(new TimingObservation(item, TimingStage.CaptureFailed,
                trigger.CaptureId, trigger.PhysicalDeviceId,
                AttemptNo: trigger.AttemptNo, Status: "FINS_CAM_DONE_3"));
            return;
        }

        var output = await _inspection.InspectAndPersistAsync(frameTicket, read!,
            c =>
            {
                var threshold = _product.RuleFor(c);
                ImageRegion? roi = null;
                if (_product.Published is not null)
                {
                    var preset = RoiReleaseWorkflow.ReadPublishedRoi(
                        _product.Published, c.ChannelId);
                    if (preset is not null)
                    {
                        // ROI is expressed in the image delivered TO the inspection
                        // processor. Line siblings are half-height child images.
                        int width = read!.Frame!.Pixels.Cols;
                        int height = read.Frame.Pixels.Rows;
                        if (line)
                        {
                            int parity = trigger.Channels[0].ChannelId == c.ChannelId ? 0 : 1;
                            height = (height + 1 - parity) / 2;
                        }
                        var rect = preset.Bounds.ToPixels(width, height);
                        roi = new ImageRegion(rect.X, rect.Y, rect.Width, rect.Height);
                    }
                }
                return InspectionSettings.OpenCvContour(
                    new ContourDefectRule(threshold.BrightThreshold, 8), roi);
            }, ct);

        foreach (var channel in output.Channels)
        {
            if (channel.Observation is null)
            {
                _captures.FailCapture(trigger.CaptureId,
                    "INSPECTOR_FAILURE:" + channel.ChannelId + ":" + channel.Reason);
                _store.RecordWorkpieceHold(item, "PHYSICAL_INSPECTION_ERROR",
                    trigger.PhysicalDeviceId + " algorithm failed; result remains non-GOOD.");
                return;
            }
            _captures.InspectionCompleted(channel.Observation);
            PeerEnvelope? outbound = _owner == MachineRole.Slave
                ? PeerEnvelope.Create("slave", "master",
                    Interlocked.Increment(ref _resultSequence), item,
                    "channel_result", channel.Observation, channel.ChannelId)
                : null;
            _store.SaveLocalResult(channel.Observation, outbound);
            _timeline.Append(TimingObservation.FromResult(item, channel.Observation));
        }
        if (!_captures.Get(trigger.CaptureId).ReadyForProductionResult)
            throw new InvalidDataException("Unconfirmed FrameReady/CamDone cannot finish physical inspection.");
        Console.WriteLine($"SIM_PHYSICAL_CAPTURE_PASS role={_owner} token={trigger.Token} " +
            $"device={trigger.PhysicalDeviceId} workpiece={item.WorkpieceId} " +
            $"channels={string.Join(",", output.Channels.Select(x => x.ChannelId))}");
        if (trigger.PhysicalDeviceId == "HIK-M-2")
            Console.WriteLine("SIM_ST05_TRIGGERED_CAPTURE_PASS workpiece=" + item.WorkpieceId +
                " CH06=" + output.Channels[0].Outcome);
    }
}
