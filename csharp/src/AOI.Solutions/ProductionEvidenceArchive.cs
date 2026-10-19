using System.Security.Cryptography;
using System.Text.Json;

namespace AOI.Solutions;

public sealed record ArchivedEvidenceFile(
    string OriginalRelativePath, string ArchivedRelativePath, long Bytes, string Sha256);

public sealed record ProductionEvidenceArchiveResult(
    string ArchiveDirectory, string ArchiveRelativePath, IReadOnlyList<ArchivedEvidenceFile> Files);

/// <summary>
/// Continuous changeover support: moves the PREVIOUS run's production / conveyor / timing
/// evidence into <run-root>/archive/<utc>/ instead of deleting it, then writes a SHA256
/// manifest. This is the only supported way to make an already-used run root clean again for
/// the next product release; nothing is ever deleted and the original bytes are hash-recorded.
/// It never touches the live A6 release DB, vision assets, node state or the coordinator.
/// </summary>
public static class ProductionEvidenceArchive
{
    public const string ArchiveFolderName = "archive";

    // Per-batch mutable state that must leave the root before the next product can run.
    // Keeping a stale progress/capture DB or a leftover writer-lock makes a fresh batch
    // resume mid-flight or refuse to start, which is exactly the bug this list prevents.
    private static readonly string[] EvidenceNames =
    [
        "production.db", "production.db-wal", "production.db-shm", "production.db.writer-lock",
        "production-progress.db", "production-progress.db-wal", "production-progress.db-shm",
        "production-progress.db.writer-lock",
        "sim-full-cycle.db", "sim-full-cycle.db-wal", "sim-full-cycle.db-shm",
        "sim-full-cycle.db.writer-lock",
        "final-plc-output.db", "final-plc-output.db-wal", "final-plc-output.db-shm",
        "final-plc-output.db.writer-lock",
        "capture-handshake.sqlite", "capture-handshake.sqlite-wal", "capture-handshake.sqlite-shm",
        // Per-batch peer interlock state: a stale WAL from a killed run can keep the next
        // batch waiting forever, so it belongs to the previous batch's archive too.
        "host-peer-interlock.sqlite", "host-peer-interlock.sqlite-wal", "host-peer-interlock.sqlite-shm",
        "timing-events.sqlite", "timing-events.sqlite-wal", "timing-events.sqlite-shm"
    ];

    public static ProductionEvidenceArchiveResult Archive(string runRoot, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);
        SolutionSafety.RequireActor(actor);
        string root = Path.GetFullPath(runRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("run-root 不存在：" + root);

        var found = new List<(string Absolute, string Relative)>();
        foreach (string role in new[] { "master", "slave" })
            foreach (string name in EvidenceNames)
            {
                string path = Path.Combine(root, role, name);
                if (File.Exists(path))
                    found.Add((path, role + "/" + name));
            }
        if (found.Count == 0)
            throw new InvalidOperationException(
                "NOTHING_TO_ARCHIVE: 该 run-root 没有生产/传送带/时序证据，无需归档。");

        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string archiveDirectory = Path.Combine(root, ArchiveFolderName, stamp);
        Directory.CreateDirectory(archiveDirectory);

        var moved = new List<ArchivedEvidenceFile>(found.Count);
        foreach ((string absolute, string relative) in found.OrderBy(x => x.Relative, StringComparer.Ordinal))
        {
            string target = Path.Combine(archiveDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string sha = HashFile(absolute);
            long bytes = new FileInfo(absolute).Length;
            File.Move(absolute, target);
            moved.Add(new ArchivedEvidenceFile(relative,
                Path.GetRelativePath(root, target).Replace('\\', '/'), bytes, sha));
        }

        var manifest = new ArchivedEvidenceManifest(
            DateTimeOffset.UtcNow, actor, ArchiveFolderName + "/" + stamp, moved);
        File.WriteAllText(Path.Combine(archiveDirectory, "archive.json"),
            JsonSerializer.Serialize(manifest, AOI.Contracts.WireFormat.Json));
        return new(archiveDirectory, ArchiveFolderName + "/" + stamp, moved);
    }

    public sealed record ArchivedEvidenceManifest(
        DateTimeOffset Utc, string Actor, string ArchiveRelativePath,
        IReadOnlyList<ArchivedEvidenceFile> Files);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
