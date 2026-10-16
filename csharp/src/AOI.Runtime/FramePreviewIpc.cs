using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AOI.Runtime;

/// <summary>
/// Local read-only PNG preview endpoint. Requests are WorkpieceId|ChannelId + LF.
/// Responses: big-endian int32 byte length followed by verified PNG; zero means
/// unavailable/rejected. No path inputs, no PLC controls and no remote listeners.
/// </summary>
public static class FramePreviewIpc
{
    public const int MaxPreviewBytes = 2 * 1024 * 1024;
    private const int MaxRequestBytes = 80;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[]? ReadVerifiedPng(ProductionStore store, MachineRole role,
        string nodeFolder, Guid workpieceId, string channelId)
    {
        if (workpieceId == Guid.Empty || string.IsNullOrWhiteSpace(channelId))
            return null;
        ChannelDefinition channel;
        try { channel = ChannelCatalog.Require(channelId); }
        catch (ArgumentException) { return null; }
        if (channel.OwnerRole != role) return null;
        var identity = store.FindWorkpiece(workpieceId);
        if (identity is null) return null;
        var result = store.ResultsFor(workpieceId)
            .Where(r => r.ChannelId == channelId)
            .OrderByDescending(r => r.AttemptNo)
            .FirstOrDefault();
        if (result is null || !result.IsSimulated || result.Outcome is ChannelOutcome.Missing)
            return null;
        try { result.ValidateFor(identity); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return null; }
        string root = Path.GetFullPath(Path.Combine(nodeFolder, "vision"));
        string dbFile = Path.Combine(root, "vision-assets.sqlite");
        if (!File.Exists(dbFile)) return null;
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        };
        try
        {
            using var db = new SqliteConnection(connection.ToString());
            db.Open();
            var asset = ReadAsset(db, result.FrameId);
            if (asset is null || asset.FrameId != result.FrameId ||
                asset.WorkpieceId != identity.WorkpieceId || asset.TraceId != identity.TraceId ||
                asset.Epoch != identity.Epoch || asset.ReleaseId != identity.ReleaseId ||
                asset.ChannelId != result.ChannelId || asset.PhysicalDeviceId != channel.PhysicalDeviceId ||
                asset.CaptureId != result.CaptureId ||
                asset.ParentFrameId != result.ParentFrameId ||
                asset.SourceFrameSha256 != result.SourceFrameSha256)
                return null;
            // For a shared line camera verify the immutable ORIGINAL parent before the ROI.
            if (asset.ParentFrameId is Guid parentFrameId)
            {
                var parent = ReadAsset(db, parentFrameId);
                if (parent is null || parent.ChannelId is not null || parent.ParentFrameId is not null ||
                    parent.WorkpieceId != identity.WorkpieceId || parent.TraceId != identity.TraceId ||
                    parent.CaptureId != asset.CaptureId ||
                    parent.PhysicalDeviceId != asset.PhysicalDeviceId ||
                    parent.ContentSha256 != asset.SourceFrameSha256 ||
                    ReadContent(root, parent) is null)
                    return null;
            }
            return ReadContent(root, asset);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { return null; } // corrupted/missing pixels must never become a preview
    }

    private static VisionAsset? ReadAsset(SqliteConnection db, Guid frameId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_json FROM vision_assets WHERE frame_id=$id";
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        return cmd.ExecuteScalar() is string json ? WireFormat.Parse<VisionAsset>(json) : null;
    }

    private static byte[]? ReadContent(string root, VisionAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.RelativePath) || Path.IsPathRooted(asset.RelativePath)) return null;
        string full = Path.GetFullPath(Path.Combine(root, asset.RelativePath));
        string relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) || !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return null;
        var file = new FileInfo(full);
        if (!file.Exists || file.Length < Signature.Length || file.Length > MaxPreviewBytes)
            return null;
        byte[] png = File.ReadAllBytes(full);
        if (png.Length != file.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            return null;
        string digest = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        return string.Equals(digest, asset.ContentSha256, StringComparison.Ordinal) ? png : null;
    }

    public static async Task ServeAsync(ProductionStore store, MachineRole role,
        string nodeFolder, int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"FRAME_PREVIEW_IPC_LISTEN {role} 127.0.0.1:{port} SIM_ONLY");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var timed = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timed.CancelAfter(TimeSpan.FromSeconds(3));
                        try
                        {
                            var stream = client.GetStream();
                            var request = new byte[MaxRequestBytes];
                            int count = 0;
                            while (count < request.Length)
                            {
                                int n = await stream.ReadAsync(request.AsMemory(count, 1), timed.Token);
                                if (n == 0) return;
                                if (request[count] == 10) break;
                                count++;
                            }
                            byte[]? png = null;
                            if (count < request.Length)
                            {
                                string line = Encoding.ASCII.GetString(request, 0, count);
                                var parts = line.Split('|');
                                if (parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out var id))
                                    png = ReadVerifiedPng(store, role, nodeFolder, id, parts[1]);
                            }
                            byte[] header = new byte[4];
                            BinaryPrimitives.WriteInt32BigEndian(header, png?.Length ?? 0);
                            await stream.WriteAsync(header, timed.Token);
                            if (png is not null)
                                await stream.WriteAsync(png, timed.Token);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException) { }
                    }
                }, ct);
            }
        }
        finally { listener.Stop(); }
    }
}
