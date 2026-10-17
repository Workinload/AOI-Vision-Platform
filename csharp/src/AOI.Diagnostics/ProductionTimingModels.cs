using AOI.Domain;

namespace AOI.Diagnostics;

/// <summary>
/// Stages record what was ACTUALLY observed at an Adapter/Core boundary.
/// In particular a sent write is NOT an acknowledged write, and a frame
/// is NOT FrameReady/CamDone/InspectionCompleted. No stage drives hardware.
/// </summary>
public enum TimingStage
{
    PlcCaptureRequestObserved,
    HardwareTriggerObserved,
    FrameArrived,
    FrameReady,
    CamDoneSent,
    CamDoneAcknowledged,
    InspectionStarted,
    InspectionCompleted,
    PeerEnqueued,
    PeerInboxPersisted,
    PeerAckPersisted,
    FinalDecisionPersisted,
    FinsReadSent,
    FinsReadCompleted,
    FinsWriteSent,
    FinsWriteAcknowledged,
    FinalPlcWriteSent,
    FinalPlcWriteAcknowledged,
    CaptureFailed,
    CaptureTimedOut,
    CoreEventPersisted
}

/// <summary>
/// Call with existing immutable WorkpieceIdentity at the exact point an event occurs.
/// Use the same CaptureId for request-first AND frame-first arrival, even if the
/// request is observed later. FINS OperationId links one request to its response.
/// </summary>
public sealed record TimingObservation(
    WorkpieceIdentity Item, TimingStage Stage,
    Guid? CaptureId = null, string? PhysicalDeviceId = null,
    string? ChannelId = null, int? AttemptNo = null, Guid? FrameId = null,
    Guid? OperationId = null, Guid? MessageId = null,
    string? PlcDmAddress = null, ushort? PlcValue = null, byte? FinsSid = null,
    string? Status = null, string? Details = null)
{
    public static TimingObservation FromResult(WorkpieceIdentity item, ChannelResult result)
    {
        result.ValidateFor(item);
        return new TimingObservation(item, TimingStage.InspectionCompleted,
            result.CaptureId, result.PhysicalDeviceId, result.ChannelId,
            result.AttemptNo, result.FrameId,
            Status: result.Outcome.ToString().ToUpperInvariant(),
            Details: $"result={result.ResultId:D}; parentFrame={result.ParentFrameId?.ToString("D") ?? "none"}");
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Item);
        Item.Validate();
        if (!Enum.IsDefined(Stage) || Stage == TimingStage.CoreEventPersisted ||
            CaptureId == Guid.Empty || FrameId == Guid.Empty ||
            OperationId == Guid.Empty || MessageId == Guid.Empty ||
            AttemptNo is < 1 or > 10000 ||
            (PhysicalDeviceId?.Length ?? 0) > 64 ||
            (ChannelId?.Length ?? 0) > 32 ||
            (Status?.Length ?? 0) > 64 ||
            (Details?.Length ?? 0) > 512 ||
            (PlcDmAddress?.Length ?? 0) > 12)
            throw new ArgumentException("Invalid timing event identity or bounded fields.");

        bool captureStage = Stage is
            TimingStage.PlcCaptureRequestObserved or TimingStage.HardwareTriggerObserved or
            TimingStage.FrameArrived or TimingStage.FrameReady or
            TimingStage.CamDoneSent or TimingStage.CamDoneAcknowledged or
            TimingStage.InspectionStarted or TimingStage.InspectionCompleted or
            TimingStage.CaptureFailed or TimingStage.CaptureTimedOut;
        if (captureStage && (CaptureId is null || AttemptNo is null ||
            string.IsNullOrWhiteSpace(PhysicalDeviceId)))
            throw new ArgumentException("Capture timing requires CaptureId, AttemptNo, and physical device.");

        if (PhysicalDeviceId is not null &&
            !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == PhysicalDeviceId))
            throw new ArgumentException("Unknown physical device in timing observation.");
        if (ChannelId is not null)
        {
            var channel = ChannelCatalog.Require(ChannelId);
            if (channel.PhysicalDeviceId != PhysicalDeviceId)
                throw new ArgumentException("Channel belongs to another camera; cannot misbind timing event.");
        }

        if (Stage is TimingStage.FrameArrived or TimingStage.FrameReady or TimingStage.InspectionCompleted &&
            FrameId is null)
            throw new ArgumentException("Frame milestone requires actual FrameId.");

        bool isFinsStage = Stage is TimingStage.FinsReadSent or TimingStage.FinsReadCompleted or
            TimingStage.FinsWriteSent or TimingStage.FinsWriteAcknowledged or
            TimingStage.CamDoneSent or TimingStage.CamDoneAcknowledged or
            TimingStage.FinalPlcWriteSent or TimingStage.FinalPlcWriteAcknowledged;
        if (isFinsStage &&
            (OperationId is null || FinsSid is null || PlcValue is null ||
             string.IsNullOrWhiteSpace(PlcDmAddress)))
            throw new ArgumentException("FINS boundary requires OperationId, SID, DM address and value.");

        if (PlcDmAddress is not null &&
            (PlcDmAddress.Length < 2 || PlcDmAddress[0] != 'D' ||
             !ushort.TryParse(PlcDmAddress.AsSpan(1), out _)))
            throw new ArgumentException("PLC address must be a numbered D register; no implied field authorization.");
    }
}

/// <summary>
/// Immutable persisted event. Sequence is LOCAL to one node's timing WAL, while
/// Stopwatch tick order is meaningful only within the same ProcessRunId.
/// Cross-node UTC ordering must account for clock skew; not global monotonic time.
/// </summary>
public sealed record TimingEvent(
    long Sequence, Guid EventId, string NodeId, Guid? ProcessRunId,
    string Origin, DateTimeOffset Utc, long? MonotonicTick, long? TickFrequency,
    Guid WorkpieceId, Guid TraceId, long Epoch, string ReleaseId,
    TimingStage Stage, Guid? CaptureId, string? PhysicalDeviceId, string? ChannelId,
    int? AttemptNo, Guid? FrameId, Guid? OperationId, Guid? MessageId,
    string? PlcDmAddress, ushort? PlcValue, byte? FinsSid,
    string? Status, string? Details);

public sealed record TimingFilter(
    Guid WorkpieceId, Guid? CaptureId = null, string? PhysicalDeviceId = null,
    string? ChannelId = null, int? AttemptNo = null, bool IncludeExistingCoreEvents = true,
    int Limit = 2000)
{
    public void Validate()
    {
        if (WorkpieceId == Guid.Empty || CaptureId == Guid.Empty ||
            AttemptNo is < 1 or > 10000 || Limit is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(TimingFilter));
        if (PhysicalDeviceId is not null &&
            !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == PhysicalDeviceId))
            throw new ArgumentException("Unknown device.");
        if (ChannelId is not null && ChannelCatalog.Require(ChannelId).PhysicalDeviceId != PhysicalDeviceId &&
            PhysicalDeviceId is not null)
            throw new ArgumentException("Device/channel filter mismatch.");
    }
}

/// <summary>One ordered expected milestone, optionally with maximum time since prior milestone.</summary>
public sealed record TimingExpectation(
    TimingStage Stage, string? NodeId = null, string? PhysicalDeviceId = null,
    string? ChannelId = null, Guid? CaptureId = null, int? AttemptNo = null,
    string? PlcDmAddress = null, ushort? PlcValue = null,
    TimeSpan? MaxSincePrevious = null);

public sealed record TimingDifference(
    string Code, string Severity, int ExpectationIndex, TimingStage ExpectedStage,
    Guid? ActualEventId, string Detail);

public sealed record TimingComparison(
    Guid WorkpieceId, int ExpectedCount, int MatchedCount,
    TimingDifference[] Differences)
{
    public bool IsMatch => Differences.Length == 0 && MatchedCount == ExpectedCount;
}

public sealed record TimingTimeline(
    Guid WorkpieceId, DateTimeOffset QueriedUtc,
    TimingEvent[] Events, TimingDifference[] DiagnosticDifferences)
{
    public bool Truncated { get; init; }
}
