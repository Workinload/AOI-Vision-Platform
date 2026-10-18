namespace AOI.Contracts;

/// <summary>
/// A bounded, read-only projection of A7's actually persisted Core timing evidence.
/// "DIRECT_OBSERVATION" is not interchangeable with legacy Core summary events.
/// UTC values across machines are NOT a shared monotonic clock.
/// </summary>
public sealed record SimTimingMilestone(
    long Sequence, string NodeId, string Origin, string Stage,
    DateTimeOffset OccurredUtc, Guid WorkpieceId, Guid TraceId,
    long Epoch, string ReleaseId, Guid? ProcessRunId,
    long? MonotonicTick, Guid? CaptureId, string? PhysicalDeviceId,
    string? ChannelId, int? AttemptNo, Guid? FrameId,
    Guid? OperationId, Guid? MessageId, string? PlcDmAddress,
    ushort? PlcValue, byte? FinsSid, string? Status);

public sealed record SimTimingFinding(
    string Code, string Severity, string Detail);

/// <summary>
/// Core -> operator read-only IPC; no reset, retry, quality override or PLC write.
/// A successfully queried timeline may still show a missing/unknown final PLC ACK.
/// This endpoint does not itself reconcile the separate A1/A7 final-output ledgers.
/// </summary>
public sealed record SimTimingReply(
    bool Accepted, string Error, Guid WorkpieceId, bool IsSimulation,
    bool Truncated, IReadOnlyList<SimTimingMilestone> Events,
    IReadOnlyList<SimTimingFinding> Findings)
{
    public void Validate()
    {
        if (!IsSimulation || Events is null || Findings is null ||
            Events.Count > 120 || Findings.Count > 120 || Error.Length > 128 ||
            (Accepted && (WorkpieceId == Guid.Empty || Error != "SIM_ONLY_READ_ONLY")) ||
            (!Accepted && (Events.Count != 0 || Findings.Count != 0)) ||
            Events.Any(e => e.WorkpieceId != WorkpieceId ||
                e.TraceId == Guid.Empty || e.Epoch < 0 ||
                string.IsNullOrWhiteSpace(e.NodeId) ||
                e.Origin is not ("DIRECT_OBSERVATION" or "CORE_WAL_LEGACY") ||
                string.IsNullOrWhiteSpace(e.Stage) || e.OccurredUtc == default) ||
            Findings.Any(f => string.IsNullOrWhiteSpace(f.Code) ||
                string.IsNullOrWhiteSpace(f.Severity)))
            throw new InvalidDataException("Invalid or unbounded SIM_ONLY timing evidence.");
    }
}
