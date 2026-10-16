using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;

namespace AOI.Runtime;

/// <summary>
/// Durable token -> immutable WorkpieceId provenance for the offline conveyor.
/// Deliberately refuses startup after a previous run: vPLC RAM, real position and
/// Core WAL must be manually reconciled, never silently treated as a new shift.
/// </summary>
public sealed class SimFullCycleLedger
{
    public string DatabasePath { get; }
    private readonly string _connectionString;

    public SimFullCycleLedger(string path, uint virtualPlcBootNonce)
    {
        if (virtualPlcBootNonce == 0) throw new ArgumentOutOfRangeException(nameof(virtualPlcBootNonce));
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var schema = db.CreateCommand();
        schema.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS sim_full_run(
               id INTEGER PRIMARY KEY CHECK(id=1), boot_nonce INTEGER NOT NULL,
               opened_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sim_full_tokens(
               token INTEGER PRIMARY KEY, identity_json TEXT NOT NULL,
               phase TEXT NOT NULL CHECK(phase IN ('registered','exited')),
               disposition TEXT, updated_utc TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        using var check = db.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sim_full_run";
        if (Convert.ToInt64(check.ExecuteScalar()) != 0)
            throw new InvalidOperationException(
                "SIM_FULL_STARTUP_HOLD: previous run found. Review old Core WAL and physical queue; " +
                "never reuse an old data root without deliberate operator reset.");
        using var init = db.CreateCommand();
        init.CommandText = "INSERT INTO sim_full_run(id,boot_nonce,opened_utc) VALUES(1,$nonce,$now)";
        init.Parameters.AddWithValue("$nonce", (long)virtualPlcBootNonce);
        init.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
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

    public void Register(ushort token, WorkpieceIdentity identity)
    {
        if (token == 0) throw new ArgumentOutOfRangeException(nameof(token));
        identity.Validate();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var existing = db.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText = "SELECT identity_json FROM sim_full_tokens WHERE token=$token";
        existing.Parameters.AddWithValue("$token", (int)token);
        var old = existing.ExecuteScalar() as string;
        string json = WireFormat.Serialize(identity);
        if (old is not null)
            throw new InvalidOperationException("SIM_FULL_DUPLICATE_TOKEN: never silently remap a conveyor slot.");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sim_full_tokens(token,identity_json,phase,updated_utc)
            VALUES($token,$identity,'registered',$utc)
            """;
        cmd.Parameters.AddWithValue("$token", (int)token);
        cmd.Parameters.AddWithValue("$identity", json);
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public WorkpieceIdentity? Find(ushort token)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT identity_json FROM sim_full_tokens WHERE token=$token";
        cmd.Parameters.AddWithValue("$token", (int)token);
        return cmd.ExecuteScalar() is string json ? WireFormat.Parse<WorkpieceIdentity>(json) : null;
    }

    public int RegisteredCount
    {
        get
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sim_full_tokens";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void MarkExited(ushort token, FinalDecision final)
    {
        if (token == 0 || final.Disposition is FinalDisposition.Hold || !final.IsSimulation)
            throw new InvalidOperationException("Unproven SIM_FULL exit is forbidden.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE sim_full_tokens SET phase='exited',disposition=$decision,updated_utc=$utc
            WHERE token=$token AND phase='registered'
            """;
        cmd.Parameters.AddWithValue("$token", (int)token);
        cmd.Parameters.AddWithValue("$decision", final.Disposition.ToString());
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Missing/already-exited conveyor token. HOLD.");
        tx.Commit();
    }
}
