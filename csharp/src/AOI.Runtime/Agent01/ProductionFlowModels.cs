using AOI.Domain;

namespace AOI.Runtime.Agent01;

/// <summary>Internal A1 event kinds; not an A0 wire DTO or physical PLC command.</summary>
public enum ProductionFlowEventKind
{
    Registered,
    StationEntered,
    ChannelCaptured,
    StationCompleted,
    EndBarcodeScanned,
    FinalPersisted,
    EjectionConfirmed
}

/// <summary>Immutable, sequenced production event stored in the A1 SQLite WAL journal.</summary>
public sealed record ProductionFlowEvent(
    long Sequence,
    ProductionFlowEventKind Kind,
    Guid WorkpieceId,
    DateTimeOffset OccurredUtc,
    WorkpieceIdentity? Workpiece = null,
    string? StationId = null,
    ChannelResult? Result = null,
    string? EndBarcode = null,
    FinalDecision? Final = null);

public sealed record StationProductionProgress(
    string StationId,
    DateTimeOffset? EnteredUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<string> ExpectedChannels,
    IReadOnlyList<string> CapturedChannels)
{
    public bool HasEntered => EnteredUtc is not null;
    public bool IsCompleted => CompletedUtc is not null;
    public int CapturedCount => CapturedChannels.Count;
    public int ExpectedCount => ExpectedChannels.Count;
}

/// <summary>
/// Operator-ready projection of one physical workpiece. The barcode is descriptive,
/// never the dictionary key. Every field is backed by replayed production events.
/// </summary>
public sealed record WorkpieceProductionProgress(
    WorkpieceIdentity Workpiece,
    string Phase,
    string? CurrentStationId,
    int CompletedStationCount,
    int StationCount,
    int CapturedChannelCount,
    int ExpectedChannelCount,
    IReadOnlyList<string> CapturedChannels,
    IReadOnlyList<StationProductionProgress> Stations,
    bool? EndBarcodeMatches,
    bool FinalReady,
    FinalDecision? Final,
    bool Ejected,
    long LastSequence);

/// <summary>
/// A0 adapter: after persisting the final in ProductionStore, call FinalPersisted,
/// and only after a token-scoped virtual PLC response call EjectionConfirmed.
/// No method in this module controls a PLC or a real camera.
/// </summary>
public interface IProductionFlowCoordinator
{
    bool Register(WorkpieceIdentity identity);
    bool EnterStation(Guid workpieceId, string stationId);
    bool RecordChannel(ChannelResult result);
    bool CompleteStation(Guid workpieceId, string stationId);
    bool ObserveEndBarcode(Guid workpieceId, string barcode);
    FinalDecision? EvaluateFinal(Guid workpieceId);
    bool FinalPersisted(Guid workpieceId, FinalDecision final);
    bool EjectionConfirmed(Guid workpieceId);
    WorkpieceProductionProgress Get(Guid workpieceId);
    IReadOnlyList<WorkpieceProductionProgress> GetAll();
    WorkpieceProductionProgress Replay(Guid workpieceId, long atOrBeforeSequence);
    IReadOnlyList<ProductionFlowEvent> EventsFor(Guid workpieceId);
    string ExportJson();
}
