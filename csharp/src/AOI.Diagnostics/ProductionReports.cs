using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace AOI.Diagnostics;

/// <summary>
/// Operator-facing, read-only production queries over the existing two Core WALs.
/// Master owns workpiece/final counts; Slave never contributes another physical unit.
/// No alternative Core, schema migrations, hardware access or simulator write operations.
/// </summary>
public sealed class ProductionReports
{
    private readonly string _root;
    public ProductionReports(string simDataRoot) =>
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(simDataRoot)
            ? throw new ArgumentException("A SIM_ONLY data root is required.", nameof(simDataRoot))
            : simDataRoot);

    private static readonly string WhereClause = """
        WHERE ($workpiece IS NULL OR w.workpiece_id=$workpiece)
          AND ($barcode IS NULL OR w.barcode=$barcode)
          AND ($from IS NULL OR w.created_utc >= $from)
          AND ($until IS NULL OR w.created_utc < $until)
          AND ($station IS NULL OR EXISTS (
              SELECT 1 FROM channel_results r WHERE r.workpiece_id=w.workpiece_id
                AND json_extract(r.result_json,'$.stationId')=$station))
        """;

    private static readonly string JoinedRows = """
        FROM workpieces w LEFT JOIN workpiece_holds h ON h.workpiece_id=w.workpiece_id
        """;

    private static string? DateSql(DateTimeOffset? instant) =>
        instant?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private string NodeDatabase(string node) =>
        Path.Combine(_root, node, "production.db");

    private SqliteConnection Open(string node)
    {
        if (node is not ("master" or "slave"))
            throw new ArgumentException("Only the two SIM_ONLY Core databases can be read.", nameof(node));
        string path = NodeDatabase(node);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Missing {node} production WAL; report is incomplete.", path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var timeout = db.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=3000";
        timeout.ExecuteNonQuery();
        return db;
    }

    private static void Apply(SqliteCommand cmd, ProductionSearch filter)
    {
        cmd.Parameters.AddWithValue("$workpiece", (object?)filter.WorkpieceId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$barcode", (object?)filter.Barcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$station", (object?)filter.StationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)DateSql(filter.FromUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$until", (object?)DateSql(filter.UntilUtc) ?? DBNull.Value);
    }

    private static ProductionDisposition Classify(string? finalJson, string? holdReason)
    {
        if (holdReason is not null)
        {
            if (finalJson is not null)
                throw new InvalidDataException("Workpiece contains both a final and an active HOLD.");
            return ProductionDisposition.Hold;
        }
        if (finalJson is null) return ProductionDisposition.InProgress;
        var final = WireFormat.Parse<FinalDecision>(finalJson);
        return final.Disposition switch {
            FinalDisposition.Good => ProductionDisposition.Good,
            FinalDisposition.Reject => ProductionDisposition.Ng,
            _ => throw new InvalidDataException("Unexpected persisted final disposition.")
        };
    }

    /// <summary>Stable page, exact barcode match, optional station and UTC half-open date range.</summary>
    public ProductionHistoryPage Search(ProductionSearch filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        using var db = Open("master");
        long count;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) " + JoinedRows + WhereClause;
            Apply(cmd, filter);
            count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var rows = new List<ProductionHistoryRow>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT w.workpiece_id,w.identity_json,w.final_json,h.reason_code,
                       (SELECT COUNT(DISTINCT r.channel_id) FROM channel_results r
                        WHERE r.workpiece_id=w.workpiece_id)
                """ + JoinedRows + WhereClause
                + " ORDER BY w.created_utc DESC,w.workpiece_id DESC LIMIT $limit OFFSET $offset";
            Apply(cmd, filter);
            cmd.Parameters.AddWithValue("$limit", filter.Limit);
            cmd.Parameters.AddWithValue("$offset", filter.Offset);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var identity = WireFormat.Parse<WorkpieceIdentity>(reader.GetString(1));
                if (identity.WorkpieceId != id) throw new InvalidDataException("Corrupt workpiece identity.");
                rows.Add(new ProductionHistoryRow(id, identity.Barcode, identity.TraceId,
                    identity.Epoch, identity.ReleaseId, identity.CreatedAtUtc,
                    Classify(reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        return new ProductionHistoryPage(count, rows.ToArray(), filter.Offset, filter.Limit);
    }

    /// <summary>All matched registrations form the denominator; paging never affects totals.</summary>
    public ProductionPeriodSummary Summarize(ProductionSearch filter, string label = "custom")
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        if (filter.FromUtc is null || filter.UntilUtc is null)
            throw new ArgumentException("An explicit UTC range is required for a production period.");
        long good = 0, ng = 0, held = 0, unfinished = 0;
        using var db = Open("master");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT w.final_json,h.reason_code " + JoinedRows + WhereClause;
        Apply(cmd, filter);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var disposition = Classify(reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
            switch (disposition)
            {
                case ProductionDisposition.Good: good++; break;
                case ProductionDisposition.Ng: ng++; break;
                case ProductionDisposition.Hold: held++; break;
                case ProductionDisposition.InProgress: unfinished++; break;
            }
        }
        long total = good + ng + held + unfinished;
        return new ProductionPeriodSummary(label, filter.FromUtc.Value.ToUniversalTime(),
            filter.UntilUtc.Value.ToUniversalTime(), total, good, ng, held, unfinished,
            good + ng == 0 ? null : (double)good / (good + ng),
            total == 0 ? null : (double)good / total);
    }

    public ProductionPeriodSummary Daily(DateOnly date, TimeZoneInfo zone) =>
        Period(date, date.AddDays(1), zone, "day:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>ISO-like Monday..next Monday, within caller's explicit local timezone.</summary>
    public ProductionPeriodSummary Weekly(DateOnly anyDateInWeek, TimeZoneInfo zone)
    {
        int delta = ((int)anyDateInWeek.DayOfWeek + 6) % 7;
        var monday = anyDateInWeek.AddDays(-delta);
        return Period(monday, monday.AddDays(7), zone,
            "week:" + monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private ProductionPeriodSummary Period(DateOnly from, DateOnly until, TimeZoneInfo zone, string label)
    {
        ArgumentNullException.ThrowIfNull(zone);
        static DateTimeOffset Edge(DateOnly date, TimeZoneInfo timezone)
        {
            var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timezone), TimeSpan.Zero);
        }
        return Summarize(new ProductionSearch(FromUtc: Edge(from, zone), UntilUtc: Edge(until, zone)), label);
    }

    /// <summary>Search both Core event tables; filters use indexed identity and bounded text match.</summary>
    public ProductionEventRow[] SearchEvents(ProductionEventSearch filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Validate();
        var rows = new List<ProductionEventRow>();
        foreach (string node in filter.NodeId is null ? new[] { "master", "slave" } : new[] { filter.NodeId })
        {
            using var db = Open(node);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT e.event_id,e.utc,e.kind,e.workpiece_id,e.message_id,e.details
                FROM flight_events e
                WHERE ($workpiece IS NULL OR e.workpiece_id=$workpiece)
                  AND ($kind IS NULL OR e.kind=$kind)
                  AND ($text IS NULL OR instr(e.details,$text)>0 OR instr(e.kind,$text)>0)
                  AND ($from IS NULL OR e.utc >= $from)
                  AND ($until IS NULL OR e.utc < $until)
                  AND ($station IS NULL OR EXISTS (
                      SELECT 1 FROM channel_results r WHERE r.workpiece_id=e.workpiece_id
                      AND json_extract(r.result_json,'$.stationId')=$station))
                ORDER BY e.utc DESC,e.event_id DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$workpiece", (object?)filter.WorkpieceId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", (object?)filter.Kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", (object?)filter.ContainsText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$station", (object?)filter.StationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$from", (object?)DateSql(filter.FromUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$until", (object?)DateSql(filter.UntilUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", filter.Limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new ProductionEventRow(node, Guid.Parse(reader.GetString(0)),
                    ParseUtc(reader.GetString(1)), reader.GetString(2),
                    reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), reader.GetString(5)));
        }
        return rows.OrderByDescending(e => e.Utc).ThenBy(e => e.NodeId, StringComparer.Ordinal)
            .ThenBy(e => e.EventId).Take(filter.Limit).ToArray();
    }

    /// <summary>One immutable WorkpieceId, never "most recent matching barcode".</summary>
    public ProductionWorkpieceTrace Trace(Guid workpieceId, bool verifyImages = true)
    {
        if (workpieceId == Guid.Empty) throw new ArgumentException("A real WorkpieceId is required.", nameof(workpieceId));
        var row = Search(new ProductionSearch(WorkpieceId: workpieceId, Limit: 1)).Rows.SingleOrDefault()
            ?? throw new KeyNotFoundException("WorkpieceId not found in the Master production WAL.");
        var observed = new Dictionary<(string ChannelId, int AttemptNo), ChannelResult>();
        foreach (var node in new[] { "master", "slave" })
        {
            using var db = Open(node);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT result_json FROM channel_results
                WHERE workpiece_id=$id ORDER BY channel_id,attempt_no
                """;
            cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var result = WireFormat.Parse<ChannelResult>(reader.GetString(0));
                var key = (result.ChannelId, result.AttemptNo);
                if (observed.TryGetValue(key, out var prior))
                {
                    if (prior != result)
                        throw new InvalidDataException($"Conflicting Master/Slave result for {key.ChannelId}/{key.AttemptNo}.");
                    continue;
                }
                if (result.WorkpieceId != row.WorkpieceId || result.TraceId != row.TraceId ||
                    result.Epoch != row.Epoch || result.ReleaseId != row.ReleaseId)
                    throw new InvalidDataException("Trace lineage mismatch; refusing cross-product image association.");
                observed[key] = result;
            }
        }
        var channels = observed.Values.OrderBy(r => r.ChannelId, StringComparer.Ordinal)
            .ThenBy(r => r.AttemptNo)
            .Select(r =>
            {
                var channel = ChannelCatalog.Require(r.ChannelId);
                string node = channel.OwnerRole == MachineRole.Master ? "master" : "slave";
                return new ProductionChannelTrace(node, r,
                    TraceAssetLookup.Lookup(_root, node, r, verifyImages));
            }).ToArray();
        return new ProductionWorkpieceTrace(row, channels,
            SearchEvents(new ProductionEventSearch(WorkpieceId: workpieceId, Limit: 500)));
    }
}
