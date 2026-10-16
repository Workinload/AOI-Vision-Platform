using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent05FailureModeTests
{
    private string _root = string.Empty;
    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void TamperedEnvelopeMustNeverBecomeAnAck()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var receiver = new ProductionStore(Path.Combine(_root, "s.db"));
        var bad = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w)
            with { PayloadJson = "{\"forged\":true}" };
        Assert.Throws<InvalidDataException>(() => receiver.AcceptIncoming(bad, "slave"));
        Assert.That(receiver.LatestWorkpiece(), Is.Null);
    }

    [Test]
    public void MisroutedEnvelopeFailsClosed()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var store = new ProductionStore(Path.Combine(_root, "m.db"));
        var envelope = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        Assert.Throws<InvalidDataException>(() => store.AcceptIncoming(envelope, "master"));
        Assert.That(store.LatestWorkpiece(), Is.Null);
    }

    [Test]
    public void AckWrongHashLeavesOutboxUnacknowledged()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var store = new ProductionStore(Path.Combine(_root, "m.db"));
        var env = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        store.CreateAndQueueRegistration(w, env);
        Assert.Throws<InvalidDataException>(() => store.PersistAcknowledgement(
            new PersistedAck(env.MessageId, "slave", new string('0', 64), DateTimeOffset.UtcNow)));
        Assert.That(store.PendingOutbox(), Has.Count.EqualTo(1));
    }

    [Test]
    public void SameMessageIdDifferentDigestMustBeRejected()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var store = new ProductionStore(Path.Combine(_root, "s.db"));
        var first = PeerEnvelope.Create("master", "slave", 1, w, "workpiece_registered", w);
        Assert.That(store.AcceptIncoming(first, "slave"), Is.False);
        Assert.That(store.AcceptIncoming(first, "slave"), Is.True);
        var second = PeerEnvelope.Create("master", "slave", 2, w,
            "workpiece_registered", w with { Barcode = "FORGED" })
            with { MessageId = first.MessageId };
        Assert.Throws<InvalidDataException>(() => store.AcceptIncoming(second, "slave"));
        Assert.That(store.LatestWorkpiece(), Is.EqualTo(w));
    }

    [Test]
    public void DuplicateConflictingAttemptNeverCountsAsGood()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var first = SyntheticVision.Generate(w, "CH02", Path.Combine(_root, "assets"));
        var second = first with { ResultId = Guid.NewGuid(), Outcome = ChannelOutcome.Ng };
        var decision = QualityRules.Evaluate(w, ["CH02"], [first, second], true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
        Assert.That(decision.Proof, Does.Contain("CONFLICTING_RESULT_FOR_SAME_ATTEMPT"));
    }

    [Test]
    public void EndBarcodeMismatchMustHoldDespiteAllGoodPixels()
    {
        var w = WorkpieceIdentity.New("SIM", 1, "QA");
        var result = SyntheticVision.Generate(w, "CH06", Path.Combine(_root, "assets"));
        var decision = QualityRules.Evaluate(w, ["CH06"], [result], false);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
        Assert.That(decision.Proof, Does.Contain("END_BARCODE_MISMATCH_OR_MISSING"));
    }
}
