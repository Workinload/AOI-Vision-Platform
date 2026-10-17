using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime.Plc;
using Microsoft.Data.Sqlite;

namespace AOI.Runtime.Agent01;

/// <summary>
/// Master Core's immutable two-word legacy output intent. This is AOI business state,
/// NOT a PLC transport. Real production writes remain unavailable in SIM_ONLY.
/// </summary>
public sealed record FinalOutputIntent(
    Guid IntentId, WorkpieceIdentity Workpiece, ushort Token,
    LegacyExitScenario Scenario, FinalDisposition Disposition, DateTimeOffset CreatedUtc);

public enum FinalOutputEventKind
{
    IntentPrepared,
    WriteClaimed,
    WireSendObserved,
    FinsAckObserved,
    ReadbackConfirmed,
    OutcomeUnknown
}

public sealed record FinalOutputEvent(
    long Sequence, FinalOutputEventKind Kind, Guid IntentId, Guid WorkpieceId,
    DateTimeOffset OccurredUtc, int StepIndex = -1,
    FinalOutputIntent? Intent = null, Guid? ClaimId = null,
    LegacyFinsWriteProof? WireProof = null, ushort? ReadbackValue = null,
    string? ReasonCode = null);

public sealed record FinalOutputStep(
    int Index, ushort DmAddress, ushort Value, Guid? ClaimId,
    LegacyFinsWriteProof? Sent, LegacyFinsWriteProof? Ack,
    DateTimeOffset? ReadbackUtc, ushort? ReadbackValue)
{
    public bool Confirmed => ReadbackUtc.HasValue;
}

/// <summary>Write entitlement granted ONLY after its WriteClaimed event was fsynced.</summary>
public sealed record FinalOutputWriteClaim(
    Guid IntentId, Guid ClaimId, WorkpieceIdentity Workpiece, ushort Token,
    int StepIndex, ushort DmAddress, ushort Value);

public sealed record FinalOutputProgress(
    FinalOutputIntent Intent, string Phase, IReadOnlyList<FinalOutputStep> Steps,
    bool Complete, bool MayClaimNextWrite, bool RequiresManualReconciliation,
    string? UnknownReason, long LastSequence)
{
    public int ConfirmedSteps => Steps.Count(x => x.Confirmed);
}

/// <summary>
/// Event-sourced final PLC output authority, keyed by WorkpieceId and physical token.
/// An unconfirmed write is NEVER automatically replayed. On startup every fsynced
/// claim lacking an ACK+readback transitions to durable UNKNOWN, even when the process
/// died between claiming and sending. This trades availability for no double actuation.
///
/// Single Master writer: an OS file lease prevents two instances from handing out
/// duplicate write claims for one SQLite journal. No FINS packets or hardware IO here.
/// </summary>
public sealed class FinalPlcOutputJournal : IDisposable
{
    private sealed class Mutable(FinalOutputIntent intent, long sequence)
    {
        public FinalOutputIntent Intent { get; } = intent;
        public List<FinalOutputStep> Steps { get; } =
            Plan(intent.Scenario, intent.Disposition)
                .Select((x, i) => new FinalOutputStep(i, x.Address, x.Value,
                    null, null, null, null, null)).ToList();
        public string? UnknownReason { get; set; }
        public long LastSequence { get; set; } = sequence;
        public bool Completed => Steps.All(x => x.Confirmed);
        public FinalOutputStep? Current => Steps.FirstOrDefault(x => !x.Confirmed);

        public Mutable Clone()
        {
            var result = new Mutable(Intent, LastSequence) { UnknownReason = UnknownReason };
            for (int i = 0; i < Steps.Count; i++) result.Steps[i] = Steps[i];
            return result;
        }
    }

    private readonly object _sync = new();
    private readonly string _connectionString;
    private readonly FileStream _writerLease;
    private Dictionary<Guid, Mutable> _states = new();
    private readonly List<FinalOutputEvent> _events = new();
    private bool _disposed;

    public string DatabasePath { get; }

    public FinalPlcOutputJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        // The lease must exist BEFORE replay/returning an executable write claim.
        _writerLease = new FileStream(DatabasePath + ".writer-lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();
        try
        {
            using var db = Open();
            using (var schema = db.CreateCommand())
            {
                schema.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    CREATE TABLE IF NOT EXISTS a1_final_output_events (
                        sequence INTEGER PRIMARY KEY,
                        intent_id TEXT NOT NULL,
                        workpiece_id TEXT NOT NULL,
                        event_json TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS ix_a1_final_output_item
                        ON a1_final_output_events(workpiece_id,sequence);
                    """;
                schema.ExecuteNonQuery();
            }

            using (var query = db.CreateCommand())
            {
                query.CommandText = """
                    SELECT sequence,intent_id,workpiece_id,event_json
                    FROM a1_final_output_events ORDER BY sequence
                    """;
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    var evt = WireFormat.Parse<FinalOutputEvent>(reader.GetString(3));
                    if (evt.Sequence != reader.GetInt64(0) ||
                        evt.IntentId.ToString("D") != reader.GetString(1) ||
                        evt.WorkpieceId.ToString("D") != reader.GetString(2) ||
                        (_events.Count != 0 && evt.Sequence <= _events[^1].Sequence))
                        throw new InvalidDataException("Output journal sequence or identity mismatch.");
                    Apply(_states, evt);
                    _events.Add(evt);
                }
            }

            // The only safe reboot assumption: a claimed write may have reached the
            // PLC even when this journal has no observable ACK/readback.
            foreach (var state in _states.Values.ToArray())
                if (state.UnknownReason is null && state.Current is { ClaimId: not null })
                    Append(new(0, FinalOutputEventKind.OutcomeUnknown,
                        state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                        state.Current.Index, ClaimId: state.Current.ClaimId,
                        ReasonCode: "PROCESS_RESTART_WITH_UNRESOLVED_WRITE"));
        }
        catch
        {
            _writerLease.Dispose();
            throw;
        }
    }

    private static (ushort Address, ushort Value)[] Plan(
        LegacyExitScenario scenario, FinalDisposition disposition)
    {
        if (!Enum.IsDefined(scenario) ||
            disposition is not (FinalDisposition.Good or FinalDisposition.Reject) ||
            (scenario != LegacyExitScenario.Normal && disposition != FinalDisposition.Reject))
            throw new ArgumentException("Invalid legacy final output branch.");
        return scenario switch
        {
            LegacyExitScenario.Normal =>
            [
                (LegacyPlcShadow.FinalQualityAddress,
                    disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2),
                (LegacyPlcShadow.EndRequestAddress, (ushort)2)
            ],
            LegacyExitScenario.BarcodeFailure =>
            [
                (LegacyPlcShadow.EndRequestAddress, (ushort)3),
                (LegacyPlcShadow.FinalQualityAddress, (ushort)2)
            ],
            LegacyExitScenario.BarcodeMismatch =>
            [
                (LegacyPlcShadow.EndRequestAddress, (ushort)4),
                (LegacyPlcShadow.FinalQualityAddress, (ushort)2)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var timeout = db.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=5000;";
        timeout.ExecuteNonQuery();
        return db;
    }

    private void CheckOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FinalPlcOutputJournal));
    }

    private static Mutable Require(Dictionary<Guid, Mutable> states, Guid intentId) =>
        states.TryGetValue(intentId, out var state) ? state :
            throw new KeyNotFoundException("Unknown final output intent " + intentId);

    private Mutable Require(Guid intentId) => Require(_states, intentId);

    private static FinalOutputStep CurrentFor(Mutable state, FinalOutputEvent evt)
    {
        if (state.UnknownReason is not null || state.Completed ||
            state.Current is not { } step || evt.StepIndex != step.Index)
            throw new InvalidOperationException("Final output step is completed, unknown or out of order.");
        return step;
    }

    private static void Apply(Dictionary<Guid, Mutable> states, FinalOutputEvent evt)
    {
        if (evt.Sequence < 1 || evt.IntentId == Guid.Empty ||
            evt.WorkpieceId == Guid.Empty || evt.OccurredUtc == default)
            throw new InvalidDataException("Invalid persistent final output event.");
        if (evt.Kind == FinalOutputEventKind.IntentPrepared)
        {
            var intent = evt.Intent ?? throw new InvalidDataException("Missing final intent.");
            intent.Workpiece.Validate();
            _ = Plan(intent.Scenario, intent.Disposition);
            if (intent.IntentId != evt.IntentId ||
                intent.Workpiece.WorkpieceId != evt.WorkpieceId ||
                intent.Token == 0 || intent.CreatedUtc == default ||
                states.ContainsKey(intent.IntentId) ||
                states.Values.Any(x => x.Intent.Workpiece.WorkpieceId == evt.WorkpieceId ||
                                       x.Intent.Token == intent.Token))
                throw new InvalidDataException("Duplicate or inconsistent workpiece/token final intent.");
            states.Add(intent.IntentId, new Mutable(intent, evt.Sequence));
            return;
        }

        var state = Require(states, evt.IntentId);
        if (evt.WorkpieceId != state.Intent.Workpiece.WorkpieceId)
            throw new InvalidDataException("Final event is not owned by this WorkpieceId.");
        // Unknown can be recorded from an interrupted ACK/readback, never inferred as success.
        var step = state.Current;
        if (evt.Kind == FinalOutputEventKind.OutcomeUnknown)
        {
            if (state.UnknownReason is not null || step is null ||
                step.Index != evt.StepIndex || step.ClaimId is null ||
                step.ClaimId != evt.ClaimId ||
                !ValidReason(evt.ReasonCode))
                throw new InvalidOperationException("Cannot mark an unclaimed or resolved output unknown.");
            state.UnknownReason = evt.ReasonCode;
            state.LastSequence = evt.Sequence;
            return;
        }
        step = CurrentFor(state, evt);
        switch (evt.Kind)
        {
            case FinalOutputEventKind.WriteClaimed:
                if (step.ClaimId is not null || evt.ClaimId is not { } claim ||
                    claim == Guid.Empty ||
                    states.Values.SelectMany(x => x.Steps)
                        .Any(x => x.ClaimId == claim))
                    throw new InvalidOperationException("Write claim must be new and exclusive.");
                step = step with { ClaimId = claim };
                break;

            case FinalOutputEventKind.WireSendObserved:
                if (step.ClaimId is null || step.ClaimId != evt.ClaimId ||
                    step.Sent is not null || evt.WireProof is not { } sent ||
                    sent.OperationId == Guid.Empty ||
                    sent.AcknowledgedUtc is not null || sent.ReplyVerified ||
                    sent.SentUtc == default ||
                    sent.DmAddress != step.DmAddress || sent.Value != step.Value)
                    throw new InvalidDataException("Wire send must correlate with one claimed word.");
                step = step with { Sent = sent };
                break;

            case FinalOutputEventKind.FinsAckObserved:
                if (step.ClaimId is null || step.ClaimId != evt.ClaimId ||
                    step.Sent is not { } previous || step.Ack is not null ||
                    evt.WireProof is not { } acknowledged ||
                    acknowledged.OperationId != previous.OperationId ||
                    acknowledged.Sid != previous.Sid ||
                    acknowledged.DmAddress != previous.DmAddress ||
                    acknowledged.Value != previous.Value ||
                    acknowledged.SentUtc != previous.SentUtc ||
                    !acknowledged.ReplyVerified ||
                    acknowledged.AcknowledgedUtc is not { } ackAt ||
                    ackAt < previous.SentUtc)
                    throw new InvalidDataException("FINS ACK does not match the actual send proof/SID.");
                step = step with { Ack = acknowledged };
                break;

            case FinalOutputEventKind.ReadbackConfirmed:
                if (step.ClaimId is null || step.ClaimId != evt.ClaimId ||
                    step.Ack is null || step.ReadbackUtc is not null ||
                    evt.ReadbackValue != step.Value)
                    throw new InvalidOperationException("Only matching ACK+readback completes the step.");
                step = step with { ReadbackUtc = evt.OccurredUtc, ReadbackValue = evt.ReadbackValue };
                break;

            default:
                throw new InvalidDataException("Unsupported final-output event kind.");
        }
        state.Steps[step.Index] = step;
        state.LastSequence = evt.Sequence;
    }

    private static bool ValidReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 96 &&
        value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    /// <summary>Apply a valid event to one cloned intent, commit FULL WAL, then publish.</summary>
    private void Append(FinalOutputEvent proposed)
    {
        CheckOpen();
        var evt = proposed with
        {
            Sequence = _events.Count == 0 ? 1 : checked(_events[^1].Sequence + 1),
            OccurredUtc = DateTimeOffset.UtcNow
        };
        var next = new Dictionary<Guid, Mutable>(_states);
        if (next.TryGetValue(evt.IntentId, out var previous))
            next[evt.IntentId] = previous.Clone();
        Apply(next, evt);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO a1_final_output_events(sequence,intent_id,workpiece_id,event_json)
            VALUES($seq,$intent,$workpiece,$json)
            """;
        cmd.Parameters.AddWithValue("$seq", evt.Sequence);
        cmd.Parameters.AddWithValue("$intent", evt.IntentId.ToString("D"));
        cmd.Parameters.AddWithValue("$workpiece", evt.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$json", WireFormat.Serialize(evt));
        cmd.ExecuteNonQuery();
        tx.Commit();
        _states = next;
        _events.Add(evt);
    }

    /// <summary>
    /// Prepare only from existing Master ProductionStore evidence:
    /// normal => an immutable SIM_ONLY Good/Reject final and no HOLD;
    /// scanner failure/mismatch => a durable WorkpieceId HOLD and NO final.
    /// No output authorization is implied until ClaimNextWrite returns.
    /// </summary>
    public FinalOutputProgress Prepare(ProductionStore store, WorkpieceIdentity item,
        ushort token, LegacyExitScenario scenario, FinalDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        _ = Plan(scenario, disposition);
        if (token == 0) throw new ArgumentOutOfRangeException(nameof(token));
        lock (_sync)
        {
            CheckOpen();
            if (store.FindWorkpiece(item.WorkpieceId) != item)
                throw new InvalidDataException("Output requires a registered, matching Master workpiece.");
            var existing = _states.Values.FirstOrDefault(x =>
                x.Intent.Workpiece.WorkpieceId == item.WorkpieceId);
            if (existing is not null)
            {
                if (existing.Intent.Token != token || existing.Intent.Workpiece != item ||
                    existing.Intent.Scenario != scenario || existing.Intent.Disposition != disposition)
                    throw new InvalidDataException("Final output intent conflicts with persisted WorkpieceId.");
                return Project(existing);
            }
            if (_states.Values.Any(x => x.Intent.Token == token))
                throw new InvalidDataException("A physical exit token cannot be reassigned in one production journal.");

            var persistedFinal = store.LatestFinal(item.WorkpieceId);
            bool held = store.HasWorkpieceHold(item.WorkpieceId);
            if (scenario == LegacyExitScenario.Normal)
            {
                if (held || persistedFinal is null || !persistedFinal.IsSimulation ||
                    persistedFinal.Disposition != disposition)
                    throw new InvalidOperationException("Normal output requires durable matching SIM_ONLY final.");
            }
            else if (!held || persistedFinal is not null || disposition != FinalDisposition.Reject)
            {
                throw new InvalidOperationException("Abnormal output requires durable HOLD and no final.");
            }

            var intent = new FinalOutputIntent(Guid.NewGuid(), item, token,
                scenario, disposition, DateTimeOffset.UtcNow);
            Append(new(0, FinalOutputEventKind.IntentPrepared, intent.IntentId,
                item.WorkpieceId, default, Intent: intent));
            return Project(Require(intent.IntentId));
        }
    }

    /// <summary>
    /// The only write-entitlement method. It fsyncs a unique claim BEFORE returning.
    /// Never call again for the same uncertain write, including after reboot.
    /// </summary>
    public FinalOutputWriteClaim? ClaimNextWrite(Guid intentId)
    {
        lock (_sync)
        {
            CheckOpen();
            var state = Require(intentId);
            if (state.UnknownReason is not null)
                throw new InvalidOperationException("OUTPUT_UNKNOWN_MANUAL_RECONCILIATION_REQUIRED");
            if (state.Completed) return null;
            var step = state.Current!;
            if (step.ClaimId is not null)
                throw new InvalidOperationException("OUTPUT_ALREADY_CLAIMED_DO_NOT_RESEND");
            var claim = Guid.NewGuid();
            Append(new(0, FinalOutputEventKind.WriteClaimed, intentId,
                state.Intent.Workpiece.WorkpieceId, default,
                step.Index, ClaimId: claim));
            return new(state.Intent.IntentId, claim, state.Intent.Workpiece,
                state.Intent.Token, step.Index, step.DmAddress, step.Value);
        }
    }

    private (Mutable State, FinalOutputStep Step) ByClaim(Guid claimId)
    {
        if (claimId == Guid.Empty) throw new ArgumentException("Write claim cannot be empty.");
        foreach (var state in _states.Values)
            foreach (var step in state.Steps)
                if (step.ClaimId == claimId) return (state, step);
        throw new KeyNotFoundException("Unknown claimed PLC output operation " + claimId);
    }

    /// <summary>
    /// Called DURABLY inside A2 WriteObservedAsync(onBeforeSend), before UDP SendAsync.
    /// The FINS transport retains its own OperationId/SID, linked to this write claim.
    /// </summary>
    public bool ObserveWireSend(Guid claimId, LegacyFinsWriteProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        lock (_sync)
        {
            CheckOpen();
            var (state, step) = ByClaim(claimId);
            if (state.UnknownReason is not null) return false;
            if (step.Sent is not null)
            {
                if (step.Sent != proof)
                    throw new InvalidDataException("Conflicting FINS proof for one output claim.");
                return false;
            }
            Append(new(0, FinalOutputEventKind.WireSendObserved,
                state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                step.Index, ClaimId: claimId, WireProof: proof));
            return true;
        }
    }

    /// <summary>Only A2's verified SID/route/end-code reply may acknowledge this write.</summary>
    public bool ConfirmFinsAck(Guid claimId, LegacyFinsWriteProof acknowledged)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);
        lock (_sync)
        {
            CheckOpen();
            var (state, step) = ByClaim(claimId);
            if (state.UnknownReason is not null) return false;
            if (step.Ack is not null)
            {
                if (step.Ack != acknowledged)
                    throw new InvalidDataException("Conflicting ACK evidence.");
                return false;
            }
            Append(new(0, FinalOutputEventKind.FinsAckObserved,
                state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                step.Index, ClaimId: claimId, WireProof: acknowledged));
            return true;
        }
    }

    /// <summary>
    /// False on a wrong readback and durably UNKNOWN; no second write is permitted.
    /// An ACK alone never completes a PLC business step.
    /// </summary>
    public bool ConfirmReadback(Guid claimId, ushort actualValue)
    {
        lock (_sync)
        {
            CheckOpen();
            var (state, step) = ByClaim(claimId);
            if (state.UnknownReason is not null) return false;
            if (step.Confirmed)
            {
                if (step.ReadbackValue != actualValue)
                    throw new InvalidDataException("Conflicting previously confirmed readback.");
                return false;
            }
            if (step.Ack is null)
                throw new InvalidOperationException("Cannot accept a readback before FINS ACK.");
            if (actualValue != step.Value)
            {
                Append(new(0, FinalOutputEventKind.OutcomeUnknown,
                    state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                    step.Index, ClaimId: claimId, ReasonCode: "READBACK_VALUE_MISMATCH"));
                return false;
            }
            Append(new(0, FinalOutputEventKind.ReadbackConfirmed,
                state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                step.Index, ClaimId: claimId, ReadbackValue: actualValue));
            return true;
        }
    }

    /// <summary>A lost ACK, broken connection or unsuccessful readback is irreversible here.</summary>
    public bool MarkUnknown(Guid claimId, string reasonCode)
    {
        if (!ValidReason(reasonCode))
            throw new ArgumentException("Use a bounded uppercase diagnostic code.", nameof(reasonCode));
        lock (_sync)
        {
            CheckOpen();
            var (state, step) = ByClaim(claimId);
            if (state.UnknownReason is not null || step.Confirmed) return false;
            Append(new(0, FinalOutputEventKind.OutcomeUnknown,
                state.Intent.IntentId, state.Intent.Workpiece.WorkpieceId, default,
                step.Index, ClaimId: claimId, ReasonCode: reasonCode));
            return true;
        }
    }

    /// <summary>
    /// Called on transport disconnect. Only already claimed (possibly sent)
    /// writes become UNKNOWN. Pure prepared intents remain safe to dispatch later.
    /// </summary>
    public int MarkDisconnected(string reasonCode = "UDP_CONNECTION_LOST")
    {
        if (!ValidReason(reasonCode)) throw new ArgumentException("Invalid disconnect reason.");
        lock (_sync)
        {
            CheckOpen();
            var unfinished = _states.Values.Where(s => s.UnknownReason is null &&
                              s.Current is { ClaimId: not null })
                .Select(s => s.Current!.ClaimId!.Value).ToArray();
            foreach (var claimId in unfinished) MarkUnknown(claimId, reasonCode);
            return unfinished.Length;
        }
    }

    private static FinalOutputProgress Project(Mutable state)
    {
        bool complete = state.Completed;
        bool unknown = state.UnknownReason is not null;
        var current = state.Current;
        string phase = unknown ? "UNKNOWN_ACK_MANUAL_RECONCILIATION" :
            complete ? "COMPLETED" :
            current?.Ack is not null ? "ACKED_WAIT_READBACK" :
            current?.Sent is not null ? "FINS_SENT_WAIT_ACK" :
            current?.ClaimId is not null ? "CLAIMED_POSSIBLY_SENT" :
            "READY_TO_DISPATCH";
        return new(state.Intent, phase,
            Array.AsReadOnly(state.Steps.ToArray()), complete,
            !unknown && !complete && current?.ClaimId is null,
            unknown, state.UnknownReason, state.LastSequence);
    }

    public FinalOutputProgress Get(Guid intentId)
    {
        lock (_sync) { CheckOpen(); return Project(Require(intentId)); }
    }

    public FinalOutputProgress? ForWorkpiece(Guid workpieceId)
    {
        lock (_sync)
        {
            CheckOpen();
            var state = _states.Values.FirstOrDefault(x =>
                x.Intent.Workpiece.WorkpieceId == workpieceId);
            return state is null ? null : Project(state);
        }
    }

    public IReadOnlyList<FinalOutputProgress> GetAll()
    {
        lock (_sync)
        {
            CheckOpen();
            return _states.Values.OrderBy(s => s.Intent.CreatedUtc)
                .Select(Project).ToArray();
        }
    }

    public IReadOnlyList<FinalOutputEvent> EventsFor(Guid intentId)
    {
        lock (_sync)
        {
            CheckOpen();
            _ = Require(intentId);
            return _events.Where(e => e.IntentId == intentId).ToArray();
        }
    }

    public string ExportJson() => WireFormat.Serialize(GetAll());

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writerLease.Dispose();
        }
    }
}
