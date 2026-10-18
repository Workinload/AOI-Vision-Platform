namespace AOI.Diagnostics;

/// <summary>
/// Copies two STOPPED Master WALs into a throwaway directory before ever
/// invoking SQLite. SQLite ReadOnly may still checkpoint/modify *-wal/*-shm on
/// Windows; it is NEVER permitted to open the original accident evidence.
/// </summary>
internal sealed class CrossLedgerSqliteSnapshot : IDisposable
{
    private readonly string _temporaryRoot;
    public string CopiedRoot => _temporaryRoot;
    public string A1Path => Path.Combine(_temporaryRoot, "master", "final-plc-output.db");
    public string A7Path => Path.Combine(_temporaryRoot, "master", ProductionTimingJournal.FileName);

    private CrossLedgerSqliteSnapshot(string temporaryRoot) =>
        _temporaryRoot = temporaryRoot;

    public static CrossLedgerSqliteSnapshot Capture(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var master = Path.Combine(Path.GetFullPath(sourceRoot), "master");
        var a1 = Path.Combine(master, "final-plc-output.db");
        var a7 = Path.Combine(master, ProductionTimingJournal.FileName);
        if (!File.Exists(a1) || !File.Exists(a7))
            throw new FileNotFoundException("A1 or A7 persistent output WAL is missing.");

        // A live A1 Core holds an exclusive writer lease. Do not take an
        // inconsistent cross-database snapshot or manufacture final success.
        var leasePath = a1 + ".writer-lock";
        if (!File.Exists(leasePath))
            throw new InvalidDataException(
                "Cannot prove source Core has stopped: A1 writer lease missing.");

        // Claim a read-only exclusive handle WITHOUT creating/modifying source.
        using var stopped = new FileStream(leasePath, FileMode.Open,
            FileAccess.Read, FileShare.None);

        // Lock ALL source DB+WAL+SHM files before copying ANY file. A7 writes
        // must be quiescent too, not merely the A1 writer. No source SQLite
        // connection is ever opened, and no source file is created.
        var openSources = new List<(FileStream Stream, string Name)>();
        string snapshotRoot = Path.Combine(Path.GetTempPath(),
            "aoi-a7-cross-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string file in new[] { a1, a7 })
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                {
                    string source = file + suffix;
                    if (!File.Exists(source)) continue;
                    openSources.Add((new FileStream(source, FileMode.Open,
                        FileAccess.Read, FileShare.None), Path.GetFileName(source)));
                }

            Directory.CreateDirectory(Path.Combine(snapshotRoot, "master"));
            foreach (var (stream, name) in openSources)
            {
                using var destination = new FileStream(
                    Path.Combine(snapshotRoot, "master", name),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.CopyTo(destination);
            }
            return new CrossLedgerSqliteSnapshot(snapshotRoot);
        }
        catch
        {
            if (Directory.Exists(snapshotRoot))
                Directory.Delete(snapshotRoot, recursive: true);
            throw;
        }
        finally
        {
            foreach (var (stream, _) in openSources) stream.Dispose();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }
}
