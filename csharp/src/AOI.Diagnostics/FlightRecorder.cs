using System.Text;

namespace AOI.Diagnostics;

/// <summary>Append-only JSONL flight recorder. Chain verification fails closed on malformed/truncated records.</summary>
public static class FlightRecorder
{
    public static IReadOnlyList<FlightRecord> ReadVerified(string path)
    {
        var entries = new List<FlightRecord>();
        if (!File.Exists(path)) return entries;
        string previous = DiagnosticsFormat.GenesisHash;
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("Blank journal record.");
            var entry = DiagnosticsFormat.Parse<FlightRecord>(line);
            if (entry.FormatVersion != DiagnosticsFormat.JournalVersion ||
                entry.Sequence != entries.Count + 1 ||
                entry.PreviousSha256 != previous ||
                entry.Snapshot.SchemaVersion != DiagnosticsFormat.SnapshotVersion ||
                entry.Snapshot.Mode != DiagnosticsFormat.SimMode ||
                entry.SnapshotSha256 != DiagnosticsFormat.Sha256(DiagnosticsFormat.Serialize(entry.Snapshot)) ||
                entry.EntrySha256 != ComputeEntryHash(entry))
                throw new InvalidDataException($"Journal integrity check failed at sequence {entries.Count + 1}.");
            entries.Add(entry);
            previous = entry.EntrySha256;
        }
        return entries;
    }

    public static FlightRecord Append(string path, DiagnosticSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != DiagnosticsFormat.SnapshotVersion ||
            snapshot.Mode != DiagnosticsFormat.SimMode ||
            snapshot.Nodes.Any(x => x.NodeId is not ("master" or "slave")))
            throw new InvalidDataException("Only supported SIM_ONLY read-only evidence is permitted.");
        var previous = ReadVerified(path);
        var record = new FlightRecord(DiagnosticsFormat.JournalVersion, previous.Count + 1,
            previous.Count == 0 ? DiagnosticsFormat.GenesisHash : previous[^1].EntrySha256,
            DiagnosticsFormat.Sha256(DiagnosticsFormat.Serialize(snapshot)), "", snapshot);
        record = record with { EntrySha256 = ComputeEntryHash(record) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine(DiagnosticsFormat.Serialize(record));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        return record;
    }

    private static string ComputeEntryHash(FlightRecord value) =>
        DiagnosticsFormat.Sha256(DiagnosticsFormat.Serialize(value with { EntrySha256 = "" }));
}
