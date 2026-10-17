using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AOI.Diagnostics;

/// <summary>
/// Merges actual per-Core timing WAL milestones with the existing Core production
/// flight_events for historical runs. Old Core events are explicitly marked
/// CORE_WAL_LEGACY: they do NOT prove a FINS write, FrameReady or CamDone.
/// Read-only queries NEVER initialize or mutate databases.
/// </summary>
public sealed class ProductionTimelineReader
{
    private readonly string _root;

    public ProductionTimelineReader(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = Path.GetFullPath(dataRoot);
    }

    public TimingTimeline Query(TimingFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        var collected = new List<TimingEvent>();
        bool anyDb = false;
        foreach (string node in new[] { "master", "slave" })
        {
            string folder = Path.Combine(_root, node);
            string timingPath = Path.Combine(folder, ProductionTimingJournal.FileName);
            if (File.Exists(timingPath))
            {
                anyDb = true;
                ReadTimingDb(timingPath, node, filter, collected);
            }
            string corePath = Path.Combine(folder, "production.db");
            if (filter.IncludeExistingCoreEvents && filter.CaptureId is null &&
                filter.PhysicalDeviceId is null && filter.ChannelId is null &&
                filter.AttemptNo is null && File.Exists(corePath))
            {
                anyDb = true;
                ReadExistingCoreEvents(corePath, node, filter.WorkpieceId, collected, filter.Limit + 1);
            }
        }
        if (!anyDb)
            throw new FileNotFoundException("No existing timing/production WALs at the supplied run root.");

        var ordered = collected.OrderBy(e => e.Utc)
            .ThenBy(e => e.NodeId, StringComparer.Ordinal)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.EventId)
            .ToArray();
        bool truncated = ordered.Length > filter.Limit;
        var events = ordered.Take(filter.Limit).ToArray();
        var problems = ProductionTimingAnalyzer.Diagnose(events, truncated);
        return new TimingTimeline(filter.WorkpieceId, DateTimeOffset.UtcNow, events, problems)
        {
            Truncated = truncated
        };
    }

    private static SqliteConnection OpenExisting(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var timeout = db.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=3000";
        timeout.ExecuteNonQuery();
        return db;
    }

    private static void ReadTimingDb(string path, string node, TimingFilter filter, List<TimingEvent> rows)
    {
        using var db = OpenExisting(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT sequence,event_json FROM timing_events
            WHERE workpiece_id=$item
              AND ($capture IS NULL OR capture_id=$capture)
              AND ($device IS NULL OR physical_device_id=$device)
              AND ($attempt IS NULL OR attempt_no=$attempt)
              AND ($channel IS NULL OR json_extract(event_json,'$.channelId')=$channel)
            ORDER BY sequence LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$item", filter.WorkpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$capture", (object?)filter.CaptureId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)filter.PhysicalDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$channel", (object?)filter.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempt", (object?)filter.AttemptNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", filter.Limit + 1);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long seq = reader.GetInt64(0);
            var value = DiagnosticsFormat.Parse<TimingEvent>(reader.GetString(1));
            if (seq < 1 || value.Sequence != 0 || value.NodeId != node ||
                value.WorkpieceId != filter.WorkpieceId ||
                value.ProcessRunId is null || value.MonotonicTick is null ||
                value.TickFrequency is null or <= 0 ||
                value.Origin != "DIRECT_OBSERVATION")
                throw new InvalidDataException("Incorrect or corrupted timebase/identity in direct timing WAL.");
            rows.Add(value with { Sequence = seq });
        }
    }

    private static void ReadExistingCoreEvents(string path, string node, Guid workpieceId,
        List<TimingEvent> rows, int limit)
    {
        using var db = OpenExisting(path);
        using var identityQuery = db.CreateCommand();
        identityQuery.CommandText = "SELECT identity_json FROM workpieces WHERE workpiece_id=$id";
        identityQuery.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        if (identityQuery.ExecuteScalar() is not string raw) return;
        var identity = WireFormat.Parse<WorkpieceIdentity>(raw);
        if (identity.WorkpieceId != workpieceId)
            throw new InvalidDataException("Existing Core workpiece identity mismatch.");

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id,e.utc,e.kind,e.message_id
              FROM flight_events e
             WHERE e.workpiece_id=$id
                OR (e.workpiece_id IS NULL AND e.message_id IN (
                    SELECT o.message_id FROM outbox o
                     WHERE json_extract(o.envelope_json,'$.workpieceId')=$id))
             ORDER BY e.utc,e.event_id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string kind = reader.GetString(2);
            rows.Add(new TimingEvent(
                Sequence: 0,
                EventId: Guid.Parse(reader.GetString(0)),
                NodeId: node,
                ProcessRunId: null,
                Origin: "CORE_WAL_LEGACY",
                Utc: DateTimeOffset.Parse(reader.GetString(1),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                MonotonicTick: null,
                TickFrequency: null,
                WorkpieceId: identity.WorkpieceId, TraceId: identity.TraceId,
                Epoch: identity.Epoch, ReleaseId: identity.ReleaseId,
                Stage: TimingStage.CoreEventPersisted,
                CaptureId: null, PhysicalDeviceId: null, ChannelId: null,
                AttemptNo: null, FrameId: null, OperationId: null,
                MessageId: reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                PlcDmAddress: null, PlcValue: null, FinsSid: null,
                Status: kind, Details: null));
        }
    }
}
