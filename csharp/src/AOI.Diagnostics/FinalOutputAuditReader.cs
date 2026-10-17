using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AOI.Diagnostics;

/// <summary>
/// Read-only inquiry of an existing Master final-output WAL. Missing evidence
/// is explicitly NOT success. Both sessions and events are never rewritten.
/// Peer MessageId is a correlation key; no ordering is inferred from separate
/// Master/Slave Stopwatch ticks.
/// </summary>
public sealed class FinalOutputAuditReader
{
    private readonly string _dbPath;

    public FinalOutputAuditReader(string simDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simDataRoot);
        _dbPath = Path.Combine(Path.GetFullPath(simDataRoot), "master",
            ProductionTimingJournal.FileName);
    }

    public FinalOutputAuditTrace? Read(Guid workpieceId)
    {
        if (workpieceId == Guid.Empty) throw new ArgumentException("A WorkpieceId is required.");
        using var db = OpenOrNull();
        if (db is null) return null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT attempt_id FROM final_output_intents WHERE workpiece_id=$id";
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        if (cmd.ExecuteScalar() is not string raw) return null;
        return ReadAttempt(db, Guid.Parse(raw));
    }

    /// <summary>Find any final output attempts linked to a durable PeerLink MessageId.</summary>
    public FinalOutputAuditTrace[] SearchByPeerMessageId(Guid messageId, int limit = 100)
    {
        if (messageId == Guid.Empty || limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        using var db = OpenOrNull();
        if (db is null) return [];
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT attempt_id FROM final_output_intents WHERE peer_message_id=$id
            UNION
            SELECT attempt_id FROM final_output_audit_events WHERE peer_message_id=$id
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$id", messageId.ToString("D"));
        cmd.Parameters.AddWithValue("$limit", limit);
        var ids = new List<Guid>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids.Select(id => ReadAttempt(db, id)).ToArray();
    }

    private SqliteConnection? OpenOrNull()
    {
        if (!File.Exists(_dbPath)) return null;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=3000";
        cmd.ExecuteNonQuery();
        // The older A7 timing-events.sqlite contains ONLY timing_events.
        // Never mutate it through a READ_ONLY query; journal creation migrates.
        using var schema = db.CreateCommand();
        schema.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
              AND name='final_output_intents'
            """;
        if (Convert.ToInt32(schema.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            db.Dispose();
            return null;
        }
        return db;
    }

    private static FinalOutputAuditTrace ReadAttempt(SqliteConnection db, Guid attemptId)
    {
        var intent = FinalOutputAuditJournal.LoadIntent(db, null, attemptId)
            ?? throw new InvalidDataException("Lost final-output intent during read.");
        var events = FinalOutputAuditJournal.LoadEvents(db, null, attemptId);
        if (events.Any(e => e.AttemptId != attemptId ||
            e.ProcessRunId != intent.ProcessRunId ||
            e.TickFrequency <= 0 || e.EventId == Guid.Empty))
            throw new InvalidDataException("Invalid final-output event lineage/timebase.");

        var findings = new List<string>();
        bool scan = events.Any(e => e.Phase == FinalOutputAuditPhase.ScannerRequestVerified);
        if (!scan) findings.Add("END_SCANNER_D2099_REQUEST_NOT_VERIFIED");

        var steps = new FinalOutputAuditStep[2];
        for (int i = 1; i <= 2; i++)
        {
            var (expectedAddress, expectedValue) = FinalOutputFinsEvidence.Expected(
                intent.Scenario, intent.Disposition, i);
            var rows = events.Where(e => e.StepNumber == i).ToArray();
            FinalOutputAuditEvent? First(FinalOutputAuditPhase phase) =>
                rows.FirstOrDefault(e => e.Phase == phase);
            var prepared = First(FinalOutputAuditPhase.StepPrepared);
            var acknowledged = First(FinalOutputAuditPhase.AckAccepted);
            var refused = First(FinalOutputAuditPhase.AckRejected);
            var readOk = First(FinalOutputAuditPhase.ReadbackVerified);
            var readWrong = First(FinalOutputAuditPhase.ReadbackMismatch);
            var unsent = First(FinalOutputAuditPhase.ExplicitlyNotSent);
            var unknown = First(FinalOutputAuditPhase.AckUnknown);
            var readUnknown = First(FinalOutputAuditPhase.ReadbackUnknown);
            var sent = First(FinalOutputAuditPhase.WireSent);
            var state =
                readOk is not null ? FinalOutputAuditStepState.Verified :
                readWrong is not null ? FinalOutputAuditStepState.ReadbackMismatch :
                readUnknown is not null ? FinalOutputAuditStepState.ReadbackUnknown :
                refused is not null ? FinalOutputAuditStepState.AckRejected :
                unsent is not null ? FinalOutputAuditStepState.ExplicitlyNotSent :
                unknown is not null || sent is not null ?
                    FinalOutputAuditStepState.SentAckUnknown :
                acknowledged is not null ?
                    FinalOutputAuditStepState.AckAcceptedReadbackPending :
                prepared is not null ? FinalOutputAuditStepState.PreparedSendUncertain :
                FinalOutputAuditStepState.NotStarted;
            // AckAccepted without readback takes precedence over WireSent.
            if (acknowledged is not null && readOk is null && readWrong is null &&
                readUnknown is null)
                state = FinalOutputAuditStepState.AckAcceptedReadbackPending;

            var last = rows.LastOrDefault();
            string? detail = last?.Detail;
            steps[i - 1] = new FinalOutputAuditStep(
                i, expectedAddress, expectedValue, state,
                prepared?.OperationId, prepared?.FinsSid,
                prepared?.RequestSha256, acknowledged?.ResponseSha256 ?? refused?.ResponseSha256,
                readOk?.RequestSha256 ?? readWrong?.RequestSha256,
                readOk?.ResponseSha256 ?? readWrong?.ResponseSha256,
                readOk?.ReadbackValue ?? readWrong?.ReadbackValue, detail);
            switch (state)
            {
                case FinalOutputAuditStepState.Verified: break;
                case FinalOutputAuditStepState.NotStarted:
                    findings.Add($"STEP_{i}_NOT_OBSERVED"); break;
                case FinalOutputAuditStepState.PreparedSendUncertain:
                    findings.Add($"STEP_{i}_PREPARED_SEND_OUTCOME_UNKNOWN"); break;
                case FinalOutputAuditStepState.ExplicitlyNotSent:
                    findings.Add($"STEP_{i}_EXPLICITLY_NOT_SENT"); break;
                case FinalOutputAuditStepState.SentAckUnknown:
                    findings.Add($"STEP_{i}_SENT_ACK_UNKNOWN"); break;
                case FinalOutputAuditStepState.AckRejected:
                    findings.Add($"STEP_{i}_FINS_REFUSED"); break;
                case FinalOutputAuditStepState.AckAcceptedReadbackPending:
                    findings.Add($"STEP_{i}_ACKED_READBACK_UNVERIFIED"); break;
                case FinalOutputAuditStepState.ReadbackUnknown:
                    findings.Add($"STEP_{i}_READBACK_UNKNOWN"); break;
                case FinalOutputAuditStepState.ReadbackMismatch:
                    findings.Add($"STEP_{i}_READBACK_MISMATCH"); break;
            }
        }

        bool fullyVerified = scan && steps.All(s => s.State == FinalOutputAuditStepState.Verified);
        // ANY incomplete prior attempt requires manual reconciliation after a crash,
        // never an automatic write of the missing/uncertain step.
        return new FinalOutputAuditTrace(
            intent.AttemptId, intent.WorkpieceId, intent.TraceId, intent.Epoch,
            intent.ReleaseId, intent.Token, intent.Scenario, intent.Disposition,
            intent.PeerMessageId, intent.CreatedUtc, scan, steps, events,
            fullyVerified, !fullyVerified, findings.ToArray());
    }
}
