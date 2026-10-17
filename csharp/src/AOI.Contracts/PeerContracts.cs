using AOI.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AOI.Contracts;

public static class WireFormat
{
    public const int ProtocolVersion = 1;
    public static JsonSerializerOptions Json { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidDataException($"Could not parse {typeof(T).Name}");
}

public sealed record PeerEnvelope(
    int ProtocolVersion, Guid MessageId, string SenderNodeId, string ReceiverNodeId,
    long Epoch, Guid TraceId, Guid WorkpieceId, string? StationId, string? ChannelId,
    string SolutionReleaseId, long Sequence, DateTimeOffset CreatedAtUtc,
    string PayloadType, string PayloadHash, string PayloadJson)
{
    public static PeerEnvelope Create<T>(string sender, string receiver, long seq,
        WorkpieceIdentity workpiece, string type, T payload, string? channelId = null)
    {
        workpiece.Validate();
        var body = WireFormat.Serialize(payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return new(WireFormat.ProtocolVersion, Guid.NewGuid(), sender, receiver,
            workpiece.Epoch, workpiece.TraceId, workpiece.WorkpieceId,
            channelId is null ? null : ChannelCatalog.Require(channelId).StationId,
            channelId, workpiece.ReleaseId, seq, DateTimeOffset.UtcNow, type, hash, body);
    }
    public void Validate()
    {
        if (ProtocolVersion != WireFormat.ProtocolVersion || MessageId == Guid.Empty ||
            WorkpieceId == Guid.Empty || TraceId == Guid.Empty || Epoch < 0 || Sequence < 1 ||
            string.IsNullOrWhiteSpace(SenderNodeId) || string.IsNullOrWhiteSpace(ReceiverNodeId) ||
            string.IsNullOrWhiteSpace(SolutionReleaseId) ||
            !string.Equals(PayloadHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PayloadJson))).ToLowerInvariant(),
                StringComparison.Ordinal))
            throw new InvalidDataException("Invalid envelope, version or payload digest.");
    }
}

public sealed record PersistedAck(Guid MessageId, string SenderNodeId, string PayloadHash,
    DateTimeOffset PersistedAtUtc);
public sealed record PeerPacket(string Kind, PeerEnvelope? Envelope, PersistedAck? Ack);

public sealed record ChannelSnapshot(string ChannelId, string StationId, string PhysicalDeviceId,
    string Status, Guid? WorkpieceId, bool IsSimulated, DateTimeOffset? LastUpdatedUtc);
/// <summary>Bounded production history in the read-only Core IPC; never a PLC command.</summary>
public sealed record WorkpieceProductionRow(
    Guid WorkpieceId, string Barcode, long Epoch, string ReleaseId,
    int CapturedChannels, FinalDisposition? Final, DateTimeOffset CreatedUtc);

public sealed record ProductionOverview(
    long Total, long Good, long Reject, long Unfinished,
    IReadOnlyList<WorkpieceProductionRow> Recent)
{
    public void Validate()
    {
        if (Total < 0 || Good < 0 || Reject < 0 || Unfinished < 0 ||
            Good + Reject + Unfinished != Total ||
            Recent is null || Recent.Count > 20 ||
            Recent.Select(r => r.WorkpieceId).Distinct().Count() != Recent.Count ||
            Recent.Any(r => r.WorkpieceId == Guid.Empty || r.Epoch < 0 ||
                r.CapturedChannels is < 0 or > 11 ||
                r.Final is FinalDisposition.Hold ||
                (r.Final is not null && r.CapturedChannels == 0)))
            throw new InvalidDataException("Unsafe or inconsistent production history snapshot.");
    }
}

/// <summary>Engineering evidence, NOT live field-read PLC data or a write API.</summary>
public sealed record PlcEngineeringSignal(
    string SignalId, string DmAddress, string StationId, string HostRole,
    string Direction, string DataType, string Description, string ValueLegend,
    string ValidationState, string ConflictGroup, bool ProductionWriteEnabled);

/// <summary>Field sign-off candidates; 'OPEN' never implies production authorization.</summary>
public sealed record PlcFieldGate(
    string GateId, string Topic, string Status, string RequiredEvidence,
    bool ProductionWriteAllowed);

/// <summary>Immutable engineering mapping supplied by A2; no live PLC claims.</summary>
public sealed record PlcEngineeringSnapshot(
    IReadOnlyList<PlcEngineeringSignal> Signals, IReadOnlyList<PlcFieldGate> Gates,
    bool IsFieldLive, DateTimeOffset CatalogCapturedUtc)
{
    public void Validate()
    {
        if (Signals is null || Signals.Count != 45 || Gates is null || Gates.Count != 14 ||
            IsFieldLive || CatalogCapturedUtc == default ||
            Signals.Select(s => s.SignalId).Distinct(StringComparer.Ordinal).Count() != 45 ||
            Gates.Select(g => g.GateId).Distinct(StringComparer.Ordinal).Count() != 14 ||
            Gates.Count(g => g.Status == "BLOCKED") != 7 ||
            Gates.Count(g => g.Status == "OPEN") != 7 ||
            Signals.Any(s => s.ProductionWriteEnabled ||
                string.IsNullOrWhiteSpace(s.SignalId) ||
                string.IsNullOrWhiteSpace(s.DmAddress) ||
                string.IsNullOrWhiteSpace(s.ValidationState) ||
                string.IsNullOrWhiteSpace(s.Description)) ||
            Gates.Any(g => g.ProductionWriteAllowed ||
                string.IsNullOrWhiteSpace(g.Topic) ||
                string.IsNullOrWhiteSpace(g.RequiredEvidence)))
            throw new InvalidDataException(
                "Unsafe or incomplete static PLC engineering registry. Field verification is required.");
    }
}

/// <summary>Durable SIM_ONLY fail-closed workpiece hold, never an operator clear command.</summary>
public sealed record WorkpieceHoldRecord(
    Guid WorkpieceId, string ReasonCode, string Detail, DateTimeOffset RaisedUtc);

public sealed record WorkpieceHoldOverview(long ActiveCount,
    IReadOnlyList<WorkpieceHoldRecord> Recent)
{
    public void Validate()
    {
        if (ActiveCount < 0 || Recent is null || Recent.Count > 20 ||
            ActiveCount < Recent.Count ||
            Recent.Select(x => x.WorkpieceId).Distinct().Count() != Recent.Count ||
            Recent.Any(x => x.WorkpieceId == Guid.Empty ||
                string.IsNullOrWhiteSpace(x.ReasonCode) || x.ReasonCode.Length > 64 ||
                string.IsNullOrWhiteSpace(x.Detail) || x.Detail.Length > 512 ||
                x.RaisedUtc == default))
            throw new InvalidDataException("Invalid durable hold overview.");
    }
}

/// <summary>Offline SIM_ONLY product release, independently verified by each Core.</summary>
public sealed record SimActiveStation(
    string StationId, IReadOnlyList<string> ChannelIds,
    byte BrightThreshold, double MinimumBrightFraction);

public sealed record SimActiveProductSnapshot(
    string SolutionId, string ReleaseId, string ContentSha256, long Epoch,
    bool IsSimulation, IReadOnlyList<SimActiveStation> Stations)
{
    public void Validate()
    {
        if (!IsSimulation || Epoch < 1 ||
            string.IsNullOrWhiteSpace(SolutionId) ||
            string.IsNullOrEmpty(ContentSha256) || ContentSha256.Length != 64 ||
            !ContentSha256.All(Uri.IsHexDigit) ||
            ReleaseId != "SIMREL-" + ContentSha256[..32] ||
            Stations is null ||
            Stations.Select(s => s.StationId).Distinct(StringComparer.Ordinal).Count() != Stations.Count ||
            Stations.Any(s => s.ChannelIds is null ||
                s.ChannelIds.Any(c => !ChannelCatalog.All.Any(def =>
                    def.StationId == s.StationId && def.ChannelId == c)) ||
                !double.IsFinite(s.MinimumBrightFraction) ||
                s.MinimumBrightFraction is < 0 or > 1) ||
            !Stations.SelectMany(s => s.ChannelIds)
                .OrderBy(c => c, StringComparer.Ordinal)
                .SequenceEqual(ChannelCatalog.All.Select(c => c.ChannelId)
                    .OrderBy(c => c, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("SIM_ONLY product activation proof is incomplete or unsafe.");
    }
}

/// <summary>Offline draft; never automatically applies to camera or released product.</summary>
public sealed record RoiDraftEdit(
    string ChannelId, double X, double Y, double Width, double Height,
    long ExpectedRevision, string Actor)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ChannelId) || string.IsNullOrWhiteSpace(Actor) ||
            Actor.Length > 64 || ExpectedRevision is < 0 or >= long.MaxValue ||
            new[] { X, Y, Width, Height }.Any(v => !double.IsFinite(v)) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            X > 1 || Y > 1 || X + Width > 1 || Y + Height > 1)
            throw new InvalidDataException("Invalid bounded normalized SIM_ONLY ROI draft.");
        _ = ChannelCatalog.Require(ChannelId);
    }
}
public sealed record RoiDraftState(
    string ChannelId, double X, double Y, double Width, double Height,
    long Revision, string Actor, DateTimeOffset EditedUtc)
{
    public void Validate()
    {
        new RoiDraftEdit(ChannelId, X, Y, Width, Height, Revision - 1, Actor).Validate();
        if (Revision < 1 || EditedUtc == default)
            throw new InvalidDataException("Invalid saved SIM_ONLY ROI revision.");
    }
}
public sealed record RoiDraftSaveRequest(RoiDraftEdit Edit, string AccessToken);
public sealed record RoiDraftSaveReply(bool Accepted, string Error, RoiDraftState? Saved);

/// <summary>Read-only SIM_FULL station motion + per-item A1 progress, not physical PLC state.</summary>
public sealed record SimConveyorItem(
    Guid WorkpieceId, ushort Token, string Barcode, string StationId,
    bool SensorTriggered, int CapturedChannels, int CompletedStations,
    string Phase, bool ReadyForExit);

public sealed record SimConveyorOverview(
    long Tick, int LoadedCount, int ExitedCount,
    IReadOnlyList<SimConveyorItem> Active,
    IReadOnlyList<string> StuckSensors)
{
    public void Validate()
    {
        if (Tick < 0 || LoadedCount < 0 || ExitedCount < 0 || ExitedCount > LoadedCount ||
            Active is null || StuckSensors is null || Active.Count > 8 ||
            LoadedCount - ExitedCount < Active.Count ||
            Active.Select(x => x.Token).Distinct().Count() != Active.Count ||
            Active.Select(x => x.WorkpieceId).Distinct().Count() != Active.Count ||
            Active.Any(x => x.WorkpieceId == Guid.Empty || x.Token == 0 ||
                string.IsNullOrWhiteSpace(x.Barcode) ||
                string.IsNullOrWhiteSpace(x.Phase) ||
                x.CapturedChannels is < 0 or > 11 ||
                x.CompletedStations is < 0 or > 7 ||
                !new[] { "LOADING", "ST03", "ST04", "ST05", "ST06", "ST09", "ST10", "ST11" }
                    .Contains(x.StationId, StringComparer.Ordinal)) ||
            StuckSensors.Distinct(StringComparer.Ordinal).Count() != StuckSensors.Count ||
            StuckSensors.Any(x => !new[] { "LOADING", "ST03", "ST04", "ST05",
                 "ST06", "ST09", "ST10", "ST11" }.Contains(x, StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid SIM_ONLY mechanical/progress IPC.");
    }
}

/// <summary>Read-only operator report over actual Master/Slave SQLite WAL, SIM_ONLY.</summary>
public sealed record SimReportPeriod(
    string Label, DateTimeOffset FromUtc, DateTimeOffset UntilUtc,
    long Total, long Good, long Ng, long Hold, long InProgress,
    double? GoodRate)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Label) || FromUtc >= UntilUtc ||
            Total < 0 || Good < 0 || Ng < 0 || Hold < 0 || InProgress < 0 ||
            Total != Good + Ng + Hold + InProgress)
            throw new InvalidDataException("Invalid production period totals.");
        double? expected = Good + Ng == 0 ? null : (double)Good / (Good + Ng);
        if (GoodRate.HasValue != expected.HasValue ||
            (GoodRate.HasValue && (!double.IsFinite(GoodRate.Value) ||
                Math.Abs(GoodRate.Value - expected!.Value) > 1e-9)))
            throw new InvalidDataException("Invalid report quality rate.");
    }
}

public sealed record SimReportWorkpiece(
    Guid WorkpieceId, string Barcode, string ReleaseId,
    DateTimeOffset RegisteredUtc, string Disposition,
    int CapturedChannels, string? HoldReason)
{
    public void Validate()
    {
        if (WorkpieceId == Guid.Empty || string.IsNullOrWhiteSpace(ReleaseId) ||
            Disposition is not ("Good" or "Ng" or "Hold" or "InProgress") ||
            CapturedChannels is < 0 or > 11 || RegisteredUtc == default)
            throw new InvalidDataException("Invalid report trace identity or verdict.");
    }
}

public sealed record SimProductionReports(
    bool IsSimulation, DateTimeOffset UpdatedUtc,
    SimReportPeriod Today, SimReportPeriod ThisWeek,
    long AllTimeMatches, IReadOnlyList<SimReportWorkpiece> Recent)
{
    public void Validate()
    {
        if (!IsSimulation || UpdatedUtc == default || Today is null ||
            ThisWeek is null || Recent is null || Recent.Count > 20 ||
            AllTimeMatches < Recent.Count ||
            Recent.Select(r => r.WorkpieceId).Distinct().Count() != Recent.Count)
            throw new InvalidDataException("Invalid SIM_ONLY report snapshot.");
        Today.Validate();
        ThisWeek.Validate();
        foreach (var item in Recent) item.Validate();
    }
}

public sealed record SimProductionReportReply(bool Accepted, string Message, SimProductionReports? Reports);

public sealed record MachineSnapshot(string NodeId, MachineRole Role, string MachineState,
    WorkpieceIdentity? LatestWorkpiece, FinalDecision? LatestFinal,
    IReadOnlyList<ChannelSnapshot> Channels, DateTimeOffset UpdatedUtc,
    bool RealHardwareWritesEnabled, string SimulationMode)
{
    public ProductionOverview? Production { get; init; }
    public PlcEngineeringSnapshot? PlcEngineering { get; init; }
    public WorkpieceHoldOverview? Holds { get; init; }
    public SimActiveProductSnapshot? ActiveProduct { get; init; }
    public IReadOnlyList<RoiDraftState>? RoiDrafts { get; init; }
    public SimConveyorOverview? Conveyor { get; init; }
}
