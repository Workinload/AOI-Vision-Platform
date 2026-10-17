using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AOI.Runtime;

/// <summary>
/// Operator SIM_ONLY ROI drafts in separate SQLite WAL. No camera commands,
/// calibration changes, release mutation, PLC writes or automatic activation.
/// </summary>
public sealed class RoiConfigStore
{
    private readonly string _connection;
    private readonly MachineRole _role;
    public string DatabasePath { get; }

    public RoiConfigStore(string path, MachineRole role)
    {
        if (role is not (MachineRole.Master or MachineRole.Slave))
            throw new ArgumentOutOfRangeException(nameof(role));
        _role = role;
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();
        using var db = Open();
        using var init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS sim_roi_drafts(
              channel_id TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL,
              width REAL NOT NULL, height REAL NOT NULL, revision INTEGER NOT NULL,
              actor TEXT NOT NULL, edited_utc TEXT NOT NULL);
            """;
        init.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000";
        pragma.ExecuteNonQuery();
        return db;
    }

    public IReadOnlyList<RoiDraftState> Snapshot()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT channel_id,x,y,width,height,revision,actor,edited_utc
            FROM sim_roi_drafts ORDER BY channel_id
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<RoiDraftState>();
        while (reader.Read())
        {
            var draft = new RoiDraftState(reader.GetString(0), reader.GetDouble(1),
                reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.GetInt64(5), reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
            draft.Validate();
            if (ChannelCatalog.Require(draft.ChannelId).OwnerRole != _role)
                throw new InvalidDataException("Foreign machine ROI draft must never be served.");
            rows.Add(draft);
        }
        return rows;
    }

    public RoiDraftState Save(RoiDraftEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        edit.Validate();
        if (ChannelCatalog.Require(edit.ChannelId).OwnerRole != _role)
            throw new InvalidOperationException("ROI_OWNER_DENIED: channel belongs to the other Core.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        long previous;
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT revision FROM sim_roi_drafts WHERE channel_id=$channel";
            read.Parameters.AddWithValue("$channel", edit.ChannelId);
            var old = read.ExecuteScalar();
            previous = old is null ? 0 : Convert.ToInt64(old);
        }
        if (previous != edit.ExpectedRevision)
            throw new InvalidOperationException("ROI_DRAFT_REVISION_CONFLICT");
        long next = checked(previous + 1);
        var now = DateTimeOffset.UtcNow;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO sim_roi_drafts(channel_id,x,y,width,height,revision,actor,edited_utc)
            VALUES($channel,$x,$y,$width,$height,$revision,$actor,$utc)
            ON CONFLICT(channel_id) DO UPDATE SET x=excluded.x,y=excluded.y,
                width=excluded.width,height=excluded.height,revision=excluded.revision,
                actor=excluded.actor,edited_utc=excluded.edited_utc
            """;
        update.Parameters.AddWithValue("$channel", edit.ChannelId);
        update.Parameters.AddWithValue("$x", edit.X);
        update.Parameters.AddWithValue("$y", edit.Y);
        update.Parameters.AddWithValue("$width", edit.Width);
        update.Parameters.AddWithValue("$height", edit.Height);
        update.Parameters.AddWithValue("$revision", next);
        update.Parameters.AddWithValue("$actor", edit.Actor);
        update.Parameters.AddWithValue("$utc", now.ToString("O"));
        update.ExecuteNonQuery();
        tx.Commit();
        return new RoiDraftState(edit.ChannelId, edit.X, edit.Y, edit.Width,
            edit.Height, next, edit.Actor, now);
    }
}

/// <summary>
/// Extra loopback-only editing endpoint; explicitly opt-in token; no hardware
/// or published product writes. It never shares ports with production PeerLink.
/// </summary>
public static class RoiDraftIpc
{
    public static async Task ServeAsync(RoiConfigStore store, int port, string? token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
        {
            Console.WriteLine("SIM_ROI_EDIT_IPC_DISABLED: missing local operator session token");
            return;
        }
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"SIM_ROI_EDIT_IPC_LISTEN 127.0.0.1:{port} SIM_ONLY");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _ = Task.Run(() => HandleAsync(store, socket, token, ct), ct);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task HandleAsync(RoiConfigStore store, TcpClient socket,
        string token, CancellationToken ct)
    {
        using (socket)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            RoiDraftSaveReply reply;
            try
            {
                var stream = socket.GetStream();
                byte[] data = new byte[2048];
                int count = 0;
                while (count < data.Length)
                {
                    int n = await stream.ReadAsync(data.AsMemory(count, 1), timeout.Token);
                    if (n == 0) return;
                    if (data[count] == (byte)'\n') break;
                    count++;
                }
                if (count >= data.Length)
                    reply = new RoiDraftSaveReply(false, "ROI_REQUEST_TOO_LONG", null);
                else
                {
                    var request = WireFormat.Parse<RoiDraftSaveRequest>(Encoding.UTF8.GetString(data, 0, count));
                    var supplied = Encoding.UTF8.GetBytes(request.AccessToken ?? "");
                    var expected = Encoding.UTF8.GetBytes(token);
                    if (supplied.Length != expected.Length ||
                        !CryptographicOperations.FixedTimeEquals(supplied, expected))
                        reply = new RoiDraftSaveReply(false, "ROI_OPERATOR_SESSION_DENIED", null);
                    else
                    {
                        var saved = store.Save(request.Edit);
                        reply = new RoiDraftSaveReply(true, "SIM_ONLY_DRAFT_SAVED_NOT_ACTIVE", saved);
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(WireFormat.Serialize(reply) + "\n");
                await stream.WriteAsync(bytes, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
                or InvalidOperationException or ArgumentException or OperationCanceledException
                or System.Text.Json.JsonException or SqliteException)
            {
                // No hardware write occurred and no success ACK must be fabricated.
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(WireFormat.Serialize(
                        new RoiDraftSaveReply(false, "ROI_DRAFT_REJECTED:" + ex.GetType().Name, null)) + "\n");
                    await socket.GetStream().WriteAsync(bytes, ct);
                }
                catch (Exception) { }
            }
        }
    }
}
