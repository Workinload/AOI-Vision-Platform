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
    private readonly VisionAssetLedger _assets;
    private readonly PublishedParameterSnapshot? _publishedParameters;
    private readonly ProductionTimingJournal _timeline;
    private readonly LegacyFinsLoopbackClient _plc;
    private readonly ProductionStore _store;
    private readonly SimProductRuntime _product;
    private readonly bool _frameFirst;
    private readonly LineScanRowMapping _lineRowMapping;
    private readonly ushort _missingToken;
    private readonly string? _missingDevice;
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
        var missingToken = Environment.GetEnvironmentVariable("AOI_SIM_MISSING_FRAME_TOKEN");
        _missingDevice = Environment.GetEnvironmentVariable("AOI_SIM_MISSING_FRAME_DEVICE");
        if (!string.IsNullOrWhiteSpace(missingToken))
        {
            if (!ushort.TryParse(missingToken, out ushort parsed) || parsed == 0 ||
                string.IsNullOrWhiteSpace(_missingDevice) ||
                !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == _missingDevice))
                throw new InvalidOperationException(
                    "SIM_ONLY missing-frame injection requires valid Token and physical device.");
            _missingToken = parsed;
        }
        _lineRowMapping = Environment.GetEnvironmentVariable("AOI_SIM_LINE_ROW_MAPPING") switch
        {
            null or "" or "DEMO_EVEN_ODD" => LineScanRowMapping.DemoEvenOdd,
            "LEGACY_DROP_ROW0" => LineScanRowMapping.LegacySkipFirstRow,
            _ => throw new InvalidOperationException("Unknown line-scan physical row mapping.")
        };
        _source = new PngFileFrameSource(imageRoot,
            flatLab: Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_LAYOUT") == "FLAT");
        _captures = new CaptureHandshakeCoordinator(
            Path.Combine(nodeFolder, "capture-handshake.sqlite"));
        _assets = new VisionAssetLedger(Path.Combine(nodeFolder, "vision"));
        _inspection = new TriggeredVisionEngine(_assets,
            new OpenCvInspectionProcessor(), lineRowMapping: _lineRowMapping);
        // Immutable dual-Core A6 Release is pinned for this Core process.
        // Individual operations Bind the exact WorkpieceId/TraceId/Epoch/Release,
        // never look up a mutable operator draft during real camera exposure.
        _publishedParameters = _product.Published is null ? null :
            new PublishedParameterSnapshot(_product.Published, _product.Epoch);
        _timeline = new ProductionTimingJournal(nodeFolder,
            owner == MachineRole.Master ? "master" : "slave");
    }

    private void PersistFault(PhysicalCaptureTrigger trigger,
        string reasonCode, string detail)
    {
        if (_owner == MachineRole.Slave)
        {
            var fault = new PhysicalCaptureFault(trigger, reasonCode, detail,
                DateTimeOffset.UtcNow);
            var outbound = PeerEnvelope.Create("slave", "master",
                Interlocked.Increment(ref _resultSequence), trigger.Workpiece,
                "capture_fault", fault, trigger.Channels[0].ChannelId);
            _store.HoldAndQueueCaptureFault(fault, outbound);
            Console.WriteLine("SIM_SLAVE_FAULT_QUEUED token=" + trigger.Token +
                " workpiece=" + trigger.Workpiece.WorkpieceId +
                " capture=" + trigger.CaptureId + " device=" +
                trigger.PhysicalDeviceId + " reason=" + reasonCode);
        }
        else
            _store.RecordWorkpieceHold(trigger.Workpiece, reasonCode, detail);
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
        ResolvedPhysicalInspectionPlan? publishedPlan = null;
        if (_publishedParameters is not null)
        {
            try
            {
                // Same identity and exact physical trigger mapping; one line-scan
                // parent obtains a single camera timeout and TWO child rules.
                publishedPlan = PublishedRuntimeParameterResolver.ResolveForTrigger(
                    _publishedParameters.Bind(item), item, trigger.PhysicalDeviceId,
                    trigger.OwnerRole, trigger.PlcAddress,
                    trigger.Channels.Select(c => c.ChannelId).ToArray());
                // We ONLY have a file-backed frame adapter in this deployment.
                // A vendor SDK target or software-trigger recipe must never
                // silently execute as a different source of pixels.
                if (publishedPlan.Capture.AdapterId is { } adapter &&
                    adapter != "sim.png.frame-source.v1")
                    throw new NotSupportedException("PUBLISHED_CAMERA_ADAPTER_NOT_INSTALLED:" + adapter);
                if (publishedPlan.Capture.TriggerMode != CaptureTriggerMode.ExternalHardwareEdge)
                    throw new NotSupportedException("PUBLISHED_TRIGGER_MODE_NOT_SUPPORTED");
                Console.WriteLine("SIM_PUBLISHED_CAPTURE_PLAN workpiece=" + item.WorkpieceId +
                    " release=" + item.ReleaseId + " device=" + trigger.PhysicalDeviceId +
                    " channels=" + string.Join(",", publishedPlan.Capture.ChannelIds) +
                    " deadline_ms=" + publishedPlan.Capture.FrameDeadline.TotalMilliseconds +
                    " algorithm=" + string.Join(",",
                        publishedPlan.Inspections.Select(x => x.AlgorithmId)) +
                    " physical_field_verified=0");
            }
            catch (Exception ex) when (ex is InvalidDataException or
                InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // This is a PER-WORKPIECE persistent fault. Never claim a camera
                // completion, inspection GOOD, or issue an unconfigured PLC write.
                PersistFault(trigger, "PHYSICAL_INSPECTION_ERROR",
                    "PUBLISHED_PLAN_NOT_READY:" + ex.GetType().Name + ":" +
                    ex.Message[..Math.Min(260, ex.Message.Length)]);
                Console.Error.WriteLine("SIM_PUBLISHED_CAPTURE_HOLD token=" + trigger.Token +
                    " workpiece=" + item.WorkpieceId + " device=" +
                    trigger.PhysicalDeviceId + " error=" + ex.Message +
                    " no_camera_or_plc_trigger=1");
                return;
            }
        }
        TimeSpan timeout = publishedPlan?.Capture.FrameDeadline ??
            (line ? LegacyPlcTiming.HistoricalReference.LineCameraDeadline
                  : LegacyPlcTiming.HistoricalReference.AreaCameraDeadline);
        ushort dm = ushort.Parse(trigger.PlcAddress.AsSpan(1));
        var ticket = new AgentTicket(item, trigger.CaptureId, trigger.AttemptNo,
            trigger.PhysicalDeviceId, DateTimeOffset.UtcNow + timeout, IsSimulation: true);
        var frameTicket = new VisionTicket(item, trigger.PhysicalDeviceId,
            trigger.CaptureId, trigger.AttemptNo);
        _captures.ExpectCapture(ticket);
        _timeline.Append(new TimingObservation(item, TimingStage.HardwareTriggerObserved,
            trigger.CaptureId, trigger.PhysicalDeviceId, AttemptNo: trigger.AttemptNo,
            Status: "SIM_ONLY_SENSOR_EDGE",
            Details: "Physical sensor event delivered to owning Core; not field-certified."));
        FrameRead? read = null;
        bool valid = false;

        async Task ReceiveActualFrameAsync()
        {
            read = _missingToken != 0 && trigger.Token == _missingToken &&
                trigger.PhysicalDeviceId == _missingDevice
                ? FrameRead.Missing("SIM_INJECTED_HARD_FRAME_ABSENT")
                : await _source.ReadAsync(frameTicket, timeout, ct);
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
        var write = await _plc.WriteObservedAsync(dm, ack.Value, sent =>
        {
            // Record the operation SID BEFORE the datagram is sent; this can
            // never masquerade as an acknowledged FINS request.
            _timeline.Append(new TimingObservation(item, TimingStage.CamDoneSent,
                trigger.CaptureId, trigger.PhysicalDeviceId,
                AttemptNo: trigger.AttemptNo,
                OperationId: sent.OperationId, FinsSid: sent.Sid,
                PlcDmAddress: trigger.PlcAddress, PlcValue: ack.Value,
                Status: "FINS_DATAGRAM_PENDING"));
        }, ct);
        if (!write.ReplyVerified || write.AcknowledgedUtc is null)
            throw new InvalidDataException("Unacknowledged camera completion is not a valid capture.");
        _timeline.Append(new TimingObservation(item, TimingStage.CamDoneAcknowledged,
            trigger.CaptureId, trigger.PhysicalDeviceId,
            AttemptNo: trigger.AttemptNo,
            OperationId: write.OperationId, FinsSid: write.Sid,
            PlcDmAddress: trigger.PlcAddress, PlcValue: ack.Value,
            Status: "ACTUAL_FINS_UDP_ACK"));
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
            PersistFault(trigger, "PHYSICAL_FRAME_MISSING",
                $"{trigger.PhysicalDeviceId}: frame unavailable or invalid; D{dm}=3 confirmed.");
            _timeline.Append(new TimingObservation(item, TimingStage.CaptureFailed,
                trigger.CaptureId, trigger.PhysicalDeviceId,
                AttemptNo: trigger.AttemptNo, Status: "FINS_CAM_DONE_3"));
            return;
        }

        _timeline.Append(new TimingObservation(item, TimingStage.InspectionStarted,
            trigger.CaptureId, trigger.PhysicalDeviceId,
            AttemptNo: trigger.AttemptNo, FrameId: read!.Frame!.FrameId,
            Status: "INSPECTOR_STARTED_AFTER_FINS_ACK"));
        // Real native OpenCV now consumes the A6 RELEASE-BOUND per-channel
        // algorithm and ROI, not mutable UI drafts or a fixed hardcoded area=8.
        // LegacyBrightness keeps its own per-pixel fraction semantics; unknown
        // plugins become a per-item ERROR/HOLD, never a fallback GOOD.
        var inspection = publishedPlan is null ? _inspection :
            new TriggeredVisionEngine(_assets, new PublishedReleaseInspector(publishedPlan),
                lineRowMapping: _lineRowMapping);
        var output = await inspection.InspectAndPersistAsync(frameTicket, read!,
            c => publishedPlan is null
                ? InspectionSettings.OpenCvContour(
                    new ContourDefectRule(_product.RuleFor(c).BrightThreshold, 8))
                : new InspectionSettings(publishedPlan.Inspections
                    .Single(p => p.ChannelId == c.ChannelId).AlgorithmId), ct);

        foreach (var channel in output.Channels)
        {
            if (channel.Observation is null)
            {
                _captures.FailCapture(trigger.CaptureId,
                    "INSPECTOR_FAILURE:" + channel.ChannelId + ":" + channel.Reason);
                PersistFault(trigger, "PHYSICAL_INSPECTION_ERROR",
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
