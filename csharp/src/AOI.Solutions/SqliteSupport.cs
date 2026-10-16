using Microsoft.Data.Sqlite;

namespace AOI.Solutions;

internal sealed class SqliteSupport
{
    private readonly string _connectionString;
    public string FilePath { get; }

    public SqliteSupport(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return db;
    }

    public void Initialize(string schema)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" + schema;
        cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Key, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in parameters)
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }

    public static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Key, object? Value)[] parameters)
    {
        using var cmd = Command(db, tx, sql, parameters);
        return cmd.ExecuteScalar();
    }

    public static int Run(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Key, object? Value)[] parameters)
    {
        using var cmd = Command(db, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }
}
