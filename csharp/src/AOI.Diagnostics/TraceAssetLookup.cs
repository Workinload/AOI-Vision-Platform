using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace AOI.Diagnostics;

/// <summary>
/// Resolves A3's persisted vision-assets.sqlite and older A0 synthetic PNG assets.
/// Read-only: a missing image is shown as missing, never synthesized or silently replaced.
/// </summary>
internal static class TraceAssetLookup
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    // Projection of A3's immutable vision_assets.asset_json, not a copied runtime implementation.
    private sealed record AssetMetadata(
        Guid FrameId, Guid WorkpieceId, Guid TraceId, long Epoch, string ReleaseId,
        Guid CaptureId, string PhysicalDeviceId, string? ChannelId,
        Guid? ParentFrameId, int AttemptNo,
        string ContentSha256, string SourceFrameSha256, string RelativePath);

    public static TraceImage Lookup(string root, string node, ChannelResult result, bool verify)
    {
        string nodeRoot = Path.GetFullPath(Path.Combine(root, node));
        string visionRoot = Path.Combine(nodeRoot, "vision");
        string dbPath = Path.Combine(visionRoot, "vision-assets.sqlite");
        if (!File.Exists(dbPath)) return Legacy(nodeRoot, node, result, verify);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        AssetMetadata? asset = ReadAsset(db, result.FrameId);
        if (asset is null)
            return Empty(node, result, "NOT_INDEXED");
        if (!Matches(result, asset))
            return Empty(node, result, "METADATA_MISMATCH");
        var parent = result.ParentFrameId is Guid parentId ? ReadAsset(db, parentId) : null;
        if (result.ParentFrameId is not null &&
            (parent is null || parent.ChannelId is not null || parent.ParentFrameId is not null ||
             parent.FrameId != result.ParentFrameId || parent.WorkpieceId != result.WorkpieceId ||
             parent.TraceId != result.TraceId || parent.CaptureId != result.CaptureId ||
             parent.PhysicalDeviceId != result.PhysicalDeviceId ||
             parent.ContentSha256 != result.SourceFrameSha256))
            return Empty(node, result, "LINE_PARENT_MISSING_OR_MISMATCH");

        bool childExists = SafePath(visionRoot, asset.RelativePath, out string? childPath) &&
            File.Exists(childPath);
        bool parentExists = parent is null || SafePath(visionRoot, parent.RelativePath, out string? parentPath) &&
            File.Exists(parentPath);
        if (!childExists || !parentExists)
            return new TraceImage(node, result.ChannelId, result.FrameId, result.ParentFrameId,
                childExists ? "vision/" + asset.RelativePath.Replace('\\', '/') : null,
                parent is not null && parentExists ? "vision/" + parent.RelativePath.Replace('\\', '/') : null,
                asset.ContentSha256, result.SourceFrameSha256, false, false, "FILE_MISSING");
        bool? verified = verify ? VerifyPng(childPath!, asset.ContentSha256) &&
            (parent is null || VerifyPng(Path.Combine(visionRoot, parent.RelativePath), parent.ContentSha256))
            : null;
        return new TraceImage(node, result.ChannelId, result.FrameId, result.ParentFrameId,
            "vision/" + asset.RelativePath.Replace('\\', '/'),
            parent is null ? null : "vision/" + parent.RelativePath.Replace('\\', '/'),
            asset.ContentSha256, result.SourceFrameSha256, true, verified,
            verified is false ? "SHA256_MISMATCH" : verified is true ? "VERIFIED" : "PRESENT_UNVERIFIED");
    }

    private static AssetMetadata? ReadAsset(SqliteConnection db, Guid frameId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_json FROM vision_assets WHERE frame_id=$id";
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : WireFormat.Parse<AssetMetadata>(json);
    }

    private static bool Matches(ChannelResult r, AssetMetadata a) =>
        a.FrameId == r.FrameId && a.WorkpieceId == r.WorkpieceId &&
        a.TraceId == r.TraceId && a.Epoch == r.Epoch && a.ReleaseId == r.ReleaseId &&
        a.ChannelId == r.ChannelId && a.PhysicalDeviceId == r.PhysicalDeviceId &&
        a.CaptureId == r.CaptureId && a.ParentFrameId == r.ParentFrameId &&
        a.AttemptNo == r.AttemptNo && a.SourceFrameSha256 == r.SourceFrameSha256 &&
        IsSha256(a.ContentSha256);

    private static TraceImage Legacy(string nodeRoot, string node, ChannelResult result, bool verify)
    {
        if (result.ParentFrameId is not null) return Empty(node, result, "NOT_INDEXED");
        string relative = Path.Combine("assets", result.WorkpieceId.ToString("N"),
            result.ChannelId, result.FrameId.ToString("N") + ".png");
        if (!SafePath(nodeRoot, relative, out var path) || !File.Exists(path))
            return Empty(node, result, "FILE_MISSING");
        bool? verified = verify ? VerifyPng(path!, result.SourceFrameSha256) : null;
        return new TraceImage(node, result.ChannelId, result.FrameId, null,
            relative.Replace('\\', '/'), null, result.SourceFrameSha256,
            result.SourceFrameSha256, true, verified,
            verified is false ? "SHA256_MISMATCH" : verified is true ? "VERIFIED" : "PRESENT_UNVERIFIED");
    }

    private static TraceImage Empty(string node, ChannelResult result, string state) =>
        new(node, result.ChannelId, result.FrameId, result.ParentFrameId, null,
            null, null, result.SourceFrameSha256, false, null, state);

    private static bool SafePath(string root, string relative, out string? full)
    {
        full = null;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        var actualRelative = Path.GetRelativePath(root, candidate);
        if (actualRelative == "." || actualRelative == ".." ||
            actualRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(actualRelative)) return false;
        full = candidate;
        return true;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool VerifyPng(string path, string digest)
    {
        if (!IsSha256(digest)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < PngSignature.Length) return false;
            Span<byte> actual = stackalloc byte[PngSignature.Length];
            stream.ReadExactly(actual);
            if (!actual.SequenceEqual(PngSignature)) return false;
            stream.Position = 0;
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return hash.Equals(digest, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }
}
