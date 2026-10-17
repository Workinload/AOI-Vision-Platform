using System.Diagnostics;
using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime.Vision;

/// <summary>Per-channel settings resolved from one immutable A6 ReleaseId by A0.</summary>
public sealed record InspectionSettings(string AlgorithmId, ImageRegion? Roi = null,
    ContourDefectRule? Contour = null)
{
    public static InspectionSettings OpenCvContour(ContourDefectRule rule, ImageRegion? roi = null)
        => new("opencv.contour", roi, rule);
}

/// <summary>
/// A swappable image-only inspector. No PLC, camera SDK, mutable recipe or production DB.
/// An inspector must never retain the caller-owned Mat after Inspect returns.
/// </summary>
public interface IInspectionProcessor
{
    PixelInspection Inspect(ChannelDefinition channel, Mat pixels, InspectionSettings settings);
}

public sealed record PixelInspection(ChannelOutcome Outcome, string Reason,
    IReadOnlyList<DefectBox> Defects, double ElapsedMilliseconds, byte[]? AnnotationPng);

/// <summary>Simple OpenCV example. Users can replace this with their own algorithm/model.</summary>
public sealed class OpenCvInspectionProcessor : IInspectionProcessor
{
    public PixelInspection Inspect(ChannelDefinition channel, Mat pixels, InspectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(settings);
        var stamp = Stopwatch.GetTimestamp();
        if (settings.AlgorithmId != "opencv.contour" || settings.Contour is null)
            return new PixelInspection(ChannelOutcome.Uncertain, "ALGORITHM_NOT_CONFIGURED",
                Array.Empty<DefectBox>(), Stopwatch.GetElapsedTime(stamp).TotalMilliseconds, null);

        var inspected = ContourDefectDetector.Inspect(pixels, settings.Contour, settings.Roi);
        // Annotation is a bounded optional UI resource, never the immutable source evidence.
        byte[]? overlayPng = null;
        if ((long)pixels.Rows * pixels.Cols <= 8_000_000)
        {
            using var annotated = ContourDefectDetector.Annotate(pixels, inspected);
            if (Cv2.ImEncode(".png", annotated, out var encoded) && encoded.Length <= 16 * 1024 * 1024)
                overlayPng = encoded;
        }
        return new PixelInspection(inspected.Outcome, inspected.Reason,
            inspected.Boxes.ToArray(), Stopwatch.GetElapsedTime(stamp).TotalMilliseconds, overlayPng);
    }
}

public sealed record InspectionChannelOutput(
    string ChannelId, ChannelOutcome Outcome, string Reason,
    ChannelResult? Observation, IReadOnlyList<DefectBox> Defects,
    double AlgorithmElapsedMilliseconds, byte[]? AnnotationPng);

public sealed record PhysicalCaptureOutput(CaptureTicket Ticket, Guid? RawFrameId,
    DateTimeOffset? FrameReadyUtc, long? FrameReadyMonotonicTimestamp,
    IReadOnlyList<InspectionChannelOutput> Channels)
{
    public int PersistedResults => Channels.Count(c => c.Observation is not null);
    public bool FrameAcquired => RawFrameId is not null;
}

/// <summary>
/// Runs one physical camera event (area=one result, interleaved line=two results).
/// Call ReadAsync independently so A0 can write FINS CamDone after FrameReady,
/// then run InspectAndPersist separately, never before external station trigger.
/// </summary>
public sealed class TriggeredVisionEngine
{
    private readonly VisionAssetLedger _ledger;
    private readonly IInspectionProcessor _processor;
    private readonly SemaphoreSlim _processingSlots;

    public TriggeredVisionEngine(VisionAssetLedger ledger, IInspectionProcessor processor,
        int maxParallelInspections = 2)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        if (maxParallelInspections is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maxParallelInspections));
        _processingSlots = new SemaphoreSlim(maxParallelInspections, maxParallelInspections);
    }

    /// <summary>
    /// Nonblocking to the PLC event/ack loop. Acquired frame ownership is retained and
    /// disposed by the inspector; cancellation while queued disposes it without a result.
    /// Share one engine instance per Core to bound concurrent native inspections.
    /// </summary>
    public async Task<PhysicalCaptureOutput> InspectAndPersistAsync(CaptureTicket ticket,
        FrameRead read, Func<ChannelDefinition, InspectionSettings> settingsFor,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _processingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            read.Frame?.Dispose();
            throw;
        }
        try
        {
            return await Task.Run(() => InspectAndPersist(ticket, read, settingsFor))
                .ConfigureAwait(false);
        }
        finally { _processingSlots.Release(); }
    }

    /// <summary>Standalone convenience path. Host must use the split ReadAsync/InspectAndPersist
    /// path when a PLC CamDone acknowledgment must occur between acquisition and inspection.</summary>
    public async Task<PhysicalCaptureOutput> CaptureAndInspectAsync(CaptureTicket ticket,
        IFrameSource source, Func<ChannelDefinition, InspectionSettings> settingsFor,
        TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var read = await source.ReadAsync(ticket, deadline, cancellationToken).ConfigureAwait(false);
        return await InspectAndPersistAsync(ticket, read, settingsFor, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes FrameRead.Frame and always disposes its native pixels. Failure is scoped to
    /// this exact ticket, never throws through Core for ordinary missing/malformed images.
    /// Caller sends CamDone based on FrameRead, NOT based on an already computed result.
    /// </summary>
    public PhysicalCaptureOutput InspectAndPersist(CaptureTicket ticket, FrameRead read,
        Func<ChannelDefinition, InspectionSettings> settingsFor)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.Validate();
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(settingsFor);
        var channels = ticket.Channels;

        PhysicalCaptureOutput Failure(ChannelOutcome outcome, string reason,
            Guid? frameId = null, DateTimeOffset? utc = null, long? ticks = null) =>
            new(ticket, frameId, utc, ticks, channels.Select(c =>
                new InspectionChannelOutput(c.ChannelId, outcome, reason, null,
                    Array.Empty<DefectBox>(), 0, null)).ToArray());

        using var frame = read.Frame;
        if (frame is null || read.Status != ChannelOutcome.Ok)
            return Failure(read.Status == ChannelOutcome.Missing ? ChannelOutcome.Missing : ChannelOutcome.Error,
                read.Reason);
        if (frame.Ticket != ticket)
            return Failure(ChannelOutcome.Error, "FRAME_TICKET_MISMATCH");
        var frameId = frame.FrameId;
        var readyUtc = frame.FrameReadyUtc;
        var monotonic = frame.MonotonicTimestamp;
        try
        {
            if (channels.Count == 1)
            {
                var channel = channels[0];
                var inspect = _processor.Inspect(channel, frame.Pixels, settingsFor(channel));
                if (inspect.Outcome is ChannelOutcome.Error or ChannelOutcome.Missing)
                    return Failure(inspect.Outcome, inspect.Reason, frameId, readyUtc, monotonic);
                var observed = _ledger.CaptureAreaDetected(ticket.Workpiece, channel.ChannelId,
                    frame.Pixels, inspect.Outcome, ticket.AttemptNo, ticket.CaptureId, frameId);
                observed.ValidateFor(ticket.Workpiece);
                return new(ticket, frameId, readyUtc, monotonic, [
                    new(channel.ChannelId, inspect.Outcome, inspect.Reason, observed,
                        inspect.Defects, inspect.ElapsedMilliseconds, inspect.AnnotationPng)
                ]);
            }
            if (channels.Count != 2 || channels.Any(c => c.SharedFrameGroup != ticket.PhysicalDeviceId))
                return Failure(ChannelOutcome.Error, "INVALID_PHYSICAL_LINE_GROUP", frameId, readyUtc, monotonic);

            // Split one physical parent, preserving FrameId and CaptureId for both children.
            using var even = VisionPixelOps.ExtractInterleavedRows(frame.Pixels, 0);
            using var odd = VisionPixelOps.ExtractInterleavedRows(frame.Pixels, 1);
            var inspections = new[]
            {
                _processor.Inspect(channels[0], even, settingsFor(channels[0])),
                _processor.Inspect(channels[1], odd, settingsFor(channels[1]))
            };
            if (inspections.Any(x => x.Outcome is ChannelOutcome.Error or ChannelOutcome.Missing))
                return new(ticket, frameId, readyUtc, monotonic,
                    channels.Select((c, i) => new InspectionChannelOutput(c.ChannelId,
                        inspections[i].Outcome, inspections[i].Reason, null,
                        inspections[i].Defects, inspections[i].ElapsedMilliseconds,
                        inspections[i].AnnotationPng)).ToArray());
            var outcomes = channels.Select((c, i) => (c.ChannelId, inspections[i].Outcome))
                .ToDictionary(x => x.ChannelId, x => x.Outcome, StringComparer.Ordinal);
            var persisted = _ledger.CaptureLineInterleaved(ticket.Workpiece,
                ticket.PhysicalDeviceId, frame.Pixels, ticket.AttemptNo, outcomes,
                ticket.CaptureId, frameId);
            return new(ticket, frameId, readyUtc, monotonic,
                channels.Select((c, i) =>
                {
                    var observed = persisted.Single(x => x.ChannelId == c.ChannelId);
                    observed.ValidateFor(ticket.Workpiece);
                    return new InspectionChannelOutput(c.ChannelId, observed.Outcome,
                        inspections[i].Reason, observed, inspections[i].Defects,
                        inspections[i].ElapsedMilliseconds, inspections[i].AnnotationPng);
                }).ToArray());
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or
            OutOfMemoryException or StackOverflowException))
        {
            return Failure(ChannelOutcome.Error, "INSPECTION_OR_ASSET_ERROR:" + ex.GetType().Name,
                frameId, readyUtc, monotonic);
        }
    }
}
