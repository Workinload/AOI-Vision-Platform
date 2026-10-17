using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace AOI.Runtime;

/// <summary>One independent WAL database per Core. Inbox is durable before PERSISTED_ACK.</summary>
public sealed class ProductionStore
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public ProductionStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var pragmas = db.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        pragmas.ExecuteNonQuery();
        return db;
    }

    private void Initialize()
    {
        using var db = Open();
        using var init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS workpieces (
              workpiece_id TEXT PRIMARY KEY, barcode TEXT NOT NULL,
              epoch INTEGER NOT NULL, release_id TEXT NOT NULL, identity_json TEXT NOT NULL,
              created_utc TEXT NOT NULL, final_json TEXT);
            CREATE TABLE IF NOT EXISTS channel_results (
              result_id TEXT PRIMARY KEY, workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
              channel_id TEXT NOT NULL, epoch INTEGER NOT NULL, release_id TEXT NOT NULL,
              attempt_no INTEGER NOT NULL, result_json TEXT NOT NULL,
              UNIQUE(workpiece_id, channel_id, epoch, release_id, attempt_no));
            CREATE TABLE IF NOT EXISTS inbox (
              sender_node_id TEXT NOT NULL, message_id TEXT NOT NULL, payload_hash TEXT NOT NULL,
              envelope_hash TEXT, received_utc TEXT NOT NULL,
              PRIMARY KEY(sender_node_id,message_id));
            CREATE TABLE IF NOT EXISTS outbox (
              message_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, envelope_json TEXT NOT NULL,
              created_utc TEXT NOT NULL, acknowledged_utc TEXT);
            CREATE TABLE IF NOT EXISTS flight_events (
              event_id TEXT PRIMARY KEY, utc TEXT NOT NULL, kind TEXT NOT NULL,
              workpiece_id TEXT, message_id TEXT, details TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS capture_triggers (
              capture_id TEXT PRIMARY KEY,
              workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
              physical_device_id TEXT NOT NULL,
              attempt_no INTEGER NOT NULL,
              trigger_json TEXT NOT NULL, processed_utc TEXT,
              UNIQUE(workpiece_id,physical_device_id,attempt_no));
            CREATE INDEX IF NOT EXISTS ix_pending_capture_triggers
              ON capture_triggers(processed_utc,workpiece_id);
            CREATE TABLE IF NOT EXISTS workpiece_holds (
              workpiece_id TEXT PRIMARY KEY REFERENCES workpieces(workpiece_id),
              reason_code TEXT NOT NULL, detail TEXT NOT NULL,
              raised_utc TEXT NOT NULL);
            """;
        init.ExecuteNonQuery();

        // Older SIM_LIGHT databases have no envelope_hash column. Preserve the WAL
        // without authorizing legacy duplicate ACKs we cannot cryptographically verify.
        using var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info(inbox)";
        bool hasEnvelopeHash = false;
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
                if (string.Equals(reader.GetString(1), "envelope_hash", StringComparison.Ordinal))
                    hasEnvelopeHash = true;
        }
        if (!hasEnvelopeHash)
        {
            using var upgrade = db.CreateCommand();
            upgrade.CommandText = "ALTER TABLE inbox ADD COLUMN envelope_hash TEXT";
            upgrade.ExecuteNonQuery();
        }
    }

    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] args)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void InsertWorkpiece(SqliteConnection db, SqliteTransaction tx, WorkpieceIdentity workpiece)
    {
        workpiece.Validate();
        using var existing = Cmd(db, tx, "SELECT identity_json FROM workpieces WHERE workpiece_id=$id",
            ("$id", workpiece.WorkpieceId.ToString("D")));
        var old = existing.ExecuteScalar() as string;
        var identityJson = WireFormat.Serialize(workpiece);
        if (old is not null)
        {
            if (!string.Equals(old, identityJson, StringComparison.Ordinal))
                throw new InvalidOperationException("Existing WorkpieceId has different identity; duplicate barcode is allowed, duplicate ID is not.");
            return;
        }
        using var cmd = Cmd(db, tx, """
            INSERT INTO workpieces(workpiece_id,barcode,epoch,release_id,identity_json,created_utc)
            VALUES ($id,$barcode,$epoch,$release,$identity,$created)
            """,
            ("$id", workpiece.WorkpieceId.ToString("D")), ("$barcode", workpiece.Barcode),
            ("$epoch", workpiece.Epoch), ("$release", workpiece.ReleaseId),
            ("$identity", identityJson), ("$created", workpiece.CreatedAtUtc.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    private static WorkpieceIdentity RequireWorkpiece(SqliteConnection db, SqliteTransaction tx, Guid workpieceId)
    {
        using var cmd = Cmd(db, tx, "SELECT identity_json FROM workpieces WHERE workpiece_id=$id",
            ("$id", workpieceId.ToString("D")));
        var json = cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException("Result arrived before its workpiece registration; fail closed.");
        return WireFormat.Parse<WorkpieceIdentity>(json);
    }

    private static void InsertResult(SqliteConnection db, SqliteTransaction tx, ChannelResult result)
    {
        result.ValidateFor(RequireWorkpiece(db, tx, result.WorkpieceId));
        var json = WireFormat.Serialize(result);
        using var existing = Cmd(db, tx, """
            SELECT result_json FROM channel_results
            WHERE workpiece_id=$id AND channel_id=$channel AND epoch=$epoch
              AND release_id=$release AND attempt_no=$attempt
            """,
            ("$id", result.WorkpieceId.ToString("D")), ("$channel", result.ChannelId),
            ("$epoch", result.Epoch), ("$release", result.ReleaseId), ("$attempt", result.AttemptNo));
        var prior = existing.ExecuteScalar() as string;
        if (prior is not null)
        {
            if (!string.Equals(prior, json, StringComparison.Ordinal))
                throw new InvalidDataException("Same workpiece/channel/attempt produced conflicting results.");
            return;
        }
        using var cmd = Cmd(db, tx, """
            INSERT INTO channel_results(result_id,workpiece_id,channel_id,epoch,release_id,attempt_no,result_json)
            VALUES ($result,$id,$channel,$epoch,$release,$attempt,$json)
            """,
            ("$result", result.ResultId.ToString("D")), ("$id", result.WorkpieceId.ToString("D")),
            ("$channel", result.ChannelId), ("$epoch", result.Epoch),
            ("$release", result.ReleaseId), ("$attempt", result.AttemptNo), ("$json", json));
        cmd.ExecuteNonQuery();
    }

    private static void Queue(SqliteConnection db, SqliteTransaction tx, PeerEnvelope envelope)
    {
        envelope.Validate();
        using var cmd = Cmd(db, tx, """
            INSERT INTO outbox(message_id,payload_hash,envelope_json,created_utc)
            VALUES ($id,$hash,$json,$utc)
            """,
            ("$id", envelope.MessageId.ToString("D")), ("$hash", envelope.PayloadHash),
            ("$json", WireFormat.Serialize(envelope)), ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    private static void Event(SqliteConnection db, SqliteTransaction tx, string kind,
        Guid? workpieceId, Guid? messageId, string details)
    {
        using var cmd = Cmd(db, tx, """
            INSERT INTO flight_events(event_id,utc,kind,workpiece_id,message_id,details)
            VALUES ($id,$utc,$kind,$workpiece,$message,$details)
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$utc", DateTimeOffset.UtcNow.ToString("O")),
            ("$kind", kind), ("$workpiece", workpieceId?.ToString("D")),
            ("$message", messageId?.ToString("D")), ("$details", details));
        cmd.ExecuteNonQuery();
    }

    public void CreateAndQueueRegistration(WorkpieceIdentity workpiece, PeerEnvelope outbound)
    {
        if (outbound.PayloadType != "workpiece_registered" || outbound.WorkpieceId != workpiece.WorkpieceId)
            throw new InvalidDataException("Registration outbox identity mismatch.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        InsertWorkpiece(db, tx, workpiece);
        Queue(db, tx, outbound);
        Event(db, tx, "WORKPIECE_REGISTERED_LOCAL", workpiece.WorkpieceId, outbound.MessageId, "SIM_ONLY");
        tx.Commit();
    }

    public void SaveLocalResult(ChannelResult result, PeerEnvelope? outbound = null)
    {
        if (outbound is not null && (outbound.PayloadType != "channel_result" || outbound.WorkpieceId != result.WorkpieceId))
            throw new InvalidDataException("Result outbox identity mismatch.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        InsertResult(db, tx, result);
        if (outbound is not null) Queue(db, tx, outbound);
        Event(db, tx, "CHANNEL_RESULT_LOCAL", result.WorkpieceId, outbound?.MessageId,
            $"{result.ChannelId}:{result.Outcome}:sim={result.IsSimulated}");
        tx.Commit();
    }


    /// <summary>
    /// Add a Slave-owned sensor event to Master's existing durable PeerLink
    /// outbox. The immutable identity is persisted before it can be sent.
    /// </summary>
    public void QueueCaptureTrigger(PhysicalCaptureTrigger trigger, PeerEnvelope outbound)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        trigger.Validate();
        outbound.Validate();
        if (trigger.OwnerRole != MachineRole.Slave ||
            outbound.SenderNodeId != "master" || outbound.ReceiverNodeId != "slave" ||
            outbound.PayloadType != "capture_trigger" ||
            outbound.WorkpieceId != trigger.Workpiece.WorkpieceId ||
            outbound.Epoch != trigger.Workpiece.Epoch ||
            outbound.TraceId != trigger.Workpiece.TraceId ||
            outbound.SolutionReleaseId != trigger.Workpiece.ReleaseId ||
            outbound.ChannelId != trigger.Channels[0].ChannelId ||
            outbound.StationId != trigger.StationId ||
            outbound.PayloadJson != WireFormat.Serialize(trigger))
            throw new InvalidDataException("Unowned, forged or mismatched slave capture trigger.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (RequireWorkpiece(db, tx, trigger.Workpiece.WorkpieceId) != trigger.Workpiece)
            throw new InvalidDataException("Capture trigger immutable identity changed.");
        Queue(db, tx, outbound);
        Event(db, tx, "CAPTURE_TRIGGER_MASTER_QUEUED", trigger.Workpiece.WorkpieceId,
            outbound.MessageId, trigger.PhysicalDeviceId + ":" + trigger.CaptureId);
        tx.Commit();
    }

    /// <summary>
    /// Atomically persist the Slave's bounded per-workpiece HOLD and its durable
    /// outbound camera-fault message. A crash cannot leave a completed failure
    /// locally without a replayable Master notification.
    /// </summary>
    public void HoldAndQueueCaptureFault(PhysicalCaptureFault fault, PeerEnvelope outbound)
    {
        ArgumentNullException.ThrowIfNull(fault);
        fault.Validate();
        outbound.Validate();
        var trigger = fault.Trigger;
        var item = trigger.Workpiece;
        if (outbound.SenderNodeId != "slave" || outbound.ReceiverNodeId != "master" ||
            outbound.PayloadType != "capture_fault" ||
            outbound.WorkpieceId != item.WorkpieceId ||
            outbound.Epoch != item.Epoch || outbound.TraceId != item.TraceId ||
            outbound.SolutionReleaseId != item.ReleaseId ||
            outbound.ChannelId != trigger.Channels[0].ChannelId ||
            outbound.StationId != trigger.StationId ||
            outbound.PayloadJson != WireFormat.Serialize(fault))
            throw new InvalidDataException("Capture fault outbox identity or camera owner mismatch.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (RequireWorkpiece(db, tx, item.WorkpieceId) != item)
            throw new InvalidDataException("Capture fault must match registered Slave identity.");
        using (var final = Cmd(db, tx,
            "SELECT final_json FROM workpieces WHERE workpiece_id=$id",
            ("$id", item.WorkpieceId.ToString("D"))))
            if (final.ExecuteScalar() is string)
                throw new InvalidOperationException("Cannot hold an already finalized workpiece.");
        using (var find = Cmd(db, tx,
            "SELECT reason_code,detail FROM workpiece_holds WHERE workpiece_id=$id",
            ("$id", item.WorkpieceId.ToString("D"))))
        using (var reader = find.ExecuteReader())
            if (reader.Read() && (reader.GetString(0) != fault.ReasonCode ||
                                  reader.GetString(1) != fault.Detail))
                throw new InvalidDataException("Conflicting Slave workpiece HOLD reasons.");

        using (var add = Cmd(db, tx, """
            INSERT INTO workpiece_holds(workpiece_id,reason_code,detail,raised_utc)
            VALUES($id,$code,$detail,$utc)
            ON CONFLICT(workpiece_id) DO NOTHING
            """, ("$id", item.WorkpieceId.ToString("D")),
            ("$code", fault.ReasonCode), ("$detail", fault.Detail),
            ("$utc", fault.OccurredUtc.ToString("O"))))
            add.ExecuteNonQuery();
        Queue(db, tx, outbound);
        Event(db, tx, "WORKPIECE_HOLD", item.WorkpieceId, null,
            "reason=" + fault.ReasonCode + " device=" + trigger.PhysicalDeviceId);
        Event(db, tx, "CAPTURE_FAULT_OUTBOX_QUEUED", item.WorkpieceId,
            outbound.MessageId, trigger.CaptureId.ToString("D"));
        tx.Commit();
    }

    /// <summary>Read whether this exact WorkpieceId has a durable HOLD.</summary>
    public bool HasWorkpieceHold(Guid workpieceId)
    {
        if (workpieceId == Guid.Empty) throw new ArgumentException("Invalid workpiece ID.");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM workpiece_holds WHERE workpiece_id=$id";
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Slave consumption queue, populated in the SAME transaction as the peer ACK.</summary>
    public IReadOnlyList<PhysicalCaptureTrigger> PendingCaptureTriggers(int limit = 32)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT trigger_json FROM capture_triggers WHERE processed_utc IS NULL ORDER BY rowid LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<PhysicalCaptureTrigger>();
        while (reader.Read())
        {
            var trigger = WireFormat.Parse<PhysicalCaptureTrigger>(reader.GetString(0));
            trigger.Validate();
            rows.Add(trigger);
        }
        return rows;
    }

    /// <summary>Mark handled only after ALL owned channel results or a persistent HOLD.</summary>
    public void CompleteCaptureTrigger(PhysicalCaptureTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        trigger.Validate();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = Cmd(db, tx,
            "SELECT trigger_json,processed_utc FROM capture_triggers WHERE capture_id=$id",
            ("$id", trigger.CaptureId.ToString("D")));
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != WireFormat.Serialize(trigger))
                throw new InvalidDataException("Unknown or changed durable capture trigger.");
            if (!reader.IsDBNull(1)) return;
        }

        using var proof = Cmd(db, tx,
            "SELECT result_json FROM channel_results WHERE workpiece_id=$id",
            ("$id", trigger.Workpiece.WorkpieceId.ToString("D")));
        var completed = new Dictionary<string, ChannelResult>(StringComparer.Ordinal);
        using (var reader = proof.ExecuteReader())
            while (reader.Read())
            {
                var result = WireFormat.Parse<ChannelResult>(reader.GetString(0));
                if (result.CaptureId == trigger.CaptureId)
                    completed[result.ChannelId] = result;
            }
        bool valid = trigger.Channels.All(ch =>
            completed.TryGetValue(ch.ChannelId, out var result) &&
            result.WorkpieceId == trigger.Workpiece.WorkpieceId &&
            result.Epoch == trigger.Workpiece.Epoch &&
            result.ReleaseId == trigger.Workpiece.ReleaseId);
        using var hold = Cmd(db, tx,
            "SELECT COUNT(*) FROM workpiece_holds WHERE workpiece_id=$id",
            ("$id", trigger.Workpiece.WorkpieceId.ToString("D")));
        bool held = Convert.ToInt64(hold.ExecuteScalar()) > 0;
        if (!valid && !held)
            throw new InvalidOperationException("Cannot acknowledge capture without real results or durable HOLD.");
        using var update = Cmd(db, tx,
            "UPDATE capture_triggers SET processed_utc=$utc WHERE capture_id=$id AND processed_utc IS NULL",
            ("$utc", DateTimeOffset.UtcNow.ToString("O")),
            ("$id", trigger.CaptureId.ToString("D")));
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Duplicate capture trigger completion.");
        Event(db, tx, held && !valid ? "CAPTURE_TRIGGER_HELD" : "CAPTURE_TRIGGER_COMPLETED",
            trigger.Workpiece.WorkpieceId, null, trigger.CaptureId.ToString("D"));
        tx.Commit();
    }

    /// <summary>Post-commit ACK is permitted only for exact persisted wire identity.</summary>
    public bool AcceptIncoming(PeerEnvelope inbound, string expectedReceiver)
    {
        inbound.Validate();
        if (inbound.ReceiverNodeId != expectedReceiver)
            throw new InvalidDataException("Misrouted packet cannot become a persisted result.");

        // Protocol v1 has one source-of-truth direction for each message type:
        // Master sends workpiece registration, Slave returns its own inspection frames.
        WorkpieceIdentity? registration = null;
        ChannelResult? inspection = null;
        PhysicalCaptureTrigger? captureTrigger = null;
        PhysicalCaptureFault? captureFault = null;
        if (inbound.PayloadType == "workpiece_registered")
        {
            if (inbound.SenderNodeId != "master" || expectedReceiver != "slave" ||
                inbound.ChannelId is not null || inbound.StationId is not null)
                throw new InvalidDataException("Forbidden registration sender or channel identity.");
            registration = WireFormat.Parse<WorkpieceIdentity>(inbound.PayloadJson);
            if (registration.WorkpieceId != inbound.WorkpieceId || registration.Epoch != inbound.Epoch ||
                registration.ReleaseId != inbound.SolutionReleaseId || registration.TraceId != inbound.TraceId)
                throw new InvalidDataException("Envelope/payload workpiece identity mismatch.");
        }
        else if (inbound.PayloadType == "channel_result")
        {
            if (inbound.SenderNodeId != "slave" || expectedReceiver != "master" ||
                string.IsNullOrWhiteSpace(inbound.ChannelId))
                throw new InvalidDataException("Forbidden inspection message sender or channel identity.");
            var channel = ChannelCatalog.Require(inbound.ChannelId);
            if (channel.OwnerRole != MachineRole.Slave || channel.StationId != inbound.StationId)
                throw new InvalidDataException("Slave cannot report a Master-owned camera or foreign station.");
            inspection = WireFormat.Parse<ChannelResult>(inbound.PayloadJson);
            if (inspection.WorkpieceId != inbound.WorkpieceId || inspection.Epoch != inbound.Epoch ||
                inspection.ReleaseId != inbound.SolutionReleaseId || inspection.TraceId != inbound.TraceId ||
                inspection.ChannelId != inbound.ChannelId ||
                inspection.StationId != inbound.StationId)
                throw new InvalidDataException("Envelope/payload result lineage mismatch.");
        }

        else if (inbound.PayloadType == "capture_trigger")
        {
            if (inbound.SenderNodeId != "master" || expectedReceiver != "slave" ||
                string.IsNullOrWhiteSpace(inbound.ChannelId))
                throw new InvalidDataException("Capture trigger may only travel Master -> Slave.");
            captureTrigger = WireFormat.Parse<PhysicalCaptureTrigger>(inbound.PayloadJson);
            captureTrigger.Validate();
            if (captureTrigger.OwnerRole != MachineRole.Slave ||
                captureTrigger.Workpiece.WorkpieceId != inbound.WorkpieceId ||
                captureTrigger.Workpiece.TraceId != inbound.TraceId ||
                captureTrigger.Workpiece.Epoch != inbound.Epoch ||
                captureTrigger.Workpiece.ReleaseId != inbound.SolutionReleaseId ||
                captureTrigger.Channels[0].ChannelId != inbound.ChannelId ||
                captureTrigger.StationId != inbound.StationId)
                throw new InvalidDataException("Capture event identity, station or channel is forged.");
        }
        else if (inbound.PayloadType == "capture_fault")
        {
            if (inbound.SenderNodeId != "slave" || expectedReceiver != "master" ||
                string.IsNullOrWhiteSpace(inbound.ChannelId))
                throw new InvalidDataException("Physical capture fault may only travel Slave -> Master.");
            captureFault = WireFormat.Parse<PhysicalCaptureFault>(inbound.PayloadJson);
            captureFault.Validate();
            var trigger = captureFault.Trigger;
            if (trigger.Workpiece.WorkpieceId != inbound.WorkpieceId ||
                trigger.Workpiece.TraceId != inbound.TraceId ||
                trigger.Workpiece.Epoch != inbound.Epoch ||
                trigger.Workpiece.ReleaseId != inbound.SolutionReleaseId ||
                trigger.Channels[0].ChannelId != inbound.ChannelId ||
                trigger.StationId != inbound.StationId)
                throw new InvalidDataException("Physical camera fault carries forged workpiece or channel.");
        }
        else throw new InvalidDataException($"Unrecognized packet type {inbound.PayloadType}");

        // A payload digest alone cannot prove workpiece, station, epoch, sender,
        // receiver, sequence or time. The entire immutable envelope is hashed.
        var envelopeHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(WireFormat.Serialize(inbound))))
            .ToLowerInvariant();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var find = Cmd(db, tx,
            "SELECT envelope_hash FROM inbox WHERE sender_node_id=$sender AND message_id=$id",
            ("$sender", inbound.SenderNodeId), ("$id", inbound.MessageId.ToString("D")));
        var existing = find.ExecuteScalar();
        if (existing is not null)
        {
            if (existing is not string prior ||
                !string.Equals(prior, envelopeHash, StringComparison.Ordinal))
                throw new InvalidDataException("Duplicate with changed metadata or unverified legacy Inbox; no ACK.");
            return true;
        }

        if (registration is not null) InsertWorkpiece(db, tx, registration);
        else if (inspection is not null) InsertResult(db, tx, inspection);
        else if (captureTrigger is not null)
        {
            if (RequireWorkpiece(db, tx, captureTrigger.Workpiece.WorkpieceId) != captureTrigger.Workpiece)
                throw new InvalidDataException("Capture trigger arrived before its exact registration.");
            using var triggerInsert = Cmd(db, tx, """
                INSERT INTO capture_triggers(capture_id,workpiece_id,physical_device_id,
                                             attempt_no,trigger_json)
                VALUES($capture,$item,$device,$attempt,$body)
                """,
                ("$capture", captureTrigger.CaptureId.ToString("D")),
                ("$item", captureTrigger.Workpiece.WorkpieceId.ToString("D")),
                ("$device", captureTrigger.PhysicalDeviceId),
                ("$attempt", captureTrigger.AttemptNo),
                ("$body", WireFormat.Serialize(captureTrigger)));
            triggerInsert.ExecuteNonQuery();
        }

        else if (captureFault is not null)
        {
            var trigger = captureFault.Trigger;
            var item = trigger.Workpiece;
            if (RequireWorkpiece(db, tx, item.WorkpieceId) != item)
                throw new InvalidDataException("Remote physical fault workpiece identity mismatch.");
            using (var final = Cmd(db, tx,
                "SELECT final_json FROM workpieces WHERE workpiece_id=$id",
                ("$id", item.WorkpieceId.ToString("D"))))
                if (final.ExecuteScalar() is string)
                    throw new InvalidOperationException("Cannot accept a late fault after final output.");
            using (var existingHold = Cmd(db, tx,
                "SELECT reason_code,detail FROM workpiece_holds WHERE workpiece_id=$id",
                ("$id", item.WorkpieceId.ToString("D"))))
            using (var reader = existingHold.ExecuteReader())
                if (reader.Read() && (reader.GetString(0) != captureFault.ReasonCode ||
                                      reader.GetString(1) != captureFault.Detail))
                    throw new InvalidDataException("Conflicting Master HOLD: manual reconciliation required.");

            using (var insertHold = Cmd(db, tx, """
                INSERT INTO workpiece_holds(workpiece_id,reason_code,detail,raised_utc)
                VALUES($id,$code,$detail,$utc)
                ON CONFLICT(workpiece_id) DO NOTHING
                """,
                ("$id", item.WorkpieceId.ToString("D")),
                ("$code", captureFault.ReasonCode), ("$detail", captureFault.Detail),
                ("$utc", captureFault.OccurredUtc.ToString("O"))))
                insertHold.ExecuteNonQuery();
            Event(db, tx, "REMOTE_CAPTURE_HOLD", item.WorkpieceId,
                inbound.MessageId, trigger.PhysicalDeviceId + ":" + trigger.CaptureId);
        }

        using var commitInbox = Cmd(db, tx, """
            INSERT INTO inbox(sender_node_id,message_id,payload_hash,envelope_hash,received_utc)
            VALUES ($sender,$id,$payload,$envelope,$utc)
            """,
            ("$sender", inbound.SenderNodeId), ("$id", inbound.MessageId.ToString("D")),
            ("$payload", inbound.PayloadHash), ("$envelope", envelopeHash),
            ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        commitInbox.ExecuteNonQuery();
        Event(db, tx, "INBOX_PERSISTED", inbound.WorkpieceId, inbound.MessageId,
            $"{inbound.PayloadType}:sha256={inbound.PayloadHash}");
        tx.Commit();
        return false;
    }

    public IReadOnlyList<PeerEnvelope> PendingOutbox()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT envelope_json FROM outbox WHERE acknowledged_utc IS NULL ORDER BY created_utc LIMIT 64";
        using var reader = cmd.ExecuteReader();
        var result = new List<PeerEnvelope>();
        while (reader.Read()) result.Add(WireFormat.Parse<PeerEnvelope>(reader.GetString(0)));
        return result;
    }

    public void PersistAcknowledgement(PersistedAck ack)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var find = Cmd(db, tx, "SELECT payload_hash FROM outbox WHERE message_id=$id",
            ("$id", ack.MessageId.ToString("D")));
        var expected = find.ExecuteScalar() as string;
        if (expected is null) throw new InvalidDataException("ACK for an unknown message.");
        if (expected != ack.PayloadHash) throw new InvalidDataException("ACK hash mismatch.");
        using var update = Cmd(db, tx, """
            UPDATE outbox SET acknowledged_utc=$utc WHERE message_id=$id
            """,
            ("$utc", ack.PersistedAtUtc.ToString("O")), ("$id", ack.MessageId.ToString("D")));
        update.ExecuteNonQuery();
        Event(db, tx, "PERSISTED_ACK_RECEIVED", null, ack.MessageId, ack.PayloadHash);
        tx.Commit();
    }

    public WorkpieceIdentity? LatestWorkpiece()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT identity_json FROM workpieces ORDER BY created_utc DESC LIMIT 1";
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : WireFormat.Parse<WorkpieceIdentity>(json);
    }

    /// <summary>Query by immutable primary key, never by the latest barcode.</summary>
    public WorkpieceIdentity? FindWorkpiece(Guid workpieceId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT identity_json FROM workpieces WHERE workpiece_id=$id";
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : WireFormat.Parse<WorkpieceIdentity>(json);
    }

    /// <summary>Restart reconciliation of already-durable Inbox registrations.</summary>
    public IReadOnlyList<WorkpieceIdentity> WorkpiecesMissingChannel(string channelId, int limit = 256)
    {
        _ = ChannelCatalog.Require(channelId);
        if (limit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT w.identity_json FROM workpieces w
            WHERE NOT EXISTS (
                SELECT 1 FROM channel_results r
                WHERE r.workpiece_id=w.workpiece_id AND r.channel_id=$channel)
            ORDER BY w.created_utc, w.workpiece_id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$channel", channelId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var items = new List<WorkpieceIdentity>();
        while (reader.Read()) items.Add(WireFormat.Parse<WorkpieceIdentity>(reader.GetString(0)));
        return items;
    }

    public IReadOnlyList<ChannelResult> ResultsFor(Guid workpieceId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM channel_results WHERE workpiece_id=$id ORDER BY channel_id,attempt_no";
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var results = new List<ChannelResult>();
        while (reader.Read()) results.Add(WireFormat.Parse<ChannelResult>(reader.GetString(0)));
        return results;
    }

    public FinalDecision? LatestFinal(Guid workpieceId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT final_json FROM workpieces WHERE workpiece_id=$id";
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : WireFormat.Parse<FinalDecision>(json);
    }

    public void StoreFinal(WorkpieceIdentity identity, FinalDecision decision)
    {
        if (decision.Disposition == FinalDisposition.Hold)
            throw new InvalidOperationException("HOLD cannot be submitted as a final pass/fail.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        _ = RequireWorkpiece(db, tx, identity.WorkpieceId);
        using var cmd = Cmd(db, tx, """
            UPDATE workpieces SET final_json=$json WHERE workpiece_id=$id AND final_json IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM workpiece_holds WHERE workpiece_id=$id
              )
            """,
            ("$json", WireFormat.Serialize(decision)), ("$id", identity.WorkpieceId.ToString("D")));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Final decision already written.");
        Event(db, tx, "FINAL_DECIDED", identity.WorkpieceId, null, decision.Proof);
        tx.Commit();
    }

    /// <summary>
    /// Permanently mark a SIM_ONLY workpiece as held. The identity is immutable;
    /// duplicate reasons are idempotent, conflicting codes fail closed.
    /// HOLD is never converted to an output command or a final GOOD/NG.
    /// </summary>
    public bool RecordWorkpieceHold(WorkpieceIdentity identity, string reasonCode, string detail)
    {
        identity.Validate();
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) ||
            string.IsNullOrWhiteSpace(detail) || detail.Length > 512)
            throw new ArgumentException("HOLD reason must be a bounded, explicit code and description.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var storedIdentity = RequireWorkpiece(db, tx, identity.WorkpieceId);
        if (storedIdentity != identity)
            throw new InvalidDataException("HOLD lineage mismatch.");
        using (var existing = Cmd(db, tx,
            "SELECT reason_code,detail FROM workpiece_holds WHERE workpiece_id=$id",
            ("$id", identity.WorkpieceId.ToString("D"))))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(0), reasonCode, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(1), detail, StringComparison.Ordinal))
                    throw new InvalidOperationException("Conflicting reason for an already-held WorkpieceId.");
                return false;
            }
        }
        using (var outcome = Cmd(db, tx,
            "SELECT final_json FROM workpieces WHERE workpiece_id=$id",
            ("$id", identity.WorkpieceId.ToString("D"))))
            if (outcome.ExecuteScalar() is string)
                throw new InvalidOperationException("Cannot retroactively HOLD a finalized workpiece.");
        using (var add = Cmd(db, tx, """
            INSERT INTO workpiece_holds(workpiece_id,reason_code,detail,raised_utc)
            VALUES($id,$code,$detail,$utc)
            """,
            ("$id", identity.WorkpieceId.ToString("D")),
            ("$code", reasonCode), ("$detail", detail),
            ("$utc", DateTimeOffset.UtcNow.ToString("O"))))
            add.ExecuteNonQuery();
        Event(db, tx, "WORKPIECE_HOLD", identity.WorkpieceId, null,
            $"reason={reasonCode} detail={detail}");
        tx.Commit();
        return true;
    }

    /// <summary>Read-only durable active holds for Avalonia and diagnostics.</summary>
    public WorkpieceHoldOverview ReadWorkpieceHolds(int limit = 12)
    {
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open();
        long active;
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM workpiece_holds";
            active = Convert.ToInt64(count.ExecuteScalar());
        }
        var items = new List<WorkpieceHoldRecord>();
        using (var query = db.CreateCommand())
        {
            query.CommandText = """
                SELECT workpiece_id,reason_code,detail,raised_utc
                FROM workpiece_holds ORDER BY raised_utc DESC, workpiece_id DESC LIMIT $limit
                """;
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                items.Add(new WorkpieceHoldRecord(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3),
                        System.Globalization.CultureInfo.InvariantCulture)));
        }
        var snapshot = new WorkpieceHoldOverview(active, items);
        snapshot.Validate();
        return snapshot;
    }

    /// <summary>Read-only bounded live overview for UI; never joins products by barcode.</summary>
    public ProductionOverview ReadProductionOverview(int historyLimit = 12)
    {
        if (historyLimit is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(historyLimit));
        using var db = Open();
        long total, good, reject, unfinished;
        using (var counters = db.CreateCommand())
        {
            counters.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN json_extract(final_json,'$.disposition')='good' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN json_extract(final_json,'$.disposition')='reject' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN final_json IS NULL THEN 1 ELSE 0 END),0)
                FROM workpieces
                """;
            using var reader = counters.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("No production counter row.");
            total = reader.GetInt64(0);
            good = reader.GetInt64(1);
            reject = reader.GetInt64(2);
            unfinished = reader.GetInt64(3);
        }
        var recent = new List<WorkpieceProductionRow>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT w.identity_json, w.final_json,
                       (SELECT COUNT(*) FROM channel_results r
                        WHERE r.workpiece_id=w.workpiece_id) AS channel_count
                FROM workpieces w
                ORDER BY w.created_utc DESC,w.workpiece_id DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", historyLimit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var wp = WireFormat.Parse<WorkpieceIdentity>(reader.GetString(0));
                var final = reader.IsDBNull(1)
                    ? null : WireFormat.Parse<FinalDecision>(reader.GetString(1));
                recent.Add(new WorkpieceProductionRow(
                    wp.WorkpieceId, wp.Barcode, wp.Epoch, wp.ReleaseId,
                    checked((int)reader.GetInt64(2)), final?.Disposition, wp.CreatedAtUtc));
            }
        }
        var result = new ProductionOverview(total, good, reject, unfinished, recent);
        result.Validate();
        return result;
    }

    public MachineSnapshot Snapshot(string nodeId, MachineRole role)
    {
        var latest = LatestWorkpiece();
        var rows = latest is null ? [] : ResultsFor(latest.WorkpieceId);
        var byChannel = rows.GroupBy(x => x.ChannelId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.MaxBy(y => y.AttemptNo)!, StringComparer.Ordinal);
        var channels = ChannelCatalog.All.Select(x =>
        {
            if (byChannel.TryGetValue(x.ChannelId, out var result))
                return new ChannelSnapshot(x.ChannelId, x.StationId, x.PhysicalDeviceId,
                    "SIM_" + result.Outcome.ToString().ToUpperInvariant(),
                    result.WorkpieceId, result.IsSimulated, result.FinishedUtc);
            return new ChannelSnapshot(x.ChannelId, x.StationId, x.PhysicalDeviceId,
                x.OwnerRole == role ? "NOT_CONNECTED" : "REMOTE_PENDING", null, false, null);
        }).ToArray();
        var final = latest is null ? null : LatestFinal(latest.WorkpieceId);
        return new(nodeId, role, latest is null ? "SIM_READY" : final is null ? "SIM_PROCESSING" : "SIM_FINAL",
            latest, final, channels, DateTimeOffset.UtcNow, false, "SIM_LIGHT")
        {
            Production = ReadProductionOverview(),
            Holds = ReadWorkpieceHolds()
        };
    }
}
