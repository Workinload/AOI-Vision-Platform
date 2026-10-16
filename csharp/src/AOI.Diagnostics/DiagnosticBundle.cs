using System.IO.Compression;
using System.Text;

namespace AOI.Diagnostics;

/// <summary>Portable SIM_ONLY bundle containing verified redacted journal and deterministic findings.</summary>
public static class DiagnosticBundle
{
    public static void Export(string journalPath, string destinationZip, TimeSpan staleAfter)
    {
        var records = FlightRecorder.ReadVerified(journalPath);
        if (records.Count == 0) throw new InvalidDataException("No verified flight records.");
        var journalBytes = File.ReadAllBytes(journalPath);
        var assessments = DiagnosticAnalyzer.Replay(journalPath, staleAfter);
        byte[] findings = Encoding.UTF8.GetBytes(DiagnosticsFormat.Serialize(assessments));
        byte[] triage = Encoding.UTF8.GetBytes(
            "# SIM_ONLY offline triage\n" +
            "1. Verify all SHA256 manifest entries and journal hash chain before replay.\n" +
            "2. Review findings; missing evidence is not permission to pass or drive real hardware.\n" +
            "3. Reproduce against isolated synthetic fixtures, patch on a separate branch, and run tests.\n" +
            "4. Human/A0/A10 must review; no automatic merge, deployment, field gate or PLC output.\n" +
            "5. Event details are redacted; snapshot still contains identity UUIDs, hashes and release IDs. Handle as restricted evidence.\n");
        var manifest = new DiagnosticBundleManifest(1, DiagnosticsFormat.SimMode,
            records[^1].Snapshot.CapturedUtc, records.Count, records[^1].EntrySha256,
            DiagnosticsFormat.Sha256(journalBytes), DiagnosticsFormat.Sha256(findings),
            DiagnosticsFormat.Sha256(triage));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationZip))!);
        using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        Add(zip, "manifest.json", Encoding.UTF8.GetBytes(DiagnosticsFormat.Serialize(manifest)));
        Add(zip, "flight.jsonl", journalBytes);
        Add(zip, "findings.json", findings);
        Add(zip, "TRIAGE.md", triage);
    }

    public static void Verify(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count != 4 ||
            zip.Entries.Any(e => e.FullName is not ("manifest.json" or "flight.jsonl" or "findings.json" or "TRIAGE.md")))
            throw new InvalidDataException("Unexpected diagnostics bundle content.");
        byte[] Read(string path)
        {
            using var stream = zip.GetEntry(path)?.Open()
                ?? throw new InvalidDataException($"Missing {path}.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        var manifest = DiagnosticsFormat.Parse<DiagnosticBundleManifest>(Encoding.UTF8.GetString(Read("manifest.json")));
        if (manifest.BundleVersion != 1 || manifest.Mode != DiagnosticsFormat.SimMode ||
            DiagnosticsFormat.Sha256(Read("flight.jsonl")) != manifest.JournalFileSha256 ||
            DiagnosticsFormat.Sha256(Read("findings.json")) != manifest.FindingsFileSha256 ||
            DiagnosticsFormat.Sha256(Read("TRIAGE.md")) != manifest.TriageFileSha256)
            throw new InvalidDataException("Diagnostics bundle SHA256 verification failed.");
        string scratch = Path.Combine(Path.GetTempPath(), "aoi-diagnostic-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllBytes(scratch, Read("flight.jsonl"));
            var records = FlightRecorder.ReadVerified(scratch);
            if (records.Count != manifest.RecordCount ||
                records.Count == 0 || records[^1].EntrySha256 != manifest.JournalHeadSha256)
                throw new InvalidDataException("Diagnostics bundle chain/head mismatch.");
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }

    private static void Add(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = entry.Open();
        writer.Write(data);
    }
}
