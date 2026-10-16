using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;

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
              received_utc TEXT NOT NULL, PRIMARY KEY(sender_node_id,message_id));
            CREATE TABLE IF NOT EXISTS outbox (
              message_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, envelope_json TEXT NOT NULL,
              created_utc TEXT NOT NULL, acknowledged_utc TEXT);
            CREATE TABLE IF NOT EXISTS flight_events (
              event_id TEXT PRIMARY KEY, utc TEXT NOT NULL, kind TEXT NOT NULL,
              workpiece_id TEXT, message_id TEXT, details TEXT NOT NULL);
            """;
        init.ExecuteNonQuery();
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

    /// <summary>Returns true if duplicate with identical hash, false when newly durable. Throws on collision.</summary>
    public bool AcceptIncoming(PeerEnvelope inbound, string expectedReceiver)
    {
        inbound.Validate();
        if (inbound.ReceiverNodeId != expectedReceiver)
            throw new InvalidDataException("Misrouted packet cannot become a persisted result.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var find = Cmd(db, tx, "SELECT payload_hash FROM inbox WHERE sender_node_id=$sender AND message_id=$id",
            ("$sender", inbound.SenderNodeId), ("$id", inbound.MessageId.ToString("D")));
        var existing = find.ExecuteScalar() as string;
        if (existing is not null)
        {
            if (!string.Equals(existing, inbound.PayloadHash, StringComparison.Ordinal))
                throw new InvalidDataException("Duplicate MessageId with different payload SHA256.");
            return true;
        }
        if (inbound.PayloadType == "workpiece_registered")
        {
            var identity = WireFormat.Parse<WorkpieceIdentity>(inbound.PayloadJson);
            if (identity.WorkpieceId != inbound.WorkpieceId || identity.Epoch != inbound.Epoch ||
                identity.ReleaseId != inbound.SolutionReleaseId || identity.TraceId != inbound.TraceId)
                throw new InvalidDataException("Envelope/payload workpiece identity mismatch.");
            InsertWorkpiece(db, tx, identity);
        }
        else if (inbound.PayloadType == "channel_result")
        {
            var result = WireFormat.Parse<ChannelResult>(inbound.PayloadJson);
            if (result.WorkpieceId != inbound.WorkpieceId || result.Epoch != inbound.Epoch ||
                result.ReleaseId != inbound.SolutionReleaseId || result.TraceId != inbound.TraceId ||
                result.ChannelId != inbound.ChannelId)
                throw new InvalidDataException("Envelope/payload result lineage mismatch.");
            InsertResult(db, tx, result);
        }
        else throw new InvalidDataException($"Unrecognized packet type {inbound.PayloadType}");

        using var commitInbox = Cmd(db, tx, """
            INSERT INTO inbox(sender_node_id,message_id,payload_hash,received_utc)
            VALUES ($sender,$id,$hash,$utc)
            """,
            ("$sender", inbound.SenderNodeId), ("$id", inbound.MessageId.ToString("D")),
            ("$hash", inbound.PayloadHash), ("$utc", DateTimeOffset.UtcNow.ToString("O")));
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
            """,
            ("$json", WireFormat.Serialize(decision)), ("$id", identity.WorkpieceId.ToString("D")));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Final decision already written.");
        Event(db, tx, "FINAL_DECIDED", identity.WorkpieceId, null, decision.Proof);
        tx.Commit();
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
            latest, final, channels, DateTimeOffset.UtcNow, false, "SIM_LIGHT");
    }
}
