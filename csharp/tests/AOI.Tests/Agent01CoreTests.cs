using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent01CoreTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ProductionStore Store(string node) =>
        new(Path.Combine(_root, node, "production.db"));

    private static WorkpieceIdentity Item(string barcode = "SAME") =>
        WorkpieceIdentity.New(barcode, 7, SimOnlySlaveRecovery.FixtureRelease);

    private static ChannelResult Fixture(WorkpieceIdentity w, string channel) =>
        new(Guid.NewGuid(), w.WorkpieceId, w.Epoch, w.ReleaseId, w.TraceId,
            channel, ChannelCatalog.Require(channel).StationId,
            ChannelCatalog.Require(channel).PhysicalDeviceId,
            Guid.NewGuid(), Guid.NewGuid(), null, 1, new string('a', 64),
            ChannelOutcome.Ok, true, DateTimeOffset.UtcNow);

    [Test]
    public void ConcurrentSameBarcodeWorkpiecesAreNeverMappedToLatest()
    {
        var store = Store("master");
        var older = Item();
        var newer = Item();
        store.CreateAndQueueRegistration(older,
            PeerEnvelope.Create("master", "slave", 1, older, "workpiece_registered", older));
        store.CreateAndQueueRegistration(newer,
            PeerEnvelope.Create("master", "slave", 2, newer, "workpiece_registered", newer));
        Assert.That(store.FindWorkpiece(older.WorkpieceId), Is.EqualTo(older));
        Assert.That(store.FindWorkpiece(newer.WorkpieceId), Is.EqualTo(newer));
        Assert.That(store.FindWorkpiece(Guid.NewGuid()), Is.Null);
        Assert.That(older.Barcode, Is.EqualTo(newer.Barcode));
    }

    [Test]
    public void FlowHoldsMissingChannelAndWrongEndScanLineage()
    {
        var w = Item();
        var waiting = WorkpieceFlow.Assess(w, ["CH02", "CH06"], [Fixture(w, "CH06")],
            EndScanEvidence.SimulatedFixtureEcho(w, w.Barcode), null, true);
        Assert.That(waiting.Phase, Is.EqualTo(WorkpiecePhase.WaitingForChannels));

        var completed = new[] { Fixture(w, "CH02"), Fixture(w, "CH06") };
        var scan = EndScanEvidence.SimulatedFixtureEcho(w, w.Barcode);
        Assert.That(WorkpieceFlow.Assess(w, ["CH02", "CH06"], completed, null, null, true).Phase,
            Is.EqualTo(WorkpiecePhase.WaitingForEndVerification));
        Assert.That(WorkpieceFlow.Assess(w, ["CH02", "CH06"], completed,
            scan with { WorkpieceId = Guid.NewGuid() }, null, true).Phase,
            Is.EqualTo(WorkpiecePhase.Held));
        var good = WorkpieceFlow.Assess(w, ["CH02", "CH06"], completed, scan, null, true);
        Assert.That(good.Phase, Is.EqualTo(WorkpiecePhase.ReadyToCommit));
        Assert.That(good.Decision!.Disposition, Is.EqualTo(FinalDisposition.Good));
        Assert.That(good.Decision.IsSimulation, Is.True);
        Assert.That(good.Decision.Proof, Does.Contain("SIM_ONLY"));
    }

    [Test]
    public void FlowRejectsForeignCapturedFrameEvenWithMatchingBarcode()
    {
        var w = Item();
        var foreign = Item();
        var assessment = WorkpieceFlow.Assess(w, ["CH02"], [Fixture(foreign, "CH02")],
            EndScanEvidence.SimulatedFixtureEcho(w, w.Barcode), null, true);
        Assert.That(assessment.Phase, Is.EqualTo(WorkpiecePhase.Held));
        Assert.That(assessment.Reason, Does.Contain("INVALID_CAPTURE_LINEAGE"));
    }

    [Test]
    public void SlaveRecoversAfterInboxCommitBeforeResultAndDoesNotDuplicateOutput()
    {
        var master = Store("master");
        var slave = Store("slave");
        var w = Item();
        var registration = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        master.CreateAndQueueRegistration(w, registration);
        Assert.That(slave.AcceptIncoming(registration, "slave"), Is.False);
        // Process crashed here: receiver may already have sent PERSISTED_ACK.
        var restartedStore = Store("slave");
        int generated = 0;
        ChannelResult Capture(WorkpieceIdentity identity)
        {
            generated++;
            return Fixture(identity, "CH02");
        }
        Assert.That(SimOnlySlaveRecovery.RecoverMissingResults(restartedStore, Capture),
            Is.EqualTo(1));
        Assert.That(SimOnlySlaveRecovery.RecoverMissingResults(restartedStore, Capture),
            Is.Zero);
        Assert.That(generated, Is.EqualTo(1));
        Assert.That(restartedStore.PendingOutbox(), Has.Count.EqualTo(1));
        Assert.That(restartedStore.ResultsFor(w.WorkpieceId), Has.Count.EqualTo(1));
    }

    [Test]
    public void SlaveRecoveryNeverGeneratesFixturesForOtherReleases()
    {
        var slave = Store("slave");
        var w = WorkpieceIdentity.New("SIM", 8, "REAL_UNVERIFIED_RELEASE");
        var registration = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        Assert.That(slave.AcceptIncoming(registration, "slave"), Is.False);
        Assert.That(SimOnlySlaveRecovery.RecoverMissingResults(slave,
            _ => throw new Exception("Must not generate")), Is.Zero);
    }

    [Test]
    public void OutboxRetryIsBoundedAndAcknowledgedMessagesDropOut()
    {
        var w = Item();
        var envelope = PeerEnvelope.Create("master", "slave", 1, w,
            "workpiece_registered", w);
        var planner = new OutboxRetryPlanner(TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(400));
        var t = DateTimeOffset.UtcNow;
        Assert.That(planner.SelectDue([envelope], t), Has.Count.EqualTo(1));
        Assert.That(planner.SelectDue([envelope], t.AddMilliseconds(50)), Is.Empty);
        Assert.That(planner.SelectDue([envelope], t.AddMilliseconds(100)), Has.Count.EqualTo(1));
        Assert.That(planner.SelectDue([envelope], t.AddMilliseconds(299)), Is.Empty);
        Assert.That(planner.SelectDue([envelope], t.AddMilliseconds(300)), Has.Count.EqualTo(1));
        Assert.That(planner.AttemptsFor(envelope.MessageId), Is.EqualTo(3));
        Assert.That(planner.SelectDue([], t.AddSeconds(10)), Is.Empty);
        Assert.That(planner.AttemptsFor(envelope.MessageId), Is.Zero);
    }
}
