using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class BootstrapTests
{
    private string _root = string.Empty;
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TearDown]
    public void CleanUp() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Test]
    public void Topology_UsesRealNinePhysicalDevicesAndElevenLogicalChannels()
    {
        Assert.That(ChannelCatalog.All, Has.Count.EqualTo(11));
        Assert.That(ChannelCatalog.All.Select(x => x.PhysicalDeviceId).Distinct().ToArray(), Has.Length.EqualTo(9));
        Assert.That(ChannelCatalog.Require("CH01").IsPositioning, Is.True);
        Assert.That(ChannelCatalog.Require("CH08").SharedFrameGroup,
            Is.EqualTo(ChannelCatalog.Require("CH09").SharedFrameGroup));
        Assert.That(ChannelCatalog.Require("CH10").SharedFrameGroup,
            Is.EqualTo(ChannelCatalog.Require("CH11").SharedFrameGroup));
        Assert.That(ChannelCatalog.Require("CH02").StationId, Is.EqualTo("ST03"));
        Assert.That(ChannelCatalog.Require("CH03").PlcSignalAddress, Is.EqualTo("D1007"));
    }

    [Test]
    public void DifferentPhysicalItemsWithIdenticalBarcodesStillHaveDifferentKeys()
    {
        var a = WorkpieceIdentity.New("same-barcode", 1, "TEST_ONLY");
        var b = WorkpieceIdentity.New("same-barcode", 1, "TEST_ONLY");
        Assert.That(a.WorkpieceId, Is.Not.EqualTo(b.WorkpieceId));
        Assert.That(a.Barcode, Is.EqualTo(b.Barcode));
    }

    [Test]
    public void NoFrameCannotEverBecomeGood()
    {
        var w = WorkpieceIdentity.New("SIM001", 1, "TEST_ONLY");
        var decision = QualityRules.Evaluate(w, ["CH02", "CH06"], [], true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
        Assert.That(decision.Proof, Does.Contain("MISSING_CHANNELS"));
    }

    [Test]
    public void WrongIdentityAndDifferentEpochAreHeldNotRemappedByBarcode()
    {
        var a = WorkpieceIdentity.New("SAME", 3, "RELEASE_A");
        var b = WorkpieceIdentity.New("SAME", 3, "RELEASE_A");
        var result = SyntheticVision.Generate(a, "CH02", Path.Combine(_root, "assets"));
        var decision = QualityRules.Evaluate(b, ["CH02"], [result], true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
        Assert.That(decision.Proof, Does.Contain("ASSOCIATION_UNCERTAIN"));
        var oldEpoch = result with { Epoch = 2 };
        decision = QualityRules.Evaluate(a, ["CH02"], [oldEpoch], true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
    }

    [Test]
    public void IncomingAckCanOnlyFollowCommittedDatabaseState()
    {
        var w = WorkpieceIdentity.New("SIM001", 1, "SIM_LIGHT_TEST_RELEASE");
        var master = new ProductionStore(Path.Combine(_root, "m", "db.sqlite"));
        var slave = new ProductionStore(Path.Combine(_root, "s", "db.sqlite"));
        var register = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        master.CreateAndQueueRegistration(w, register);
        Assert.That(slave.AcceptIncoming(register, "slave"), Is.False);
        Assert.That(slave.AcceptIncoming(register, "slave"), Is.True);
        Assert.That(slave.LatestWorkpiece(), Is.EqualTo(w));
        Assert.That(master.PendingOutbox(), Has.Count.EqualTo(1));
        master.PersistAcknowledgement(new PersistedAck(register.MessageId, "slave", register.PayloadHash, DateTimeOffset.UtcNow));
        Assert.That(master.PendingOutbox(), Is.Empty);

        var wrong = PeerEnvelope.Create("master", "slave", 2, w, "workpiece_registered", w with { Barcode = "FAKE" })
            with { MessageId = register.MessageId };
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(wrong, "slave"));
    }

    [Test]
    public void SqliteActuallyUsesWalAndPersistentResultsCarryRealPixelSha256()
    {
        var w = WorkpieceIdentity.New("SIM001", 1, "SIM_LIGHT_TEST_RELEASE");
        var store = new ProductionStore(Path.Combine(_root, "db.sqlite"));
        var registration = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        store.CreateAndQueueRegistration(w, registration);
        var result = SyntheticVision.Generate(w, "CH06", Path.Combine(_root, "assets"));
        Assert.That(result.SourceFrameSha256, Has.Length.EqualTo(64));
        Assert.That(result.Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(result.IsSimulated, Is.True);
        store.SaveLocalResult(result);
        Assert.That(store.ResultsFor(w.WorkpieceId), Has.Count.EqualTo(1));
        using var db = new SqliteConnection($"Data Source={store.DatabasePath}");
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode";
        Assert.That(pragma.ExecuteScalar(), Is.EqualTo("wal"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "assets"), "*.png", SearchOption.AllDirectories), Has.Length.EqualTo(1));
    }

    [Test]
    public void PlcProtocolDoesActualFinsMemoryReadAndWrite_ButOnlyToMemoryFixture()
    {
        var plc = new VirtualPlc("SIM-0001");
        var read = FinsDm.Request(false, 1999, new ushort[1], 22);
        var reply = plc.Handle(read);
        Assert.That(FinsDm.DecodeResponse(read, reply), Is.EqualTo(new ushort[] { 1 }));
        var write = FinsDm.Request(true, 1999, new ushort[] { 2 }, 23);
        FinsDm.DecodeResponse(write, plc.Handle(write));
        Assert.That(plc.ReadForTest(1999), Is.EqualTo(2));
        var getBarcode = FinsDm.Request(false, 2000, new ushort[10], 24);
        var words = FinsDm.DecodeResponse(getBarcode, plc.Handle(getBarcode));
        Assert.That(FinsDm.SimBarcode(words), Is.EqualTo("SIM-0001"));
    }

    [Test]
    public void FinalMustNotUseSimulatedResultsAsProductionProof()
    {
        var w = WorkpieceIdentity.New("SIM001", 1, "TEST_ONLY");
        var result = SyntheticVision.Generate(w, "CH02", Path.Combine(_root, "assets"));
        var decision = QualityRules.Evaluate(w, ["CH02"], [result], true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Good));
        Assert.That(decision.IsSimulation, Is.True);
        Assert.That(decision.Proof, Does.Contain("SIM_ONLY"));
    }
}
