using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace AOI.Diagnostics;

/// <summary>
/// Per-Core durable local timing evidence sink. One SQLite WAL per master/slave process,
/// independent from the authoritative ProductionStore transaction. Never sends PLC
/// commands, manufactures camera frames or modifies A0 production/vision databases.
/// The caller records milestones ONLY after their corresponding real action has occurred.
/// </summary>
public sealed class ProductionTimingJournal
{
    public const string FileName = "timing-events.sqlite";
    private readonly string _nodeId;
    private readonly string _connectionString;
    private readonly Guid _processRunId = Guid.NewGuid();

    public string DatabasePath { get; }
    public Guid ProcessRunId => _processRunId;

    public ProductionTimingJournal(string nodeFolder, string nodeId)
    {
        if (nodeId is not ("master" or "slave"))
            throw new ArgumentException("Expected Core node 'master' or 'slave'.", nameof(nodeId));
        _nodeId = nodeId;
        string folder = Path.GetFullPath(nodeFolder);
        Directory.CreateDirectory(folder);
        DatabasePath = Path.Combine(folder, FileName);
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();

        using var db = Open();
        using var init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS timing_events(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                node_id TEXT NOT NULL,
                workpiece_id TEXT NOT NULL,
                capture_id TEXT,
                physical_device_id TEXT,
                epoch INTEGER NOT NULL,
                attempt_no INTEGER,
                stage TEXT NOT NULL,
                utc TEXT NOT NULL,
                event_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_timing_workpiece
            ON timing_events(workpiece_id,sequence);
            CREATE INDEX IF NOT EXISTS ix_timing_capture
            ON timing_events(workpiece_id,capture_id,attempt_no);
            """;
        init.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000";
        pragma.ExecuteNonQuery();
        return db;
    }

    /// <summary>
    /// Capture local Stopwatch ticks and UTC at API entry. Stable within one process run.
    /// Exceptions are propagated: A0 decides policy for unavailable evidence storage,
    /// instead of logging a false "success".
    /// </summary>
    public TimingEvent Append(TimingObservation observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        observed.Validate();
        long tick = Stopwatch.GetTimestamp();
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        var item = observed.Item;
        var entry = new TimingEvent(
            Sequence: 0, EventId: Guid.NewGuid(), NodeId: _nodeId,
            ProcessRunId: _processRunId, Origin: "DIRECT_OBSERVATION",
            Utc: utc, MonotonicTick: tick, TickFrequency: Stopwatch.Frequency,
            WorkpieceId: item.WorkpieceId, TraceId: item.TraceId, Epoch: item.Epoch,
            ReleaseId: item.ReleaseId, Stage: observed.Stage,
            CaptureId: observed.CaptureId, PhysicalDeviceId: observed.PhysicalDeviceId,
            ChannelId: observed.ChannelId, AttemptNo: observed.AttemptNo,
            FrameId: observed.FrameId, OperationId: observed.OperationId,
            MessageId: observed.MessageId, PlcDmAddress: observed.PlcDmAddress,
            PlcValue: observed.PlcValue, FinsSid: observed.FinsSid,
            Status: observed.Status, Details: observed.Details);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO timing_events(
                event_id,node_id,workpiece_id,capture_id,physical_device_id,
                epoch,attempt_no,stage,utc,event_json)
            VALUES($event,$node,$item,$capture,$device,$epoch,$attempt,$stage,$utc,$json)
            RETURNING sequence
            """;
        cmd.Parameters.AddWithValue("$event", entry.EventId.ToString("D"));
        cmd.Parameters.AddWithValue("$node", _nodeId);
        cmd.Parameters.AddWithValue("$item", item.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$capture", (object?)entry.CaptureId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)entry.PhysicalDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$epoch", item.Epoch);
        cmd.Parameters.AddWithValue("$attempt", (object?)entry.AttemptNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stage", entry.Stage.ToString());
        cmd.Parameters.AddWithValue("$utc", utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$json", DiagnosticsFormat.Serialize(entry));
        long sequence = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return entry with { Sequence = sequence };
    }
}
