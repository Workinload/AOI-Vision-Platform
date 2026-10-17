using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace AOI.Diagnostics;

/// <summary>
/// Master Core's durable, append-only evidence for each two-step legacy exit.
/// This uses the existing timing-events.sqlite WAL, with additive schema creation
/// only: old timing_events, Core production WAL, and all existing traces stay intact.
/// NEVER calls a PLC or retries a write. Open an attempt once per WorkpieceId;
/// after restart, read and reconcile it instead of beginning another attempt.
/// </summary>
public sealed class FinalOutputAuditJournal
{
    private readonly string _connectionString;
    private readonly Guid _processRunId = Guid.NewGuid();

    public Guid ProcessRunId => _processRunId;
    public string DatabasePath { get; }

    public FinalOutputAuditJournal(string masterNodeFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterNodeFolder);
        string folder = Path.GetFullPath(masterNodeFolder);
        Directory.CreateDirectory(folder);
        DatabasePath = Path.Combine(folder, ProductionTimingJournal.FileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS final_output_intents (
                attempt_id TEXT PRIMARY KEY,
                workpiece_id TEXT NOT NULL UNIQUE,
                trace_id TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                release_id TEXT NOT NULL,
                token INTEGER NOT NULL,
                scenario TEXT NOT NULL,
                disposition TEXT NOT NULL,
                peer_message_id TEXT,
                process_run_id TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS final_output_audit_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                attempt_id TEXT NOT NULL REFERENCES final_output_intents(attempt_id),
                phase TEXT NOT NULL,
                step_number INTEGER NOT NULL,
                operation_id TEXT,
                fins_sid INTEGER,
                dm_address INTEGER,
                value INTEGER,
                request_sha256 TEXT,
                response_sha256 TEXT,
                readback_value INTEGER,
                peer_message_id TEXT,
                utc TEXT NOT NULL,
                process_run_id TEXT NOT NULL,
                monotonic_tick INTEGER NOT NULL,
                tick_frequency INTEGER NOT NULL,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_final_output_attempt_order
              ON final_output_audit_events(attempt_id,sequence);
            CREATE INDEX IF NOT EXISTS ix_final_output_peer
              ON final_output_audit_events(peer_message_id);
            """;
        init.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000";
        cmd.ExecuteNonQuery();
        return db;
    }

    /// <summary>
    /// Must be durable BEFORE performing the first scanner/PLC operation.
    /// The UNIQUE WorkpieceId index prevents a second send sequence on restart.
    /// </summary>
    public Guid Begin(WorkpieceIdentity item, ushort token, FinalOutputAuditScenario scenario,
        FinalDisposition disposition, Guid? peerMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        _ = FinalOutputFinsEvidence.Expected(scenario, disposition, 1);
        if (token == 0 || peerMessageId == Guid.Empty)
            throw new ArgumentException("Invalid physical token or peer message identity.");
        Guid id = Guid.NewGuid();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT attempt_id FROM final_output_intents WHERE workpiece_id=$item";
        query.Parameters.AddWithValue("$item", item.WorkpieceId.ToString("D"));
        if (query.ExecuteScalar() is not null)
            throw new InvalidOperationException(
                "FINAL_OUTPUT_ALREADY_STARTED: read existing WAL; no automatic replay.");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO final_output_intents(
                attempt_id,workpiece_id,trace_id,epoch,release_id,token,
                scenario,disposition,peer_message_id,process_run_id,created_utc)
            VALUES($id,$item,$trace,$epoch,$release,$token,$scenario,$disposition,$peer,$run,$utc)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$item", item.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$trace", item.TraceId.ToString("D"));
        cmd.Parameters.AddWithValue("$epoch", item.Epoch);
        cmd.Parameters.AddWithValue("$release", item.ReleaseId);
        cmd.Parameters.AddWithValue("$token", token);
        cmd.Parameters.AddWithValue("$scenario", scenario.ToString());
        cmd.Parameters.AddWithValue("$disposition", disposition.ToString());
        cmd.Parameters.AddWithValue("$peer", (object?)peerMessageId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$run", _processRunId.ToString("D"));
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "FINAL_OUTPUT_ALREADY_STARTED: never send twice for one workpiece.", ex);
        }
        tx.Commit();
        return id;
    }

    /// <summary>Prove the scanner's D2099=1 request from a REAL received UDP reply.</summary>
    public FinalOutputAuditEvent ScannerRequestVerified(Guid attemptId, Guid operationId,
        ReadOnlySpan<byte> readRequest, ReadOnlySpan<byte> readResponse)
    {
        var (sid, address) = FinalOutputFinsEvidence.ReadRequest(readRequest);
        var reply = FinalOutputFinsEvidence.VerifiedReply(readRequest, readResponse, true);
        if (address != 2099 || reply.EndCode != 0 || reply.Word != 1)
            throw new InvalidDataException("D2099=1 scan request was not actually confirmed.");
        return Append(attemptId, FinalOutputAuditPhase.ScannerRequestVerified, 0,
            operationId, sid, address, 1, FinalOutputFinsEvidence.Sha256(readRequest),
            FinalOutputFinsEvidence.Sha256(readResponse));
    }

    /// <summary>
    /// Durable exact SID/op/packet-intent BEFORE transport send is attempted.
    /// If a crash follows, this is UNKNOWN, not proof that the packet was NOT sent.
    /// Step 2 cannot start without step 1 ACK AND readback verification.
    /// </summary>
    public FinalOutputAuditEvent PrepareStep(Guid attemptId, int stepNumber,
        Guid operationId, ReadOnlySpan<byte> actualWriteRequest)
    {
        var (sid, address, value) = FinalOutputFinsEvidence.WriteRequest(actualWriteRequest);
        return Append(attemptId, FinalOutputAuditPhase.StepPrepared, stepNumber,
            operationId, sid, address, value,
            FinalOutputFinsEvidence.Sha256(actualWriteRequest));
    }

    /// <summary>Call ONLY after UDP SendAsync returned successfully; not from onBeforeSend.</summary>
    public FinalOutputAuditEvent WireSent(Guid attemptId, int stepNumber, Guid operationId) =>
        Append(attemptId, FinalOutputAuditPhase.WireSent, stepNumber, operationId);

    /// <summary>
    /// Pairs a REAL returned FINS response to the original intent by request SHA,
    /// SID, route, opcode, DM/value. Non-zero FINS end code is a confirmed refusal,
    /// not an ACK success. Malformed/absent response must be marked AckUnknown.
    /// </summary>
    public FinalOutputAuditEvent RecordWriteReply(Guid attemptId, int stepNumber,
        Guid operationId, ReadOnlySpan<byte> writeRequest, ReadOnlySpan<byte> writeResponse)
    {
        var (sid, address, value) = FinalOutputFinsEvidence.WriteRequest(writeRequest);
        var reply = FinalOutputFinsEvidence.VerifiedReply(writeRequest, writeResponse, false);
        return Append(attemptId, reply.EndCode == 0
                ? FinalOutputAuditPhase.AckAccepted : FinalOutputAuditPhase.AckRejected,
            stepNumber, operationId, sid, address, value,
            FinalOutputFinsEvidence.Sha256(writeRequest),
            FinalOutputFinsEvidence.Sha256(writeResponse),
            detail: reply.EndCode == 0 ? "FINS_END_CODE_0000" : $"FINS_END_CODE_{reply.EndCode:X4}");
    }

    /// <summary>
    /// Distinguishes a valid ACK from a subsequent independent readback.
    /// A readback with the wrong actual word persists as ReadbackMismatch.
    /// </summary>
    public FinalOutputAuditEvent RecordReadback(Guid attemptId, int stepNumber,
        Guid readOperationId, ReadOnlySpan<byte> readRequest, ReadOnlySpan<byte> readResponse)
    {
        var (sid, address) = FinalOutputFinsEvidence.ReadRequest(readRequest);
        var reply = FinalOutputFinsEvidence.VerifiedReply(readRequest, readResponse, true);
        if (reply.EndCode != 0 || reply.Word is null)
            throw new InvalidDataException("Readback FINS response was refused; record uncertainty.");
        return Append(attemptId, FinalOutputAuditPhase.ReadbackVerified, stepNumber,
            readOperationId, sid, address, null,
            FinalOutputFinsEvidence.Sha256(readRequest),
            FinalOutputFinsEvidence.Sha256(readResponse), reply.Word);
    }

    /// <summary>
    /// ACK may have been lost AFTER the PLC applied the write. An exception or
    /// link loss must NEVER produce a NotSent proof or authorize a resend.
    /// </summary>
    public FinalOutputAuditEvent AckUnknown(Guid attemptId, int stepNumber,
        Guid operationId, string reason) =>
        Append(attemptId, FinalOutputAuditPhase.AckUnknown, stepNumber, operationId,
            detail: Reason(reason));

    public FinalOutputAuditEvent ReadbackUnknown(Guid attemptId, int stepNumber,
        Guid operationId, string reason) =>
        Append(attemptId, FinalOutputAuditPhase.ReadbackUnknown, stepNumber, operationId,
            detail: Reason(reason));

    /// <summary>
    /// ONLY use if the adapter positively knows no socket SendAsync was attempted.
    /// This is invalid once WireSent, an uncertain send or a reply is recorded.
    /// </summary>
    public FinalOutputAuditEvent ExplicitlyNotSent(Guid attemptId, int stepNumber,
        Guid operationId, string reason) =>
        Append(attemptId, FinalOutputAuditPhase.ExplicitlyNotSent, stepNumber, operationId,
            detail: Reason(reason));

    /// <summary>
    /// Correlate existing durable PeerLink outbox/inbox by its ORIGINAL MessageId.
    /// Caller must invoke only after the corresponding Core WAL transaction commits.
    /// </summary>
    public FinalOutputAuditEvent LinkPersistedPeerMessage(Guid attemptId,
        Guid messageId, string stage)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("A durable PeerLink MessageId is required.");
        return Append(attemptId, FinalOutputAuditPhase.PeerMessageLinked, 0,
            peerMessageId: messageId, detail: Reason(stage));
    }

    private static string Reason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 ||
            reason.Any(char.IsControl))
            throw new ArgumentException("A short non-control diagnostic reason is required.");
        return reason;
    }

    private FinalOutputAuditEvent Append(Guid attemptId, FinalOutputAuditPhase phase,
        int stepNumber, Guid? operationId = null, byte? sid = null,
        ushort? address = null, ushort? value = null,
        string? requestSha = null, string? responseSha = null,
        ushort? readback = null, Guid? peerMessageId = null, string? detail = null)
    {
        if (attemptId == Guid.Empty || operationId == Guid.Empty ||
            !Enum.IsDefined(phase) || stepNumber is < 0 or > 2)
            throw new ArgumentException("Invalid final-output event reference.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var intent = LoadIntent(db, tx, attemptId)
            ?? throw new KeyNotFoundException("Unknown final-output attempt.");
        if (intent.ProcessRunId != _processRunId)
            throw new InvalidOperationException(
                "FINAL_OUTPUT_RESTART_UNKNOWN: new process may READ but cannot auto-continue an old write.");
        var existing = LoadEvents(db, tx, attemptId);
        ValidateTransition(intent, existing, phase, stepNumber, operationId,
            sid, address, value, requestSha, readback, peerMessageId);
        var observed = new FinalOutputAuditEvent(0, Guid.NewGuid(), attemptId,
            phase, stepNumber, operationId, sid, address, value,
            requestSha, responseSha, readback, peerMessageId,
            DateTimeOffset.UtcNow, _processRunId,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, detail);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO final_output_audit_events(
                event_id,attempt_id,phase,step_number,operation_id,fins_sid,
                dm_address,value,request_sha256,response_sha256,readback_value,
                peer_message_id,utc,process_run_id,monotonic_tick,tick_frequency,detail)
            VALUES($event,$attempt,$phase,$step,$op,$sid,$addr,$value,$req,$reply,$readback,
                   $peer,$utc,$run,$tick,$freq,$detail)
            RETURNING sequence
            """;
        cmd.Parameters.AddWithValue("$event", observed.EventId.ToString("D"));
        cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        cmd.Parameters.AddWithValue("$phase", phase.ToString());
        cmd.Parameters.AddWithValue("$step", stepNumber);
        cmd.Parameters.AddWithValue("$op", (object?)operationId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sid", (object?)sid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addr", (object?)address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$req", (object?)requestSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reply", (object?)responseSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$readback", (object?)readback ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peer", (object?)peerMessageId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$utc", observed.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$run", _processRunId.ToString("D"));
        cmd.Parameters.AddWithValue("$tick", observed.MonotonicTick);
        cmd.Parameters.AddWithValue("$freq", observed.TickFrequency);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        long seq = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        tx.Commit();
        return observed with { Sequence = seq };
    }

    private static void ValidateTransition(FinalOutputIntentRow intent,
        IReadOnlyList<FinalOutputAuditEvent> events, FinalOutputAuditPhase phase,
        int step, Guid? op, byte? sid, ushort? address, ushort? value,
        string? requestSha, ushort? readback, Guid? peer)
    {
        bool Present(FinalOutputAuditPhase p, int n) =>
            events.Any(e => e.Phase == p && e.StepNumber == n);
        if (phase == FinalOutputAuditPhase.PeerMessageLinked)
        {
            if (step != 0 || peer is null)
                throw new InvalidOperationException("Peer evidence must carry MessageId.");
            return;
        }
        if (phase == FinalOutputAuditPhase.ScannerRequestVerified)
        {
            if (step != 0 || op is null || sid is null || address != 2099 ||
                value != 1 || requestSha is null ||
                Present(phase, 0) || events.Any(e => e.StepNumber > 0))
                throw new InvalidOperationException("Scanner D2099=1 proof must precede output.");
            return;
        }
        if (step is not (1 or 2))
            throw new InvalidOperationException("Output events require step 1 or 2.");
        var prepared = events.FirstOrDefault(e =>
            e.StepNumber == step && e.Phase == FinalOutputAuditPhase.StepPrepared);
        var stepEvents = events.Where(e => e.StepNumber == step).ToArray();
        bool Has(FinalOutputAuditPhase p) => stepEvents.Any(e => e.Phase == p);
        bool terminal = Has(FinalOutputAuditPhase.AckUnknown) ||
            Has(FinalOutputAuditPhase.AckRejected) ||
            Has(FinalOutputAuditPhase.ReadbackUnknown) ||
            Has(FinalOutputAuditPhase.ReadbackMismatch) ||
            Has(FinalOutputAuditPhase.ReadbackVerified) ||
            Has(FinalOutputAuditPhase.ExplicitlyNotSent);
        if (phase == FinalOutputAuditPhase.StepPrepared)
        {
            var expected = FinalOutputFinsEvidence.Expected(
                intent.Scenario, intent.Disposition, step);
            if (!Present(FinalOutputAuditPhase.ScannerRequestVerified, 0) ||
                stepEvents.Length != 0 ||
                (step == 2 && !Present(FinalOutputAuditPhase.ReadbackVerified, 1)) ||
                op is null || sid is null || address != expected.Address ||
                value != expected.Value || requestSha is null ||
                events.Any(e => e.OperationId == op))
                throw new InvalidOperationException(
                    "Final write requires scanner proof and exactly the expected ordered DM/value.");
            return;
        }
        if (prepared is null || op is null)
            throw new InvalidOperationException("No persisted pre-send step intent.");
        if (phase != FinalOutputAuditPhase.ReadbackVerified &&
            phase != FinalOutputAuditPhase.ReadbackMismatch &&
            phase != FinalOutputAuditPhase.ReadbackUnknown &&
            prepared.OperationId != op)
            throw new InvalidOperationException("FINS write OperationId changed mid-step.");
        switch (phase)
        {
            case FinalOutputAuditPhase.WireSent:
                if (Has(phase) || terminal) throw new InvalidOperationException("Duplicate/late wire send.");
                break;
            case FinalOutputAuditPhase.ExplicitlyNotSent:
                if (Has(FinalOutputAuditPhase.WireSent) || terminal)
                    throw new InvalidOperationException("Cannot claim explicitly NOT SENT after a possible send.");
                break;
            case FinalOutputAuditPhase.AckUnknown:
                if (terminal || Has(FinalOutputAuditPhase.AckAccepted))
                    throw new InvalidOperationException("Already resolved or uncertain output.");
                break;
            case FinalOutputAuditPhase.AckAccepted:
            case FinalOutputAuditPhase.AckRejected:
                if (!Has(FinalOutputAuditPhase.WireSent) ||
                    Has(FinalOutputAuditPhase.AckAccepted) || terminal ||
                    sid != prepared.FinsSid || address != prepared.DmAddress ||
                    value != prepared.Value || requestSha != prepared.RequestSha256)
                    throw new InvalidOperationException(
                        "Received FINS response does not match durable sent packet and step.");
                break;
            case FinalOutputAuditPhase.ReadbackVerified:
            case FinalOutputAuditPhase.ReadbackMismatch:
                if (!Has(FinalOutputAuditPhase.AckAccepted) || terminal ||
                    sid is null || address != prepared.DmAddress || readback is null)
                    throw new InvalidOperationException("Readback requires the actual ACK and matching DM.");
                if ((phase == FinalOutputAuditPhase.ReadbackVerified) !=
                    (readback == prepared.Value))
                    throw new InvalidOperationException("Readback stage disagrees with actual PLC word.");
                break;
            case FinalOutputAuditPhase.ReadbackUnknown:
                if (!Has(FinalOutputAuditPhase.AckAccepted) || terminal)
                    throw new InvalidOperationException("Unknown readback requires known write ACK.");
                break;
            default:
                throw new InvalidOperationException("Unsupported transition.");
        }
    }

    internal sealed record FinalOutputIntentRow(
        Guid AttemptId, Guid WorkpieceId, Guid TraceId, long Epoch, string ReleaseId,
        ushort Token, FinalOutputAuditScenario Scenario, FinalDisposition Disposition,
        Guid? PeerMessageId, Guid ProcessRunId, DateTimeOffset CreatedUtc);

    internal static FinalOutputIntentRow? LoadIntent(SqliteConnection db,
        SqliteTransaction? transaction, Guid attemptId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT attempt_id,workpiece_id,trace_id,epoch,release_id,token,scenario,
                   disposition,peer_message_id,process_run_id,created_utc
              FROM final_output_intents WHERE attempt_id=$id
            """;
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new FinalOutputIntentRow(
            Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)),
            r.GetInt64(3), r.GetString(4), checked((ushort)r.GetInt32(5)),
            Enum.Parse<FinalOutputAuditScenario>(r.GetString(6)),
            Enum.Parse<FinalDisposition>(r.GetString(7)),
            r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)),
            Guid.Parse(r.GetString(9)), DateTimeOffset.Parse(r.GetString(10),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    internal static FinalOutputAuditEvent[] LoadEvents(SqliteConnection db,
        SqliteTransaction? tx, Guid attemptId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT sequence,event_id,attempt_id,phase,step_number,operation_id,fins_sid,
                   dm_address,value,request_sha256,response_sha256,readback_value,
                   peer_message_id,utc,process_run_id,monotonic_tick,tick_frequency,detail
              FROM final_output_audit_events
             WHERE attempt_id=$id ORDER BY sequence
            """;
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        var result = new List<FinalOutputAuditEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new FinalOutputAuditEvent(
                r.GetInt64(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)),
                Enum.Parse<FinalOutputAuditPhase>(r.GetString(3)), r.GetInt32(4),
                r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
                r.IsDBNull(6) ? null : checked((byte)r.GetInt32(6)),
                r.IsDBNull(7) ? null : checked((ushort)r.GetInt32(7)),
                r.IsDBNull(8) ? null : checked((ushort)r.GetInt32(8)),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : checked((ushort)r.GetInt32(11)),
                r.IsDBNull(12) ? null : Guid.Parse(r.GetString(12)),
                DateTimeOffset.Parse(r.GetString(13),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Guid.Parse(r.GetString(14)), r.GetInt64(15), r.GetInt64(16),
                r.IsDBNull(17) ? null : r.GetString(17)));
        }
        return result.ToArray();
    }
}
