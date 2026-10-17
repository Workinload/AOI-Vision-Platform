using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;

namespace AOI.Runtime.Agent01;

/// <summary>
/// A1's reusable, single-process event-sourced correlation engine. The camera/PLC adapters
/// ONLY report events: no soft triggering, virtual mechanics, PLC IO, or fake inspection.
/// PLC request and physical frame can arrive in either order. CaptureId (allocated per
/// physical exposure) + immutable WorkpieceId/Epoch/ReleaseId/AttemptNo bind them.
/// A0 owns wiring into its existing ProductionFlowCoordinator and ProductionStore.
/// </summary>
public sealed class CaptureHandshakeCoordinator : ICaptureHandshakeCoordinator
{
    private sealed class AttemptState(CaptureTicket ticket, long sequence)
    {
        public CaptureTicket Ticket { get; } = ticket;
        public bool RequestObserved { get; set; }
        public CaptureFrameEvidence? Frame { get; set; }
        public bool FrameIsValidated { get; set; }
        public ushort? CamDoneSent { get; set; }
        public string? Failure { get; set; }
        public long LastSequence { get; set; } = sequence;
        public Dictionary<string, ChannelResult> Results { get; } = new(StringComparer.Ordinal);
        public bool HasAllResults => Ticket.ChannelIds.All(Results.ContainsKey);
        public bool Terminal => Failure is not null || (CamDoneSent == 2 && HasAllResults);

        public AttemptState Clone()
        {
            var other = new AttemptState(Ticket, LastSequence)
            {
                RequestObserved = RequestObserved,
                Frame = Frame,
                FrameIsValidated = FrameIsValidated,
                CamDoneSent = CamDoneSent,
                Failure = Failure
            };
            foreach (var result in Results) other.Results.Add(result.Key, result.Value);
            return other;
        }

        public ushort? AckDue => CamDoneSent is not null || !RequestObserved
            ? null : Failure is not null ? (ushort)3 : FrameIsValidated ? (ushort)2 : null;
    }

    private readonly object _sync = new();
    private Dictionary<Guid, AttemptState> _states = new();
    private readonly List<CaptureHandshakeEvent> _events = new();
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public CaptureHandshakeCoordinator(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();
        using var db = Open();
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS a1_capture_handshakes (
                    sequence INTEGER PRIMARY KEY,
                    capture_id TEXT NOT NULL,
                    workpiece_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    event_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_a1_capture_id
                    ON a1_capture_handshakes(capture_id, sequence);
                CREATE INDEX IF NOT EXISTS ix_a1_capture_workpiece
                    ON a1_capture_handshakes(workpiece_id, sequence);
                """;
            schema.ExecuteNonQuery();
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT sequence, capture_id, workpiece_id, kind, event_json
            FROM a1_capture_handshakes ORDER BY sequence
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var evt = WireFormat.Parse<CaptureHandshakeEvent>(reader.GetString(4));
            if (evt.Sequence != reader.GetInt64(0) ||
                evt.CaptureId.ToString("D") != reader.GetString(1) ||
                !string.Equals(evt.Kind.ToString(), reader.GetString(3), StringComparison.Ordinal) ||
                (_events.Count != 0 && evt.Sequence <= _events[^1].Sequence))
                throw new InvalidDataException("A1 capture event identity/order is inconsistent.");
            Apply(_states, evt);
            var itemId = _states[evt.CaptureId].Ticket.Workpiece.WorkpieceId;
            if (itemId.ToString("D") != reader.GetString(2))
                throw new InvalidDataException("A1 capture event belongs to an unexpected workpiece.");
            _events.Add(evt);
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return db;
    }

    private static AttemptState Require(Dictionary<Guid, AttemptState> states, Guid id) =>
        states.TryGetValue(id, out var state) ? state :
            throw new KeyNotFoundException("Unknown CaptureId " + id);

    private AttemptState Require(Guid captureId) => Require(_states, captureId);

    private static AttemptState? LatestState(IEnumerable<AttemptState> states,
        Guid workpieceId, string deviceId) =>
        states.Where(s => s.Ticket.Workpiece.WorkpieceId == workpieceId &&
                          s.Ticket.PhysicalDeviceId == deviceId)
            .MaxBy(s => s.Ticket.AttemptNo);

    private bool IsLatest(AttemptState state) =>
        ReferenceEquals(state, LatestState(_states.Values,
            state.Ticket.Workpiece.WorkpieceId, state.Ticket.PhysicalDeviceId));

    private static void RequireCurrent(Dictionary<Guid, AttemptState> states, AttemptState state)
    {
        var latest = LatestState(states.Values, state.Ticket.Workpiece.WorkpieceId,
            state.Ticket.PhysicalDeviceId);
        if (latest?.Ticket.CaptureId != state.Ticket.CaptureId)
            throw new InvalidOperationException("Old capture attempt cannot mutate a newer attempt.");
    }

    private static void VerifyResult(AttemptState state, ChannelResult result)
    {
        result.ValidateFor(state.Ticket.Workpiece);
        if (result.CaptureId != state.Ticket.CaptureId ||
            result.AttemptNo != state.Ticket.AttemptNo ||
            result.IsSimulated != state.Ticket.IsSimulation ||
            result.PhysicalDeviceId != state.Ticket.PhysicalDeviceId ||
            !state.Ticket.ChannelIds.Contains(result.ChannelId, StringComparer.Ordinal))
            throw new InvalidDataException("Inspection does not belong to the requested physical capture.");
        var frame = state.Frame ?? throw new InvalidOperationException("Inspection without acquired frame.");
        if (!state.FrameIsValidated || result.SourceFrameSha256 != frame.SourceFrameSha256)
            throw new InvalidDataException("Inspection source is not the verified acquired frame.");
        bool isLine = ChannelCatalog.Require(result.ChannelId).SharedFrameGroup is not null;
        if (isLine
            ? result.ParentFrameId != frame.SourceFrameId || result.FrameId == frame.SourceFrameId
            : result.FrameId != frame.SourceFrameId || result.ParentFrameId is not null)
            throw new InvalidDataException("Frame or shared line-parent identity mismatch.");
    }

    private static void Apply(Dictionary<Guid, AttemptState> states, CaptureHandshakeEvent evt)
    {
        if (evt.CaptureId == Guid.Empty || evt.Sequence < 1 || evt.EventUtc == default)
            throw new InvalidDataException("Invalid capture journal event identity/time.");
        if (evt.Kind == CaptureHandshakeEventKind.CaptureExpected)
        {
            var ticket = evt.Ticket ?? throw new InvalidDataException("CaptureExpected lacks ticket.");
            ticket.Validate();
            if (evt.CaptureId != ticket.CaptureId || states.ContainsKey(evt.CaptureId))
                throw new InvalidDataException("CaptureId re-used with another ticket.");
            var prior = LatestState(states.Values, ticket.Workpiece.WorkpieceId,
                ticket.PhysicalDeviceId);
            if (prior is not null)
            {
                if (prior.Ticket.Workpiece != ticket.Workpiece ||
                    ticket.AttemptNo <= prior.Ticket.AttemptNo ||
                    !prior.Terminal ||
                    (prior.RequestObserved && prior.CamDoneSent is null))
                    throw new InvalidOperationException("Previous physical attempt not finished or retry number invalid.");
            }
            states.Add(ticket.CaptureId, new AttemptState(ticket, evt.Sequence));
            return;
        }
        var state = Require(states, evt.CaptureId);
        RequireCurrent(states, state);
        if (state.Failure is not null && evt.Kind is not CaptureHandshakeEventKind.CamDoneConfirmed)
            throw new InvalidOperationException("Failed attempt cannot acquire a later frame/result.");
        if (state.HasAllResults)
            throw new InvalidOperationException("Completed attempt cannot be modified.");
        switch (evt.Kind)
        {
            case CaptureHandshakeEventKind.PlcRequestObserved:
                if (state.RequestObserved || evt.RequestAddress != state.Ticket.PlcAddress)
                    throw new InvalidDataException("Duplicate or wrong PLC request signal.");
                state.RequestObserved = true;
                break;
            case CaptureHandshakeEventKind.FrameAcquired:
                if (state.Frame is not null)
                    throw new InvalidDataException("Duplicate/second physical frame in one attempt.");
                var frame = evt.Frame ?? throw new InvalidDataException("Missing frame evidence.");
                frame.ValidateFor(state.Ticket);
                state.Frame = frame;
                break;
            case CaptureHandshakeEventKind.FrameValidated:
                if (state.Frame is null || state.FrameIsValidated ||
                    state.Frame.SourceFrameId != evt.ValidatedFrameId)
                    throw new InvalidDataException("Validated frame does not match acquired frame.");
                state.FrameIsValidated = true;
                break;
            case CaptureHandshakeEventKind.CamDoneConfirmed:
                if (state.CamDoneSent is not null || evt.CamDoneValue != state.AckDue)
                    throw new InvalidOperationException("CamDone requires request and verified frame or recorded failure.");
                state.CamDoneSent = evt.CamDoneValue;
                break;
            case CaptureHandshakeEventKind.InspectionCompleted:
                if (state.CamDoneSent != 2)
                    throw new InvalidOperationException("Inspection must follow a confirmed successful CamDone.");
                var result = evt.Inspection ?? throw new InvalidDataException("Missing inspection result.");
                VerifyResult(state, result);
                if (state.Results.ContainsKey(result.ChannelId))
                    throw new InvalidDataException("Conflicting inspection result for a channel and attempt.");
                state.Results.Add(result.ChannelId, result);
                break;
            case CaptureHandshakeEventKind.CaptureFailed:
                if (string.IsNullOrWhiteSpace(evt.FailureReason) || evt.FailureReason.Length > 256)
                    throw new InvalidDataException("Capture failure needs a bounded diagnostic reason.");
                state.Failure = evt.FailureReason;
                break;
            default:
                throw new InvalidDataException("Unsupported capture event: " + evt.Kind);
        }
        state.LastSequence = evt.Sequence;
    }

    /// <summary>
    /// Validate a per-capture clone before appending it to the FULL-synchronous WAL.
    /// A failed user event never corrupts the persisted state of another workpiece.
    /// </summary>
    private void Append(CaptureHandshakeEvent proposed)
    {
        var evt = proposed with
        {
            Sequence = _events.Count == 0 ? 1 : checked(_events[^1].Sequence + 1),
            EventUtc = DateTimeOffset.UtcNow
        };
        var cloned = new Dictionary<Guid, AttemptState>(_states);
        if (cloned.TryGetValue(evt.CaptureId, out var previous))
            cloned[evt.CaptureId] = previous.Clone();
        Apply(cloned, evt);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO a1_capture_handshakes(sequence,capture_id,workpiece_id,kind,event_json)
            VALUES($seq,$capture,$item,$kind,$json)
            """;
        cmd.Parameters.AddWithValue("$seq", evt.Sequence);
        cmd.Parameters.AddWithValue("$capture", evt.CaptureId.ToString("D"));
        cmd.Parameters.AddWithValue("$item", cloned[evt.CaptureId].Ticket.Workpiece.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$kind", evt.Kind.ToString());
        cmd.Parameters.AddWithValue("$json", WireFormat.Serialize(evt));
        cmd.ExecuteNonQuery();
        tx.Commit();
        _states = cloned;
        _events.Add(evt);
    }

    public bool ExpectCapture(CaptureTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.Validate();
        lock (_sync)
        {
            if (_states.TryGetValue(ticket.CaptureId, out var existing))
            {
                if (existing.Ticket != ticket)
                    throw new InvalidDataException("CaptureId already belongs to another physical attempt.");
                return false;
            }
            Append(new(0, CaptureHandshakeEventKind.CaptureExpected,
                ticket.CaptureId, default, Ticket: ticket));
            return true;
        }
    }

    public bool ObservePlcRequest(Guid captureId, string requestAddress)
    {
        lock (_sync)
        {
            var state = Require(captureId);
            if (state.RequestObserved && state.Ticket.PlcAddress == requestAddress) return false;
            if (!IsLatest(state) || state.Failure is not null) return false;
            Append(new(0, CaptureHandshakeEventKind.PlcRequestObserved, captureId,
                default, RequestAddress: requestAddress));
            return true;
        }
    }

    public bool FrameAcquired(CaptureFrameEvidence frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            var state = Require(frame.CaptureId);
            // Wrong physical-item evidence never modifies a valid ticket.
            frame.ValidateFor(state.Ticket);
            if (!IsLatest(state) || state.Failure is not null) return false;
            if (state.Frame is not null)
            {
                if (state.Frame != frame)
                    throw new InvalidDataException("A second frame conflicts with the first exposure.");
                return false;
            }
            if (frame.FrameUtc > state.Ticket.ExpiresAtUtc)
            {
                FailCaptureCore(frame.CaptureId, "FRAME_LATE_AFTER_DEADLINE");
                return false;
            }
            Append(new(0, CaptureHandshakeEventKind.FrameAcquired,
                frame.CaptureId, default, Frame: frame));
            return true;
        }
    }

    public bool FrameValidated(Guid captureId, Guid frameId)
    {
        lock (_sync)
        {
            var state = Require(captureId);
            if (!IsLatest(state) || state.Failure is not null) return false;
            if (state.FrameIsValidated && state.Frame?.SourceFrameId == frameId) return false;
            Append(new(0, CaptureHandshakeEventKind.FrameValidated,
                captureId, default, ValidatedFrameId: frameId));
            return true;
        }
    }

    public bool CamDoneConfirmed(Guid captureId, ushort value)
    {
        lock (_sync)
        {
            var state = Require(captureId);
            if (!IsLatest(state)) return false;
            if (state.CamDoneSent is not null)
            {
                if (state.CamDoneSent != value)
                    throw new InvalidDataException("Conflicting acknowledged CamDone value.");
                return false;
            }
            Append(new(0, CaptureHandshakeEventKind.CamDoneConfirmed,
                captureId, default, CamDoneValue: value));
            return true;
        }
    }

    public bool InspectionCompleted(ChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            var state = Require(result.CaptureId);
            VerifyResult(state, result);
            if (!IsLatest(state) || state.Failure is not null) return false;
            if (state.Results.TryGetValue(result.ChannelId, out var previous))
            {
                if (previous != result)
                    throw new InvalidDataException("Different results claim the same physical capture/channel.");
                return false;
            }
            Append(new(0, CaptureHandshakeEventKind.InspectionCompleted,
                result.CaptureId, default, Inspection: result));
            return true;
        }
    }

    private void FailCaptureCore(Guid captureId, string reason) =>
        Append(new(0, CaptureHandshakeEventKind.CaptureFailed,
            captureId, default, FailureReason: reason));

    public bool FailCapture(Guid captureId, string reason)
    {
        lock (_sync)
        {
            var state = Require(captureId);
            if (!IsLatest(state) || state.Failure is not null) return false;
            FailCaptureCore(captureId, reason);
            return true;
        }
    }

    public IReadOnlyList<CaptureHandshakeProgress> ExpireDue(DateTimeOffset nowUtc)
    {
        if (nowUtc == default) throw new ArgumentOutOfRangeException(nameof(nowUtc));
        lock (_sync)
        {
            var expired = _states.Values.Where(IsLatest)
                .Where(s => !s.Terminal && s.Ticket.ExpiresAtUtc <= nowUtc)
                .Select(s => s.Ticket.CaptureId).ToArray();
            foreach (var id in expired)
            {
                var state = Require(id);
                string reason = !state.RequestObserved ? "PLC_REQUEST_TIMEOUT" :
                    state.Frame is null ? "FRAME_MISSING_TIMEOUT" :
                    !state.FrameIsValidated ? "FRAME_VALIDATION_TIMEOUT" :
                    state.CamDoneSent is null ? "CAM_DONE_TIMEOUT" : "INSPECTION_TIMEOUT";
                FailCaptureCore(id, reason);
            }
            return expired.Select(id => Project(Require(id), isLatest: true)).ToArray();
        }
    }

    private static CaptureHandshakeProgress Project(AttemptState state, bool isLatest)
    {
        string phase = !isLatest ? "SUPERSEDED" : state.Failure is not null ? "FAILED" :
            state.HasAllResults ? state.Results.Values.Any(r =>
                r.Outcome is ChannelOutcome.Error or ChannelOutcome.Missing or ChannelOutcome.Uncertain)
                    ? "INSPECTION_ERROR" : "INSPECTION_COMPLETED" :
            state.CamDoneSent == 2 ? "INSPECTING" :
            state.AckDue == 2 ? "CAM_DONE_READY" :
            state.FrameIsValidated && !state.RequestObserved ? "FRAME_READY_BEFORE_REQUEST" :
            state.Frame is not null && !state.FrameIsValidated ? "FRAME_WAIT_VALIDATION" :
            state.RequestObserved ? "REQUEST_FIRST_WAIT_FRAME" : "CAPTURE_EXPECTED";
        var completed = state.Results.Keys.Order(StringComparer.Ordinal).ToArray();
        return new(state.Ticket, phase, state.RequestObserved, state.Frame is not null,
            state.FrameIsValidated, state.AckDue, state.CamDoneSent,
            Array.AsReadOnly(completed), state.Failure, state.LastSequence);
    }

    public CaptureHandshakeProgress Get(Guid captureId)
    {
        lock (_sync)
        {
            var state = Require(captureId);
            return Project(state, IsLatest(state));
        }
    }

    public CaptureHandshakeProgress? Latest(Guid workpieceId, string physicalDeviceId)
    {
        lock (_sync)
        {
            var state = LatestState(_states.Values, workpieceId, physicalDeviceId);
            return state is null ? null : Project(state, true);
        }
    }

    public IReadOnlyList<CaptureHandshakeProgress> PendingCamDone()
    {
        lock (_sync)
        {
            return _states.Values.Where(IsLatest).Where(s => s.AckDue is not null)
                .OrderBy(s => s.Ticket.ExpiresAtUtc)
                .Select(s => Project(s, true)).ToArray();
        }
    }

    public IReadOnlyList<CaptureHandshakeEvent> EventsFor(Guid captureId)
    {
        lock (_sync)
        {
            _ = Require(captureId);
            return _events.Where(e => e.CaptureId == captureId).ToArray();
        }
    }

    public string ExportJson()
    {
        lock (_sync)
        {
            var all = _states.Values.OrderBy(s => s.Ticket.Workpiece.CreatedAtUtc)
                .ThenBy(s => s.Ticket.PhysicalDeviceId, StringComparer.Ordinal)
                .ThenBy(s => s.Ticket.AttemptNo)
                .Select(s => Project(s, IsLatest(s))).ToArray();
            return WireFormat.Serialize(all);
        }
    }
}
