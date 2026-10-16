using AOI.Diagnostics;
using AOI.Domain;
using NUnit.Framework;

namespace AOI.Diagnostics.Tests;

[TestFixture]
public sealed class FlightRecorderTests
{
    private string _root = "";
    [SetUp] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "a7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static DiagnosticSnapshot Snapshot(NodeEvidence[] nodes, DateTimeOffset? now = null) =>
        new(1, DiagnosticsFormat.SimMode, now ?? DateTimeOffset.UtcNow, nodes);

    private static NodeEvidence Node(string id, WorkpieceEvidence[]? workpieces = null,
        ChannelResult[]? results = null, OutboxEvidence[]? outbox = null, InboxEvidence[]? inbox = null) =>
        new(id, true, workpieces ?? [], results ?? [], outbox ?? [], inbox ?? [], []);

    [Test]
    public void FlightJournal_AppendAndReplayAreDeterministic_AndTamperDetected()
    {
        string path = Path.Combine(_root, "flight.jsonl");
        var snapshot = Snapshot([Node("master"), Node("slave")]);
        var first = FlightRecorder.Append(path, snapshot);
        var second = FlightRecorder.Append(path, snapshot);
        Assert.That(first.Sequence, Is.EqualTo(1));
        Assert.That(second.PreviousSha256, Is.EqualTo(first.EntrySha256));
        Assert.That(DiagnosticAnalyzer.Replay(path, TimeSpan.FromSeconds(5)), Has.Length.EqualTo(2));
        File.AppendAllText(path, "corrupted\n");
        Assert.Throws<InvalidDataException>(() => FlightRecorder.ReadVerified(path));
    }

    [Test]
    public void MissingPeerInboxAfterAck_IsCritical()
    {
        var wp = WorkpieceIdentity.New("SIM", 1, "TEST");
        var ack = new OutboxEvidence(Guid.NewGuid(), "master", "slave",
            wp.WorkpieceId, wp.TraceId, wp.Epoch, wp.ReleaseId,
            "workpiece_registered", null, new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, true);
        var report = DiagnosticAnalyzer.Analyze(Snapshot([
            Node("master", [new(wp, null)], outbox: [ack]),
            Node("slave", [new(wp, null)])
        ]), TimeSpan.FromMinutes(5));
        Assert.That(report.Findings.Select(x => x.Code), Does.Contain("ACK_WITHOUT_RECEIVER_INBOX"));
    }

    [Test]
    public void CrossNodeIdentityMismatchAndStallAreDetected()
    {
        var wp = WorkpieceIdentity.New("A", 2, "RELEASE");
        var older = wp with { TraceId = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        var report = DiagnosticAnalyzer.Analyze(Snapshot([
            Node("master", [new(wp, null)]), Node("slave", [new(older, null)])
        ], now.AddMinutes(2)), TimeSpan.FromSeconds(30));
        Assert.That(report.Findings.Select(x => x.Code), Does.Contain("CROSS_NODE_IDENTITY_CONFLICT"));
        Assert.That(report.Findings.Select(x => x.Code), Does.Contain("WORKPIECE_STALLED"));
    }

    [Test]
    public void SameFrameUsedForDistinctWorkpieces_IsCritical()
    {
        var a = WorkpieceIdentity.New("same", 1, "R");
        var b = WorkpieceIdentity.New("same", 1, "R");
        Guid frame = Guid.NewGuid();
        ChannelResult Make(WorkpieceIdentity w) => new(Guid.NewGuid(), w.WorkpieceId, w.Epoch,
            w.ReleaseId, w.TraceId, "CH06", "ST05", "HIK-M-2", Guid.NewGuid(),
            frame, null, 1, new string('a', 64), ChannelOutcome.Ok, true, DateTimeOffset.UtcNow);
        var report = DiagnosticAnalyzer.Analyze(Snapshot([
            Node("master", [new(a, null), new(b, null)], [Make(a), Make(b)]), Node("slave")
        ]), TimeSpan.FromMinutes(1));
        Assert.That(report.Findings.Select(x => x.Code), Does.Contain("FRAME_REUSED_ACROSS_WORKPIECES"));
    }

    [Test]
    public void BundleVerifiedAndModifiedZipRejected()
    {
        string journal = Path.Combine(_root, "flight.jsonl");
        string archive = Path.Combine(_root, "bundle.zip");
        FlightRecorder.Append(journal, Snapshot([Node("master"), Node("slave")]));
        DiagnosticBundle.Export(journal, archive, TimeSpan.FromSeconds(10));
        Assert.DoesNotThrow(() => DiagnosticBundle.Verify(archive));
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("findings.json")!.Delete();
            var entry = zip.CreateEntry("findings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("tampered");
        }
        Assert.Throws<InvalidDataException>(() => DiagnosticBundle.Verify(archive));
    }

    [Test]
    public void MissingWalsNeverProduceHealthyAssessment()
    {
        var snapshot = ReadOnlyCapture.Capture(_root);
        var report = DiagnosticAnalyzer.Analyze(snapshot, TimeSpan.FromSeconds(1));
        Assert.That(report.Findings.Count(x => x.Code == "NODE_DB_MISSING"), Is.EqualTo(2));
    }
}
