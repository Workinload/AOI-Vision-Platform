using AOI.Domain;

namespace AOI.Runtime.Agent01;

/// <summary>
/// Immutable correlation allocated by the station scheduler BEFORE an external request/frame
/// is processed. A barcode or bare PLC value is never sufficient to identify a frame.
/// One physical line-scan ticket covers two logical channels with the same capture ID.
/// </summary>
public sealed record CaptureTicket(
    WorkpieceIdentity Workpiece,
    Guid CaptureId,
    int AttemptNo,
    string PhysicalDeviceId,
    DateTimeOffset ExpiresAtUtc,
    bool IsSimulation)
{
    public IReadOnlyList<string> ChannelIds => ChannelCatalog.All
        .Where(c => c.PhysicalDeviceId == PhysicalDeviceId)
        .Select(c => c.ChannelId).Order(StringComparer.Ordinal).ToArray();

    public string StationId => RequireDefinition().StationId;
    public string PlcAddress => RequireDefinition().PlcSignalAddress;

    private ChannelDefinition RequireDefinition() =>
        ChannelCatalog.All.FirstOrDefault(c => c.PhysicalDeviceId == PhysicalDeviceId)
        ?? throw new ArgumentException("Unknown capture device " + PhysicalDeviceId);

    public void Validate()
    {
        if (Workpiece is null) throw new ArgumentException("Workpiece identity required.");
        Workpiece.Validate();
        if (CaptureId == Guid.Empty || AttemptNo < 1 ||
            ExpiresAtUtc <= Workpiece.CreatedAtUtc || ChannelIds.Count is < 1 or > 2)
            throw new ArgumentException("Invalid physical capture ticket.");
        var channels = ChannelCatalog.All.Where(c => c.PhysicalDeviceId == PhysicalDeviceId).ToArray();
        if (channels.Any(c => c.StationId != StationId ||
                              c.PlcSignalAddress != PlcAddress))
            throw new InvalidDataException("One physical device must use one PLC request address.");
    }
}

/// <summary>
/// A3's immutable, already-acquired frame metadata, not an instruction to software-trigger
/// a camera. For area cameras SourceFrameId is the final frame; for line cameras it is
/// the shared physical parent. Image validity is confirmed separately by FrameValidated.
/// </summary>
public sealed record CaptureFrameEvidence(
    Guid WorkpieceId,
    long Epoch,
    string ReleaseId,
    Guid TraceId,
    Guid CaptureId,
    int AttemptNo,
    string PhysicalDeviceId,
    Guid SourceFrameId,
    string SourceFrameSha256,
    DateTimeOffset FrameUtc)
{
    public void ValidateFor(CaptureTicket ticket)
    {
        if (WorkpieceId != ticket.Workpiece.WorkpieceId ||
            Epoch != ticket.Workpiece.Epoch || ReleaseId != ticket.Workpiece.ReleaseId ||
            TraceId != ticket.Workpiece.TraceId || CaptureId != ticket.CaptureId ||
            AttemptNo != ticket.AttemptNo || PhysicalDeviceId != ticket.PhysicalDeviceId ||
            SourceFrameId == Guid.Empty || FrameUtc == default ||
            SourceFrameSha256 is null || SourceFrameSha256.Length != 64 ||
            !SourceFrameSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Frame cannot be associated with this physical capture ticket.");
    }
}

public enum CaptureHandshakeEventKind
{
    CaptureExpected,
    PlcRequestObserved,
    FrameAcquired,
    FrameValidated,
    CamDoneConfirmed,
    InspectionCompleted,
    CaptureFailed,
    FrameRejected
}

public sealed record CaptureHandshakeEvent(
    long Sequence,
    CaptureHandshakeEventKind Kind,
    Guid CaptureId,
    DateTimeOffset EventUtc,
    CaptureTicket? Ticket = null,
    string? RequestAddress = null,
    CaptureFrameEvidence? Frame = null,
    Guid? ValidatedFrameId = null,
    ushort? CamDoneValue = null,
    ChannelResult? Inspection = null,
    string? FailureReason = null);

/// <summary>
/// Read-only Core projection. PendingCamDone=2/3 is only a command INTENT.
/// A2 must perform the actual FINS/UDP write and confirm its successful response
/// before the Host calls CamDoneConfirmed. No PLC writes happen inside A1.
/// </summary>
public sealed record CaptureHandshakeProgress(
    CaptureTicket Ticket,
    string Phase,
    bool PlcRequestObserved,
    bool FrameAcquired,
    bool FrameValidated,
    ushort? PendingCamDone,
    ushort? ConfirmedCamDone,
    IReadOnlyList<string> InspectionChannels,
    string? FailureReason,
    long LastSequence)
{
    public IReadOnlyDictionary<string, ChannelOutcome> Outcomes { get; init; } =
        new Dictionary<string, ChannelOutcome>(StringComparer.Ordinal);

    public int RejectedFrameCount { get; init; }
    public string? LastRejectedFrameReason { get; init; }

    public bool HasAllInspections =>
        Ticket.ChannelIds.All(ch => InspectionChannels.Contains(ch, StringComparer.Ordinal));
    public bool ReadyForProductionResult =>
        ConfirmedCamDone == 2 && HasAllInspections && FailureReason is null &&
        Outcomes.Values.All(o => o is ChannelOutcome.Ok or ChannelOutcome.Ng);
}

public interface ICaptureHandshakeCoordinator
{
    bool ExpectCapture(CaptureTicket ticket);
    bool ObservePlcRequest(Guid captureId, string requestAddress);
    bool FrameAcquired(CaptureFrameEvidence frame);
    bool TryFrameAcquired(CaptureFrameEvidence frame);
    bool FrameValidated(Guid captureId, Guid frameId);
    bool CamDoneConfirmed(Guid captureId, ushort value);
    bool InspectionCompleted(ChannelResult result);
    bool FailCapture(Guid captureId, string reason);
    IReadOnlyList<CaptureHandshakeProgress> ExpireDue(DateTimeOffset nowUtc);
    CaptureHandshakeProgress Get(Guid captureId);
    CaptureHandshakeProgress? Latest(Guid workpieceId, string physicalDeviceId);
    IReadOnlyList<CaptureHandshakeProgress> PendingCamDone();
    IReadOnlyList<ChannelResult> CompletedResults(Guid captureId);
    IReadOnlyList<CaptureHandshakeEvent> EventsFor(Guid captureId);
    string ExportJson();
}
