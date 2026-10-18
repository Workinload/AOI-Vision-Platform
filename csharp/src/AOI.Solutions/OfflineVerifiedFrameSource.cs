using AOI.Domain;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace AOI.Solutions;

/// <summary>One channel's verified source-frame size read from a reference run root.</summary>
public sealed record VerifiedFrameReference(
    string ChannelId, RoiFrameSize Size, string RelativePath, string Sha256, string Provenance);

/// <summary>
/// Read-only source of REAL captured frame sizes for offline ROI editing. It reads the
/// A3 vision asset ledger of an existing run root (master/slave), verifies each stored PNG's
/// SHA256 against the ledger row, and returns the true pixel size. It never creates, repairs,
/// writes or deletes anything, and a tampered/missing PNG is simply omitted (fail closed).
///
/// This exists because a NEW clean product root has no captured images yet, while ROI pixel
/// mapping must use real dimensions. The operator keeps a prior run root as a read-only
/// reference and writes the new product/ROI into a different, clean root.
/// </summary>
public static class OfflineVerifiedFrameSource
{
    public const string LedgerFileName = "vision-assets.sqlite";

    public static IReadOnlyList<VerifiedFrameReference> LoadFromRunRoot(
        string referenceRunRoot, IEnumerable<string>? channelIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceRunRoot);
        string root = Path.GetFullPath(referenceRunRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("参考 run-root 不存在：" + root);
        var wanted = channelIds is null
            ? ChannelCatalog.All.Select(c => c.ChannelId)
            : channelIds.Where(id => ChannelCatalog.All.Any(c => c.ChannelId == id));
        var results = new List<VerifiedFrameReference>();
        foreach (string channelId in wanted.Distinct(StringComparer.Ordinal))
        {
            VerifiedFrameReference? found = TryReadChannel(root, channelId);
            if (found is not null) results.Add(found);
        }
        return results;
    }

    private static VerifiedFrameReference? TryReadChannel(string root, string channelId)
    {
        ChannelDefinition channel = ChannelCatalog.Require(channelId);
        string role = channel.OwnerRole == MachineRole.Master ? "master" : "slave";
        string ledgerRoot = Path.Combine(root, role, "vision");
        string databasePath = Path.Combine(ledgerRoot, LedgerFileName);
        if (!File.Exists(databasePath)) return null;
        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT asset_json, relative_path FROM vision_assets
                WHERE channel_id=$channel AND kind IN ('area','child')
                ORDER BY rowid DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("$channel", channelId);
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            string json = row.GetString(0);
            string relativePath = row.GetString(1);
            using var document = JsonDocument.Parse(json);
            JsonElement rootElement = document.RootElement;
            if (!TryGetInt(rootElement, "width", out int width) ||
                !TryGetInt(rootElement, "height", out int height) ||
                !TryGetString(rootElement, "contentSha256", out string? digest) ||
                width <= 0 || height <= 0 || string.IsNullOrEmpty(digest)) return null;
            string pngPath = Path.GetFullPath(Path.Combine(ledgerRoot, relativePath));
            if (!File.Exists(pngPath)) return null;
            string actual = HashFile(pngPath);
            if (!string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase)) return null;
            var size = new RoiFrameSize(width, height);
            size.Validate();
            return new VerifiedFrameReference(channelId, size, relativePath, actual,
                "参考 run-root 已验证 PNG（只读）· " + role + "/vision/" + relativePath.Replace('\\', '/') +
                " · sha256=" + actual[..12]);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or JsonException
            or InvalidDataException or ArgumentException)
        {
            return null; // unreadable reference evidence is never invented
        }
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement found) && found.TryGetInt32(out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement found) ||
            found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString();
        return true;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
