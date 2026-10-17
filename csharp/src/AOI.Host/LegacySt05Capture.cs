using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc;
using AOI.Runtime.Vision;
using AOI.Solutions;
using OpenCvSharp;
using System.Security.Cryptography;
using AgentCaptureTicket = AOI.Runtime.Agent01.CaptureTicket;
using VisionCaptureTicket = AOI.Runtime.Vision.CaptureTicket;

namespace AOI.Host;

/// <summary>
/// First opt-in, real station-event-driven capture: ST05, HIK-M-2, CH06, D1011.
/// No frame is acquired for this physical device at workpiece registration.
/// A hardware-triggered frame MAY precede observation of PLC request value 1.
/// All device I/O is directed to the loopback legacy shadow, not a production PLC.
/// </summary>
internal sealed class LegacySt05Capture
{
    private const string PhysicalDevice = "HIK-M-2";
    private const ushort CameraDm = 1011;
    private readonly CaptureHandshakeCoordinator _captures;
    private readonly PngFileFrameSource _source;
    private readonly TriggeredVisionEngine _inspection;
    private readonly ProductionTimingJournal _timing;
    private readonly LegacyFinsLoopbackClient _plc;
    private readonly ProductionStore _store;
    private readonly SimProductRuntime _product;
    private readonly bool _frameFirst;

    public LegacySt05Capture(string masterFolder, ProductionStore store,
        SimProductRuntime product, LegacyFinsLoopbackClient plc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _product = product ?? throw new ArgumentNullException(nameof(product));
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        var imageRoot = Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_ROOT");
        if (string.IsNullOrWhiteSpace(imageRoot))
            throw new InvalidOperationException("Legacy ST05 requires actual operator PNG frame input.");
        var ordering = Environment.GetEnvironmentVariable("AOI_SIM_ST05_ORDER");
        _frameFirst = ordering switch
        {
            null or "" or "REQUEST_FIRST" => false,
            "FRAME_FIRST" => true,
            _ => throw new InvalidOperationException("Unknown ST05 PLC-vs-Frame ordering.")
        };
        _source = new PngFileFrameSource(imageRoot,
            flatLab: Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_LAYOUT") == "FLAT");
        _captures = new CaptureHandshakeCoordinator(
            Path.Combine(masterFolder, "capture-handshake.sqlite"));
        _inspection = new TriggeredVisionEngine(
            new VisionAssetLedger(Path.Combine(masterFolder, "vision")),
            new OpenCvInspectionProcessor());
        _timing = new ProductionTimingJournal(masterFolder, "master");
    }

    public async Task HandleCaptureAsync(ushort token, WorkpieceIdentity item,
        CancellationToken ct)
    {
        if (token == 0 || item is null)
            throw new ArgumentException("ST05 event must carry a registered token and workpiece.");
        _product.VerifyWorkpiece(item);
        if (_captures.Latest(item.WorkpieceId, PhysicalDevice) is not null)
            throw new InvalidDataException("Duplicate ST05 sensor event for one physical attempt.");

        Guid captureId = Guid.NewGuid();
        var expected = new AgentCaptureTicket(item, captureId, 1, PhysicalDevice,
            DateTimeOffset.UtcNow.AddSeconds(10), IsSimulation: true);
        var frameTicket = new VisionCaptureTicket(item, PhysicalDevice, captureId, 1);
        _captures.ExpectCapture(expected);
        FrameRead? ready = null;
        bool validated = false;

        async Task ReadFrameAsync()
        {
            ready = await _source.ReadAsync(frameTicket,
                LegacyPlcTiming.HistoricalReference.AreaCameraDeadline, ct);
            if (ready.Status != ChannelOutcome.Ok || ready.Frame is null) return;
            if (!Cv2.ImEncode(".png", ready.Frame.Pixels, out var encoded) ||
                encoded.Length == 0) return;
            var sha = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
            var observed = new CaptureFrameEvidence(item.WorkpieceId, item.Epoch,
                item.ReleaseId, item.TraceId, captureId, 1, PhysicalDevice,
                ready.Frame.FrameId, sha, ready.Frame.FrameReadyUtc);
            if (!_captures.TryFrameAcquired(observed)) return;
            _timing.Append(new TimingObservation(item, TimingStage.FrameArrived,
                captureId, PhysicalDevice, AttemptNo: 1, FrameId: ready.Frame.FrameId,
                Status: "SIM_PNG_FRAME_ACQUIRED"));
            validated = _captures.FrameValidated(captureId, ready.Frame.FrameId);
            if (validated)
                _timing.Append(new TimingObservation(item, TimingStage.FrameReady,
                    captureId, PhysicalDevice, AttemptNo: 1, FrameId: ready.Frame.FrameId,
                    Status: "FRAME_VALIDATED"));
        }

        if (_frameFirst) await ReadFrameAsync();
        // D4300 is a private simulated PLC-side offer; the ACK at D1011 is
        // a genuine FINS/UDP datagram acknowledged by the legacy shadow server.
        await _plc.WriteAsync(LegacyPlcShadow.SimOfferCaptureAddress,
            [token, CameraDm], ct);
        ushort[] request = await _plc.ReadAsync(CameraDm, 1, ct);
        if (request.Length != 1 || request[0] != 1)
            throw new InvalidDataException("ST05 virtual PLC did not offer D1011=1.");
        _captures.ObservePlcRequest(captureId, "D1011");
        _timing.Append(new TimingObservation(item, TimingStage.PlcCaptureRequestObserved,
            captureId, PhysicalDevice, AttemptNo: 1, PlcDmAddress: "D1011",
            PlcValue: 1, Status: "REAL_FINS_READ_SIM_ONLY"));
        if (!_frameFirst) await ReadFrameAsync();

        if (!validated)
            _captures.FailCapture(captureId, ready?.Reason ?? "FRAME_VALIDATION_FAILED");
        var due = _captures.Get(captureId).PendingCamDone;
        if (due is not (2 or 3))
            throw new InvalidDataException("ST05 CamDone has no attributable capture evidence.");
        await _plc.WriteAsync(CameraDm, [due.Value], ct);
        var actual = await _plc.ReadAsync(CameraDm, 1, ct);
        if (actual.Length != 1 || actual[0] != due.Value)
            throw new InvalidDataException("ST05 CamDone FINS readback disagrees.");
        _captures.CamDoneConfirmed(captureId, due.Value);
        Console.WriteLine("SIM_ST05_REAL_FINS_CAM_DONE_PASS token=" + token +
            " workpiece=" + item.WorkpieceId + " capture=" + captureId +
            " D1011=" + due.Value + " ordering=" + (_frameFirst ? "FRAME_FIRST" : "REQUEST_FIRST"));

        if (due == 3)
        {
            ready?.Frame?.Dispose();
            _store.RecordWorkpieceHold(item, "ST05_FRAME_MISSING",
                "ST05 external frame missing or invalid; D1011=3 was FINS-acknowledged.");
            _timing.Append(new TimingObservation(item, TimingStage.CaptureFailed,
                captureId, PhysicalDevice, AttemptNo: 1,
                Status: "D1011_3_ACKED"));
            return;
        }

        var output = await _inspection.InspectAndPersistAsync(frameTicket, ready!,
            channel =>
            {
                var rules = _product.RuleFor(channel);
                ImageRegion? roi = null;
                if (_product.Published is not null)
                {
                    var published = RoiReleaseWorkflow.ReadPublishedRoi(
                        _product.Published, channel.ChannelId);
                    if (published is not null)
                    {
                        var source = ready!.Frame!.Pixels;
                        var p = published.Bounds.ToPixels(source.Cols, source.Rows);
                        roi = new ImageRegion(p.X, p.Y, p.Width, p.Height);
                    }
                }
                return InspectionSettings.OpenCvContour(
                    new ContourDefectRule(rules.BrightThreshold, 8), roi);
            }, ct);

        foreach (var channel in output.Channels)
        {
            if (channel.Observation is null)
            {
                _captures.FailCapture(captureId,
                    "INSPECTION_FAILED:" + channel.ChannelId + ":" + channel.Reason);
                _store.RecordWorkpieceHold(item, "ST05_INSPECTION_ERROR",
                    "ST05 algorithm failed; no fabricated result is permitted.");
                return;
            }
            _captures.InspectionCompleted(channel.Observation);
            _store.SaveLocalResult(channel.Observation);
            _timing.Append(TimingObservation.FromResult(item, channel.Observation));
        }
        if (!_captures.Get(captureId).ReadyForProductionResult)
            throw new InvalidDataException("ST05 cannot complete without validated FrameReady and FINS CamDone.");
        Console.WriteLine("SIM_ST05_TRIGGERED_CAPTURE_PASS workpiece=" +
            item.WorkpieceId + " CH06=" + output.Channels[0].Outcome);
    }
}
