using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AOI.Diagnostics;

public enum CrossLedgerStatus
{
    CONFIRMED,
    INCOMPLETE,
    ACK_UNKNOWN,
    READBACK_UNKNOWN,
    AUDIT_MISSING,
    CONFLICT
}

public sealed record CrossLedgerFinding(string Code, string Description);

public sealed record CrossLedgerStepEvidence(
    int StepNumber, ushort DmAddress, ushort Value, Guid? ClaimId,
    byte? AuthoritySid, byte? AuditSid,
    string? RawRequestSha256, string? RawResponseSha256,
    string? ReadRequestSha256, string? ReadResponseSha256,
    ushort? ReadbackValue, bool BothLedgersConfirmed);

public sealed record CrossLedgerBatch(
    DateTimeOffset QueriedUtc, CrossLedgerReport[] Workpieces,
    bool Truncated, CrossLedgerStatus OverallStatus);

public sealed record CrossLedgerReport(
    Guid WorkpieceId, CrossLedgerStatus Status, Guid? A1IntentId,
    Guid? A7AttemptId, Guid? TraceId, long? Epoch, string? ReleaseId,
    ushort? PhysicalToken, FinalOutputAuditScenario? Scenario,
    FinalDisposition? InspectionDisposition, CrossLedgerStepEvidence[] Steps,
    CrossLedgerFinding[] Findings, bool RequiresManualReconciliation,
    bool SimOnly, bool RealPlcWriteAuthorized, bool PhysicalEjectionVerified);

/// <summary>
/// Fully read-only cross-ledger reconciliation. A1 is the authoritative
/// one-time write CLAIM; A7 independently stores exact actual FINS raw-packet
/// SHA256/SID, ACK and independent readback. One alone never proves success.
/// No A1 Runtime reference, no PLC, no restart, no synthetic datagram.
/// </summary>
public static class CrossLedgerReadOnlyReconciliation
{
    // A1 event JSON is projected using A0's existing Web JSON convention,
    // without referencing or editing the A1 authority assembly/DTO.
    private sealed record A1Intent(
        Guid IntentId, WorkpieceIdentity Workpiece, ushort Token,
        FinalOutputAuditScenario Scenario, FinalDisposition Disposition,
        DateTimeOffset CreatedUtc);
    private sealed record A1WireProof(
        Guid OperationId, byte Sid, ushort DmAddress, ushort Value,
        DateTimeOffset SentUtc, DateTimeOffset? AcknowledgedUtc,
        bool ReplyVerified);
    private sealed record A1Event(
        long Sequence, string Kind, Guid IntentId, Guid WorkpieceId,
        DateTimeOffset OccurredUtc, int StepIndex = -1,
        A1Intent? Intent = null, Guid? ClaimId = null,
        A1WireProof? WireProof = null, ushort? ReadbackValue = null,
        string? ReasonCode = null);

    private static string? Code(A1Event? e) => e?.Kind;

    /// <summary>
    /// Call against a STOPPED, quiescent SIM_ONLY run. Copies the original A1
    /// DB+WAL+SHM and A7 DB+WAL+SHM while holding exclusive file handles and
    /// A1's stopped-Core writer lease. SQLite opens ONLY disposable copies.
    /// A live Core returns CONFLICT/SOURCE_NOT_QUIESCENT without reading it.
    /// </summary>
    public static CrossLedgerReport Capture(string dataRoot, Guid workpieceId)
    {
        if (workpieceId == Guid.Empty) throw new ArgumentException("WorkpieceId is required.");
        try
        {
            using var snapshot = CrossLedgerSqliteSnapshot.Capture(dataRoot);
            return Inspect(snapshot.CopiedRoot, snapshot.A1Path, workpieceId);
        }
        catch (FileNotFoundException)
        {
            return Failure(workpieceId, CrossLedgerStatus.AUDIT_MISSING,
                "LEDGER_DATABASE_MISSING", "The A1 or A7 database does not exist.");
        }
        catch (IOException)
        {
            return Failure(workpieceId, CrossLedgerStatus.CONFLICT,
                "SOURCE_NOT_QUIESCENT",
                "Snapshot lock/copy failed; stop the Core and retry read-only inspection.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(workpieceId, CrossLedgerStatus.CONFLICT,
                "SOURCE_NOT_QUIESCENT",
                "Unable to lock all evidence files; original databases were not opened.");
        }
        catch (InvalidDataException)
        {
            return Failure(workpieceId, CrossLedgerStatus.CONFLICT,
                "INVALID_LEDGER_EVIDENCE", "An identity, schema or event invariant is invalid.");
        }
        catch (JsonException)
        {
            return Failure(workpieceId, CrossLedgerStatus.CONFLICT,
                "INVALID_LEDGER_JSON", "Persisted event JSON cannot be trusted.");
        }
        catch (SqliteException)
        {
            return Failure(workpieceId, CrossLedgerStatus.CONFLICT,
                "INVALID_SQLITE_SNAPSHOT", "The copied WAL pair could not be read consistently.");
        }
    }

    /// <summary>
    /// Bounded operator/offline batch: copy the original DB+WAL+SHM ONCE,
    /// reconcile distinct physical WorkpieceIds (not barcodes) in the same
    /// stopped run, and never report a truncated batch as a complete run.
    /// Requires the same stopped-Core lease as Capture().
    /// </summary>
    public static CrossLedgerBatch CaptureAll(string dataRoot, int limit = 200)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        using var snapshot = CrossLedgerSqliteSnapshot.Capture(dataRoot);
        var ids = new List<Guid>();
        using (var db = OpenCopied(snapshot.A1Path))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT workpiece_id FROM a1_final_output_events
                 ORDER BY workpiece_id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(Guid.Parse(reader.GetString(0)));
        }
        bool truncated = ids.Count > limit;
        var reports = ids.Take(limit)
            .Select(id =>
            {
                try { return Inspect(snapshot.CopiedRoot, snapshot.A1Path, id); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or SqliteException)
                {
                    return Failure(id, CrossLedgerStatus.CONFLICT,
                        "INVALID_LEDGER_EVIDENCE", "This workpiece has malformed evidence.");
                }
            }).ToArray();
        CrossLedgerStatus overall = truncated ? CrossLedgerStatus.INCOMPLETE :
            reports.Length == 0 ? CrossLedgerStatus.AUDIT_MISSING :
            reports.MaxBy(r => Rank(r.Status))!.Status;
        return new CrossLedgerBatch(DateTimeOffset.UtcNow, reports, truncated, overall);
    }

    private static CrossLedgerReport Failure(Guid id, CrossLedgerStatus status,
        string code, string description) =>
        new(id, status, null, null, null, null, null, null, null, null,
            [], [new CrossLedgerFinding(code, description)],
            RequiresManualReconciliation: true, SimOnly: true,
            RealPlcWriteAuthorized: false, PhysicalEjectionVerified: false);

    private static SqliteConnection OpenCopied(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        return db;
    }

    private static A1Event[] ReadAuthority(string path, Guid workpieceId)
    {
        using var db = OpenCopied(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT sequence,intent_id,workpiece_id,event_json
              FROM a1_final_output_events WHERE workpiece_id=$id
             ORDER BY sequence LIMIT 65
            """;
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        var events = new List<A1Event>();
        long prior = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long seq = reader.GetInt64(0);
            string intent = reader.GetString(1);
            string item = reader.GetString(2);
            var entry = DiagnosticsFormat.Parse<A1Event>(reader.GetString(3));
            if (seq <= prior || seq != entry.Sequence ||
                entry.IntentId == Guid.Empty ||
                !string.Equals(intent, entry.IntentId.ToString("D"), StringComparison.Ordinal) ||
                !string.Equals(item, entry.WorkpieceId.ToString("D"), StringComparison.Ordinal) ||
                entry.WorkpieceId != workpieceId ||
                entry.OccurredUtc == default || string.IsNullOrWhiteSpace(entry.Kind))
                throw new InvalidDataException("A1 event identity, sort or sequence invalid.");
            prior = seq;
            events.Add(entry);
        }
        if (events.Count > 64)
            throw new InvalidDataException("A1 event bound exceeded.");
        return events.ToArray();
    }

    private static CrossLedgerReport Inspect(string snapshotRoot, string a1Path,
        Guid workpieceId)
    {
        var authority = ReadAuthority(a1Path, workpieceId);
        if (authority.Length == 0)
            return Failure(workpieceId, CrossLedgerStatus.AUDIT_MISSING,
                "A1_OUTPUT_INTENT_MISSING",
                "No authoritative A1 final-output intent for this WorkpieceId.");

        var initial = authority[0];
        var intent = initial.Intent;
        if (initial.Kind != "intentPrepared" || intent is null ||
            initial.StepIndex != -1 ||
            authority.Count(e => e.Kind == "intentPrepared") != 1 ||
            initial.IntentId != intent.IntentId || intent.Workpiece is null)
            throw new InvalidDataException("A1 intent missing, duplicated or structurally invalid.");
        intent.Workpiece.Validate();
        if (intent.Workpiece.WorkpieceId != workpieceId || intent.Token == 0 ||
            authority.Any(e => e.IntentId != intent.IntentId || e.Kind is not
                ("intentPrepared" or "writeClaimed" or "wireSendObserved" or
                 "finsAckObserved" or "readbackConfirmed" or "outcomeUnknown")))
            throw new InvalidDataException("A1 intent lineage or event kind mismatch.");

        var audit = new FinalOutputAuditReader(snapshotRoot).Read(workpieceId);
        if (audit is null)
            return new CrossLedgerReport(workpieceId,
                CrossLedgerStatus.AUDIT_MISSING, intent.IntentId, null,
                intent.Workpiece.TraceId, intent.Workpiece.Epoch, intent.Workpiece.ReleaseId,
                intent.Token, intent.Scenario, intent.Disposition, [],
                [new("A7_OUTPUT_ATTEMPT_MISSING", "A1 exists but the independent A7 attempt is absent.")],
                true, true, false, false);

        var notes = new List<(CrossLedgerStatus Status, CrossLedgerFinding Finding)>();
        void Add(CrossLedgerStatus status, string code, string detail) =>
            notes.Add((status, new CrossLedgerFinding(code, detail)));

        if (audit.WorkpieceId != intent.Workpiece.WorkpieceId ||
            audit.TraceId != intent.Workpiece.TraceId ||
            audit.Epoch != intent.Workpiece.Epoch ||
            audit.ReleaseId != intent.Workpiece.ReleaseId ||
            audit.Token != intent.Token || audit.Scenario != intent.Scenario ||
            audit.Disposition != intent.Disposition)
            Add(CrossLedgerStatus.CONFLICT, "WORKPIECE_IDENTITY_CONFLICT",
                "A1/A7 WorkpieceId, TraceId, Epoch, ReleaseId, token, scenario or disposition differs.");

        var events = audit.Events;
        if (events.Length > 64 || events.Any(x => x.AttemptId != audit.AttemptId))
            Add(CrossLedgerStatus.CONFLICT, "A7_EVENT_LINEAGE_INVALID",
                "A7 event count or attempt linkage is not valid.");

        var scan = events.Where(e => e.Phase == FinalOutputAuditPhase.ScannerRequestVerified).ToArray();
        if (scan.Length == 0)
            Add(CrossLedgerStatus.AUDIT_MISSING, "SCANNER_REQUEST_NOT_AUDITED",
                "No raw FINS D2099=1 scanner response is observed.");
        else if (scan.Length != 1 || scan[0].StepNumber != 0 ||
            scan[0].DmAddress != 2099 || scan[0].Value != 1 ||
            scan[0].OperationId is null || scan[0].FinsSid is null ||
            !Hash(scan[0].RequestSha256) || !Hash(scan[0].ResponseSha256))
            Add(CrossLedgerStatus.CONFLICT, "SCANNER_EVIDENCE_CONFLICT",
                "D2099 scanner request or raw packet hashes are malformed or repeated.");

        var claims = authority.Where(e => e.Kind == "writeClaimed").ToArray();
        if (claims.Any(e => e.ClaimId is null || e.ClaimId == Guid.Empty) ||
            claims.Where(e => e.ClaimId is not null).Select(e => e.ClaimId).Distinct().Count()
                != claims.Length)
            Add(CrossLedgerStatus.CONFLICT, "A1_CLAIM_REUSED",
                "A1 physical ClaimId is missing or duplicated.");
        var a7Prepares = events.Where(e => e.Phase == FinalOutputAuditPhase.StepPrepared).ToArray();
        if (a7Prepares.Where(e => e.OperationId is not null)
                .Select(e => e.OperationId).Distinct().Count() != a7Prepares.Length)
            Add(CrossLedgerStatus.CONFLICT, "A7_OPERATION_REUSED",
                "A7 output operation is missing or duplicated.");

        var result = new List<CrossLedgerStepEvidence>(2);
        for (int stepNumber = 1; stepNumber <= 2; stepNumber++)
        {
            int index = stepNumber - 1;
            var (dm, value) = FinalOutputFinsEvidence.Expected(
                intent.Scenario, intent.Disposition, stepNumber);
            var one = authority.Where(e => e.StepIndex == index).ToArray();
            var two = events.Where(e => e.StepNumber == stepNumber).ToArray();
            var claimed = OnlyA1(one, "writeClaimed", stepNumber, Add);
            var sent = OnlyA1(one, "wireSendObserved", stepNumber, Add);
            var acknowledged = OnlyA1(one, "finsAckObserved", stepNumber, Add);
            var readback = OnlyA1(one, "readbackConfirmed", stepNumber, Add);
            var unknown = OnlyA1(one, "outcomeUnknown", stepNumber, Add);
            var prepared = OnlyA7(two, FinalOutputAuditPhase.StepPrepared, stepNumber, Add);
            var wire = OnlyA7(two, FinalOutputAuditPhase.WireSent, stepNumber, Add);
            var ack = OnlyA7(two, FinalOutputAuditPhase.AckAccepted, stepNumber, Add);
            var reject = OnlyA7(two, FinalOutputAuditPhase.AckRejected, stepNumber, Add);
            var ackUnknown = OnlyA7(two, FinalOutputAuditPhase.AckUnknown, stepNumber, Add);
            var read = OnlyA7(two, FinalOutputAuditPhase.ReadbackVerified, stepNumber, Add);
            var mismatch = OnlyA7(two, FinalOutputAuditPhase.ReadbackMismatch, stepNumber, Add);
            var readUnknown = OnlyA7(two, FinalOutputAuditPhase.ReadbackUnknown, stepNumber, Add);
            var notSent = OnlyA7(two, FinalOutputAuditPhase.ExplicitlyNotSent, stepNumber, Add);

            if (claimed is null && prepared is null)
            {
                if (one.Length != 0 || two.Length != 0)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_ORPHAN_EVENT",
                        "Evidence exists without a claimed operation and prepared packet.");
                else
                    Add(CrossLedgerStatus.INCOMPLETE, $"STEP_{stepNumber}_NOT_STARTED",
                        "No independent A1/A7 second-step write proof. Never infer it from DM final values.");
            }
            else if (claimed is null || prepared is null)
                Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_CLAIM_AUDIT_MISSING",
                    "A1 durable write claim and A7 prepared raw packet must BOTH exist.");
            else
            {
                if (claimed.ClaimId is null || claimed.ClaimId == Guid.Empty ||
                    prepared.OperationId != claimed.ClaimId ||
                    prepared.DmAddress != dm || prepared.Value != value ||
                    prepared.FinsSid is null || !Hash(prepared.RequestSha256))
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_CLAIM_OPERATION_CONFLICT",
                        "A1 ClaimId != A7 OperationId, or raw FINS SID/DM/value/hash invalid.");
                if (stepNumber == 2)
                {
                    var previousA1 = authority.FirstOrDefault(e =>
                        e.Kind == "readbackConfirmed" && e.StepIndex == 0);
                    var previousA7 = events.FirstOrDefault(e =>
                        e.Phase == FinalOutputAuditPhase.ReadbackVerified && e.StepNumber == 1);
                    if (previousA1 is null || previousA7 is null ||
                        previousA1.Sequence >= claimed.Sequence ||
                        previousA7.Sequence >= prepared.Sequence)
                        Add(CrossLedgerStatus.CONFLICT, "STEP_2_BEFORE_STEP_1_VERIFIED",
                            "Step 2 must follow independently confirmed step 1 in each ledger.");
                }
            }

            if (sent is not null)
            {
                var proof = sent.WireProof;
                if (claimed is null || proof is null ||
                    sent.ClaimId != claimed.ClaimId ||
                    proof.OperationId != claimed.ClaimId ||
                    proof.DmAddress != dm || proof.Value != value ||
                    proof.SentUtc == default || proof.AcknowledgedUtc is not null ||
                    proof.ReplyVerified ||
                    (prepared is not null && proof.Sid != prepared.FinsSid) ||
                    (claimed is not null && sent.Sequence <= claimed.Sequence))
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_SID_OR_SEND_CONFLICT",
                        "A1 persisted send proof does not match its claim/A7 FINS SID/DM/value.");
            }
            if (wire is not null)
            {
                if (prepared is null || wire.OperationId != prepared.OperationId ||
                    wire.Sequence <= prepared.Sequence)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_WIRE_EVENT_CONFLICT",
                        "A7 actual UDP SendAsync event cannot be paired to prepared packet.");
                if (sent is null)
                    Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_A1_SEND_MISSING",
                        "A7 reports an actual send but A1 send proof is absent.");
            }
            if (notSent is not null || reject is not null || mismatch is not null)
                Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_OUTPUT_REJECTED_OR_MISMATCH",
                    "A7 positively observed NOT_SENT, rejected FINS or wrong independent readback.");

            if (acknowledged is not null)
            {
                var proof = acknowledged.WireProof;
                if (sent is null || sent.WireProof is not { } prior ||
                    acknowledged.ClaimId != claimed?.ClaimId ||
                    proof is null || proof.OperationId != prior.OperationId ||
                    proof.Sid != prior.Sid || proof.DmAddress != prior.DmAddress ||
                    proof.Value != prior.Value || proof.SentUtc != prior.SentUtc ||
                    proof.AcknowledgedUtc is null || !proof.ReplyVerified ||
                    acknowledged.Sequence <= sent.Sequence)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_A1_ACK_CONFLICT",
                        "A1 ACK does not match the persisted SID/ClaimId/send.");
                if (ack is null)
                    Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_A7_ACK_MISSING",
                        "A1 ACK exists without independent A7 raw response evidence.");
            }
            if (ack is not null)
            {
                if (wire is null || prepared is null || ack.OperationId != prepared.OperationId ||
                    ack.FinsSid != prepared.FinsSid || ack.DmAddress != dm ||
                    ack.Value != value || ack.RequestSha256 != prepared.RequestSha256 ||
                    !Hash(ack.ResponseSha256) || ack.Sequence <= wire.Sequence)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_RAW_ACK_CONFLICT",
                        "A7 actual response SID/DM/value/raw SHA does not match sent packet.");
                if (acknowledged is null)
                    Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_A1_ACK_MISSING",
                        "Raw A7 ACK exists but A1 one-time authority did not confirm it.");
            }
            if (ack is not null && ackUnknown is not null ||
                read is not null && readUnknown is not null ||
                unknown is not null && readback is not null)
                Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_TERMINAL_STATE_CONFLICT",
                    "Confirmed and unknown terminal events cannot both assert this step.");

            if (readback is not null)
            {
                if (acknowledged is null || readback.ClaimId != claimed?.ClaimId ||
                    readback.ReadbackValue != value ||
                    readback.Sequence <= acknowledged.Sequence)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_A1_READBACK_CONFLICT",
                        "A1 readback was not causally confirmed by its ACK/claim.");
                if (read is null)
                    Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_A7_READBACK_MISSING",
                        "A1 confirmed a readback but A7 has no independent raw readback.");
            }
            if (read is not null)
            {
                if (ack is null || read.DmAddress != dm ||
                    read.ReadbackValue != value ||
                    read.OperationId is null || read.FinsSid is null ||
                    !Hash(read.RequestSha256) || !Hash(read.ResponseSha256) ||
                    read.Sequence <= ack.Sequence)
                    Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_RAW_READBACK_CONFLICT",
                        "A7 raw readback hashes/address/actual value do not prove the expected word.");
                if (readback is null)
                    Add(CrossLedgerStatus.AUDIT_MISSING, $"STEP_{stepNumber}_A1_READBACK_MISSING",
                        "A7 readback exists but A1 never confirmed the one-time claim.");
            }

            if (claimed is not null && acknowledged is null &&
                ack is null && reject is null && notSent is null)
                Add(CrossLedgerStatus.ACK_UNKNOWN, $"STEP_{stepNumber}_ACK_UNKNOWN",
                    "One-time claim may have reached PLC; absent/rejected ACK does not authorize retry.");
            else if (ack is not null && read is null ||
                     acknowledged is not null && readback is null ||
                     readUnknown is not null)
                Add(CrossLedgerStatus.READBACK_UNKNOWN, $"STEP_{stepNumber}_READBACK_UNKNOWN",
                    "A confirmed FINS reply alone is insufficient without independent matching readback.");
            if (unknown is not null && string.IsNullOrWhiteSpace(unknown.ReasonCode))
                Add(CrossLedgerStatus.CONFLICT, $"STEP_{stepNumber}_UNKNOWN_REASON_MISSING",
                    "An authoritative A1 unknown event must retain its original reason.");

            result.Add(new CrossLedgerStepEvidence(stepNumber, dm, value,
                claimed?.ClaimId, sent?.WireProof?.Sid, prepared?.FinsSid,
                prepared?.RequestSha256, ack?.ResponseSha256,
                read?.RequestSha256, read?.ResponseSha256,
                read?.ReadbackValue, claimed is not null && prepared is not null &&
                sent is not null && wire is not null && acknowledged is not null &&
                ack is not null && readback is not null && read is not null &&
                unknown is null && reject is null && mismatch is null &&
                notSent is null && ackUnknown is null && readUnknown is null));
        }

        if (authority.Any(e => e.StepIndex is < -1 or > 1))
            Add(CrossLedgerStatus.CONFLICT, "A1_STEP_INDEX_INVALID",
                "A1 output events contain a step other than 0/1.");
        if (events.Any(e => e.StepNumber is < 0 or > 2))
            Add(CrossLedgerStatus.CONFLICT, "A7_STEP_NUMBER_INVALID",
                "A7 output events contain an unknown step.");
        if (result.Any(s => !s.BothLedgersConfirmed) && notes.Count == 0)
            Add(CrossLedgerStatus.INCOMPLETE, "OUTPUT_NOT_CONFIRMED",
                "Both complete, independent output steps have not been established.");

        CrossLedgerStatus status = notes.Count == 0
            ? CrossLedgerStatus.CONFIRMED
            : notes.MaxBy(n => Rank(n.Status)).Status;
        return new CrossLedgerReport(workpieceId, status, intent.IntentId,
            audit.AttemptId, intent.Workpiece.TraceId, intent.Workpiece.Epoch,
            intent.Workpiece.ReleaseId, intent.Token, intent.Scenario, intent.Disposition,
            result.ToArray(), notes.Select(n => n.Finding)
                .DistinctBy(n => n.Code).OrderBy(n => n.Code, StringComparer.Ordinal).ToArray(),
            status != CrossLedgerStatus.CONFIRMED, true, false, false);
    }

    private static int Rank(CrossLedgerStatus status) => status switch
    {
        CrossLedgerStatus.CONFLICT => 50,
        CrossLedgerStatus.AUDIT_MISSING => 40,
        CrossLedgerStatus.READBACK_UNKNOWN => 30,
        CrossLedgerStatus.ACK_UNKNOWN => 20,
        CrossLedgerStatus.INCOMPLETE => 10,
        _ => 0
    };

    private static bool Hash(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static A1Event? OnlyA1(A1Event[] events, string kind, int step,
        Action<CrossLedgerStatus, string, string> add)
    {
        var found = events.Where(x => x.Kind == kind).ToArray();
        if (found.Length > 1)
            add(CrossLedgerStatus.CONFLICT, $"STEP_{step}_DUPLICATE_A1_{kind}",
                "A1 contains multiple events of one irreversible kind for one physical step.");
        return found.FirstOrDefault();
    }

    private static FinalOutputAuditEvent? OnlyA7(
        FinalOutputAuditEvent[] events, FinalOutputAuditPhase phase, int step,
        Action<CrossLedgerStatus, string, string> add)
    {
        var found = events.Where(x => x.Phase == phase).ToArray();
        if (found.Length > 1)
            add(CrossLedgerStatus.CONFLICT, $"STEP_{step}_DUPLICATE_A7_{phase}",
                "A7 contains repeated physical FINS phase evidence.");
        return found.FirstOrDefault();
    }
}
