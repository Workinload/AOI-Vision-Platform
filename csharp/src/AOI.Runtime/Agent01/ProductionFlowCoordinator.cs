using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;

namespace AOI.Runtime.Agent01;

/// <summary>
/// Single-Master-writer, event-sourced production progress for the existing SIM_ONLY
/// conveyor. A2 supplies station/sensor events, A3 supplies actual ChannelResult objects,
/// and A0 owns ProducerStore final commits plus token-scoped virtual PLC effects.
///
/// All items are keyed by WorkpieceId, never barcode or "latest" item. Capture results
/// may arrive before the station sensor event (PeerLink reordering) but cannot complete
/// a station until its sensor enter event and its complete set of channel results exist.
/// This class never talks to hardware or adds a second Host.
/// </summary>
public sealed class ProductionFlowCoordinator : IProductionFlowCoordinator
{
    public static IReadOnlyList<string> StationOrder { get; } =
        Array.AsReadOnly(new[] { "LOADING", "ST03", "ST04", "ST05", "ST06", "ST09", "ST10" });

    private static readonly IReadOnlyDictionary<string, string[]> ChannelsByStation =
        StationOrder.ToDictionary(station => station,
            station => ChannelCatalog.All.Where(c => c.StationId == station)
                .Select(c => c.ChannelId).Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);

    private static readonly string[] InspectionChannels = ChannelCatalog.All
        .Where(c => !c.IsPositioning).Select(c => c.ChannelId)
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class ItemState(WorkpieceIdentity identity, long registeredSequence)
    {
        public WorkpieceIdentity Identity { get; } = identity;
        public long RegisteredSequence { get; } = registeredSequence;
        public long LastSequence { get; set; } = registeredSequence;
        public string? CurrentStationId { get; set; }
        public string? EndBarcode { get; set; }
        public FinalDecision? Final { get; set; }
        public bool Ejected { get; set; }
        public Dictionary<string, DateTimeOffset> Entered { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Completed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ChannelResult> Captures { get; } = new(StringComparer.Ordinal);

        public ItemState Clone()
        {
            var clone = new ItemState(Identity, RegisteredSequence)
            {
                LastSequence = LastSequence,
                CurrentStationId = CurrentStationId,
                EndBarcode = EndBarcode,
                Final = Final,
                Ejected = Ejected
            };
            foreach (var row in Entered) clone.Entered.Add(row.Key, row.Value);
            foreach (var row in Completed) clone.Completed.Add(row.Key, row.Value);
            foreach (var row in Captures) clone.Captures.Add(row.Key, row.Value);
            return clone;
        }
    }

    private readonly object _gate = new();
    private readonly string _connectionString;
    private Dictionary<Guid, ItemState> _items = new();
    private readonly List<ProductionFlowEvent> _events = new();
    public string DatabasePath { get; }

    public ProductionFlowCoordinator(string databasePath)
    {
        if (ChannelCatalog.All.Count != 11 ||
            ChannelsByStation.Values.Sum(channels => channels.Length) != 11 ||
            ChannelsByStation.Values.Any(channels => channels.Length == 0))
            throw new InvalidOperationException("The station flow must match the 11-channel catalog.");
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var db = Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS a1_production_flow_events(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    workpiece_id TEXT NOT NULL,
                    event_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_a1_flow_workpiece
                    ON a1_production_flow_events(workpiece_id,sequence);
                """;
            command.ExecuteNonQuery();
        }
        using var read = db.CreateCommand();
        read.CommandText = """
            SELECT sequence, workpiece_id, event_json
            FROM a1_production_flow_events ORDER BY sequence
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var evt = WireFormat.Parse<ProductionFlowEvent>(reader.GetString(2));
            var sequence = reader.GetInt64(0);
            if (evt.Sequence != sequence || sequence < 1 ||
                evt.WorkpieceId.ToString("D") != reader.GetString(1) ||
                (_events.Count != 0 && sequence <= _events[^1].Sequence))
                throw new InvalidDataException("Invalid A1 production flow journal ordering or identity.");
            try { Apply(_items, evt); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                throw new InvalidDataException("Unable to replay production flow event " + sequence, ex);
            }
            _events.Add(evt);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static ItemState Require(Dictionary<Guid, ItemState> items, Guid workpieceId) =>
        items.TryGetValue(workpieceId, out var value) ? value :
            throw new KeyNotFoundException("Unknown WorkpieceId: " + workpieceId);

    private ItemState Require(Guid workpieceId) => Require(_items, workpieceId);

    private static string RequireStation(string? stationId)
    {
        if (stationId is null || !ChannelsByStation.ContainsKey(stationId))
            throw new ArgumentException("Unknown station: " + stationId, nameof(stationId));
        return stationId;
    }

    private static FinalDecision? Candidate(ItemState state)
    {
        if (state.Final is not null || state.Ejected ||
            state.Completed.Count != StationOrder.Count ||
            state.Captures.Count != ChannelCatalog.All.Count ||
            state.EndBarcode is null ||
            string.IsNullOrWhiteSpace(state.EndBarcode) ||
            !string.Equals(state.EndBarcode, state.Identity.Barcode, StringComparison.Ordinal))
            return null;

        var decision = QualityRules.Evaluate(state.Identity, InspectionChannels,
            state.Captures.Values.Where(c => !ChannelCatalog.Require(c.ChannelId).IsPositioning),
            endBarcodeMatches: true);
        return decision.Disposition == FinalDisposition.Hold || !decision.IsSimulation ? null : decision;
    }

    private static bool MatchesFinal(FinalDecision a, FinalDecision b) =>
        a.Disposition == b.Disposition &&
        a.IsSimulation == b.IsSimulation &&
        a.Proof == b.Proof &&
        a.Expected.SequenceEqual(b.Expected, StringComparer.Ordinal) &&
        a.Received.SequenceEqual(b.Received, StringComparer.Ordinal);

    private static void Apply(Dictionary<Guid, ItemState> states, ProductionFlowEvent evt)
    {
        if (evt.WorkpieceId == Guid.Empty || evt.Sequence < 1)
            throw new InvalidDataException("Production event needs sequence and WorkpieceId.");
        if (evt.Kind == ProductionFlowEventKind.Registered)
        {
            var identity = evt.Workpiece ?? throw new InvalidDataException("Missing registration identity.");
            identity.Validate();
            if (identity.WorkpieceId != evt.WorkpieceId || states.ContainsKey(evt.WorkpieceId))
                throw new InvalidDataException("Duplicate or inconsistent WorkpieceId registration.");
            states.Add(evt.WorkpieceId, new ItemState(identity, evt.Sequence));
            return;
        }

        var state = Require(states, evt.WorkpieceId);
        if (state.Ejected)
            throw new InvalidOperationException("Ejected workpiece cannot receive new progress.");
        if (state.Final is not null && evt.Kind != ProductionFlowEventKind.EjectionConfirmed)
            throw new InvalidOperationException("Finalized workpiece cannot be reprocessed.");

        switch (evt.Kind)
        {
            case ProductionFlowEventKind.StationEntered:
            {
                string station = RequireStation(evt.StationId);
                int next = state.Entered.Count;
                if (next >= StationOrder.Count ||
                    StationOrder[next] != station ||
                    (next > 0 && !state.Completed.ContainsKey(StationOrder[next - 1])))
                    throw new InvalidOperationException("Station entry skipped or previous station unfinished.");
                state.Entered.Add(station, evt.OccurredUtc);
                state.CurrentStationId = station;
                break;
            }
            case ProductionFlowEventKind.ChannelCaptured:
            {
                var result = evt.Result ?? throw new InvalidDataException("Missing capture.");
                result.ValidateFor(state.Identity);
                if (!result.IsSimulated)
                    throw new InvalidOperationException("A1 SIM_ONLY flow cannot use a REAL capture.");
                if (state.Captures.TryGetValue(result.ChannelId, out var previous) &&
                    previous.AttemptNo >= result.AttemptNo)
                    throw new InvalidOperationException("Non-increasing channel capture attempt.");
                state.Captures[result.ChannelId] = result;
                break;
            }
            case ProductionFlowEventKind.StationCompleted:
            {
                string station = RequireStation(evt.StationId);
                if (state.CurrentStationId != station ||
                    !state.Entered.ContainsKey(station) ||
                    state.Completed.ContainsKey(station) ||
                    ChannelsByStation[station].Any(ch => !state.Captures.ContainsKey(ch)))
                    throw new InvalidOperationException("Station cannot complete before its captures and entry.");
                state.Completed.Add(station, evt.OccurredUtc);
                break;
            }
            case ProductionFlowEventKind.EndBarcodeScanned:
                if (state.Completed.Count != StationOrder.Count ||
                    state.Captures.Count != ChannelCatalog.All.Count ||
                    state.EndBarcode is not null)
                    throw new InvalidOperationException("Exit scan requires all seven stations and 11 channels.");
                state.EndBarcode = evt.EndBarcode ??
                    throw new InvalidDataException("Missing barcode scan payload.");
                break;
            case ProductionFlowEventKind.FinalPersisted:
            {
                var actual = evt.Final ?? throw new InvalidDataException("Missing final.");
                var expected = Candidate(state);
                if (expected is null || !MatchesFinal(expected, actual))
                    throw new InvalidOperationException("Final does not match completed 11-channel evidence.");
                state.Final = actual with
                {
                    Expected = actual.Expected.ToArray(),
                    Received = actual.Received.ToArray()
                };
                break;
            }
            case ProductionFlowEventKind.EjectionConfirmed:
                if (state.Final is null)
                    throw new InvalidOperationException("No durable final recorded before ejection acknowledgment.");
                state.Ejected = true;
                state.CurrentStationId = "OUTFEED";
                break;
            default:
                throw new InvalidDataException("Unsupported flow event: " + evt.Kind);
        }
        state.LastSequence = evt.Sequence;
    }

    /// <summary>Validate a cloned state first; only then commit the event and publish the state.</summary>
    private void Append(ProductionFlowEvent proposed)
    {
        var evt = proposed with
        {
            Sequence = _events.Count == 0 ? 1 : _events[^1].Sequence + 1,
            OccurredUtc = DateTimeOffset.UtcNow
        };
        var next = _items.ToDictionary(x => x.Key, x => x.Value.Clone());
        Apply(next, evt);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO a1_production_flow_events(sequence,workpiece_id,event_json)
            VALUES($sequence,$id,$json)
            """;
        cmd.Parameters.AddWithValue("$sequence", evt.Sequence);
        cmd.Parameters.AddWithValue("$id", evt.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$json", WireFormat.Serialize(evt));
        cmd.ExecuteNonQuery();
        transaction.Commit();
        _items = next;
        _events.Add(evt);
    }

    public bool Register(WorkpieceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        lock (_gate)
        {
            if (_items.TryGetValue(identity.WorkpieceId, out var existing))
            {
                if (existing.Identity != identity)
                    throw new InvalidDataException("WorkpieceId is already registered with another identity.");
                return false;
            }
            Append(new(0, ProductionFlowEventKind.Registered,
                identity.WorkpieceId, default, Workpiece: identity));
            return true;
        }
    }

    public bool EnterStation(Guid workpieceId, string stationId)
    {
        lock (_gate)
        {
            var state = Require(workpieceId);
            RequireStation(stationId);
            if (state.Entered.ContainsKey(stationId))
            {
                if (state.CurrentStationId != stationId && !state.Completed.ContainsKey(stationId))
                    throw new InvalidOperationException("Previous station cannot be reopened out of order.");
                return false;
            }
            Append(new(0, ProductionFlowEventKind.StationEntered, workpieceId, default,
                StationId: stationId));
            return true;
        }
    }

    public bool RecordChannel(ChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var state = Require(result.WorkpieceId);
            result.ValidateFor(state.Identity);
            if (state.Captures.TryGetValue(result.ChannelId, out var previous))
            {
                if (previous.AttemptNo > result.AttemptNo) return false; // delayed PeerLink result
                if (previous.AttemptNo == result.AttemptNo)
                {
                    if (previous == result) return false; // retransmitted Inbox message
                    throw new InvalidDataException("Conflicting result for the same channel attempt.");
                }
            }
            Append(new(0, ProductionFlowEventKind.ChannelCaptured, result.WorkpieceId,
                default, Result: result));
            return true;
        }
    }

    public bool CompleteStation(Guid workpieceId, string stationId)
    {
        lock (_gate)
        {
            var state = Require(workpieceId);
            RequireStation(stationId);
            if (state.Completed.ContainsKey(stationId)) return false;
            Append(new(0, ProductionFlowEventKind.StationCompleted, workpieceId,
                default, StationId: stationId));
            return true;
        }
    }

    public bool ObserveEndBarcode(Guid workpieceId, string barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        lock (_gate)
        {
            var state = Require(workpieceId);
            if (state.EndBarcode is not null)
            {
                if (state.EndBarcode != barcode)
                    throw new InvalidDataException("Conflicting end barcode reads for one workpiece.");
                return false;
            }
            Append(new(0, ProductionFlowEventKind.EndBarcodeScanned, workpieceId,
                default, EndBarcode: barcode));
            return true;
        }
    }

    public FinalDecision? EvaluateFinal(Guid workpieceId)
    {
        lock (_gate) return Candidate(Require(workpieceId));
    }

    public bool FinalPersisted(Guid workpieceId, FinalDecision final)
    {
        ArgumentNullException.ThrowIfNull(final);
        lock (_gate)
        {
            var state = Require(workpieceId);
            if (state.Final is not null)
            {
                if (!MatchesFinal(state.Final, final))
                    throw new InvalidDataException("Conflicting final for this physical workpiece.");
                return false;
            }
            Append(new(0, ProductionFlowEventKind.FinalPersisted, workpieceId,
                default, Final: final));
            return true;
        }
    }

    public bool EjectionConfirmed(Guid workpieceId)
    {
        lock (_gate)
        {
            var state = Require(workpieceId);
            if (state.Ejected) return false;
            Append(new(0, ProductionFlowEventKind.EjectionConfirmed, workpieceId, default));
            return true;
        }
    }

    private static WorkpieceProductionProgress Project(ItemState state)
    {
        var stations = StationOrder.Select(station =>
        {
            state.Entered.TryGetValue(station, out var entered);
            state.Completed.TryGetValue(station, out var completed);
            var channels = ChannelsByStation[station];
            return new StationProductionProgress(station,
                state.Entered.ContainsKey(station) ? entered : null,
                state.Completed.ContainsKey(station) ? completed : null,
                Array.AsReadOnly(channels.ToArray()),
                Array.AsReadOnly(channels.Where(ch => state.Captures.ContainsKey(ch)).ToArray()));
        }).ToArray();

        var candidate = Candidate(state);
        bool? endMatch = state.EndBarcode is null ? null :
            !string.IsNullOrWhiteSpace(state.EndBarcode) &&
            string.Equals(state.EndBarcode, state.Identity.Barcode, StringComparison.Ordinal);
        string phase = state.Ejected ? "EJECTED" :
            state.Final is not null ? "FINAL_COMMITTED" :
            candidate is not null ? "READY_TO_FINALIZE" :
            endMatch == false ? "END_BARCODE_MISMATCH" :
            state.Completed.Count == StationOrder.Count ? "WAITING_FOR_FINAL_EVIDENCE" :
            state.CurrentStationId is null ? "REGISTERED" :
            state.Completed.ContainsKey(state.CurrentStationId) ? "BETWEEN_STATIONS" : "AT_STATION";
        return new WorkpieceProductionProgress(
            state.Identity, phase, state.CurrentStationId, state.Completed.Count,
            StationOrder.Count, state.Captures.Count, ChannelCatalog.All.Count,
            Array.AsReadOnly(state.Captures.Keys.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(stations), endMatch, candidate is not null,
            state.Final, state.Ejected, state.LastSequence);
    }

    public WorkpieceProductionProgress Get(Guid workpieceId)
    {
        lock (_gate) return Project(Require(workpieceId));
    }

    public IReadOnlyList<WorkpieceProductionProgress> GetAll()
    {
        lock (_gate)
            return _items.Values.OrderBy(x => x.RegisteredSequence)
                .Select(Project).ToArray();
    }

    public WorkpieceProductionProgress Replay(Guid workpieceId, long atOrBeforeSequence)
    {
        if (atOrBeforeSequence < 1)
            throw new ArgumentOutOfRangeException(nameof(atOrBeforeSequence));
        lock (_gate)
        {
            var state = new Dictionary<Guid, ItemState>();
            foreach (var evt in _events.Where(x => x.WorkpieceId == workpieceId &&
                         x.Sequence <= atOrBeforeSequence))
                Apply(state, evt);
            return Project(Require(state, workpieceId));
        }
    }

    public IReadOnlyList<ProductionFlowEvent> EventsFor(Guid workpieceId)
    {
        lock (_gate)
        {
            _ = Require(workpieceId);
            return _events.Where(x => x.WorkpieceId == workpieceId).ToArray();
        }
    }

    /// <summary>Read-only JSON for A0's upcoming per-item IPC or offline operator demo.</summary>
    public string ExportJson() => WireFormat.Serialize(GetAll());
}
