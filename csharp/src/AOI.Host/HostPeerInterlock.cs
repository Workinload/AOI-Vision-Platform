using Microsoft.Data.Sqlite;

namespace AOI.Host;

/// <summary>
/// A0 Host-level, persistent stop-the-line gate. It is deliberately separate
/// from Agent01's per-output effect journal: a failed PeerLink leaves the
/// physical PLC and camera side effects uncertain, so NO automatic production
/// cycle may be created on a newly opened TCP session.
///
/// The marker is committed to SQLite WAL with FULL synchronous durability.
/// There is intentionally no automatic reset/replay API. A future authorized
/// operator reconciliation must inspect A1 effects and PLC feedback first.
/// </summary>
internal sealed class HostPeerInterlock
{
    public string DatabasePath { get; }

    public HostPeerInterlock(string nodeFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeFolder);
        Directory.CreateDirectory(nodeFolder);
        DatabasePath = Path.Combine(nodeFolder, "host-peer-interlock.sqlite");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_peer_interlock (
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                latched_utc TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                detail TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        db.Open();
        using var config = db.CreateCommand();
        config.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        config.ExecuteNonQuery();
        return db;
    }

    public bool IsLatched()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM host_peer_interlock WHERE singleton=1";
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    public void Latch(string reason, string detail)
    {
        if (reason is not ("PEER_LINK_LOST" or "PEER_SEND_FAILED" or
                          "PEER_INBOUND_FAILED"))
            throw new ArgumentException("Unknown interlock reason.", nameof(reason));
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("A durable interlock requires a cause.", nameof(detail));
        // Do not let untrusted exception text inflate the operator journal.
        string bounded = detail.Length > 320 ? detail[..320] : detail;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO host_peer_interlock
                (singleton,latched_utc,reason_code,detail)
            VALUES(1,$utc,$reason,$detail);
            """;
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$detail", bounded);
        cmd.ExecuteNonQuery();
        tx.Commit();
        if (!IsLatched())
            throw new InvalidOperationException("Failed to durably persist peer interlock.");
    }
}
