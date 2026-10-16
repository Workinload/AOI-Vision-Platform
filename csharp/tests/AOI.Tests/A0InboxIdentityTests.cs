using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class A0InboxIdentityTests
{
    private string _root = string.Empty;
    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a0-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ProductionStore Store(string role) =>
        new(Path.Combine(_root, role, "production.db"));
    private static WorkpieceIdentity Item() =>
        WorkpieceIdentity.New("SAME_BARCODE", 7, "SIM_LIGHT_TEST_RELEASE");
    private static PeerEnvelope Register(WorkpieceIdentity item) =>
        PeerEnvelope.Create("master", "slave", 1, item, "workpiece_registered", item);

    [Test]
    public void IdenticalPayloadHashButMutatedEnvelopeMetadataNeverProducesDuplicateAck()
    {
        var item = Item();
        var slave = Store("slave");
        var packet = Register(item);
        Assert.That(slave.AcceptIncoming(packet, "slave"), Is.False);
        Assert.That(slave.AcceptIncoming(packet, "slave"), Is.True);
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(
            packet with { Sequence = packet.Sequence + 1 }, "slave"));
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(
            packet with { WorkpieceId = Guid.NewGuid() }, "slave"));
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(
            packet with { TraceId = Guid.NewGuid() }, "slave"));
        Assert.That(slave.LatestWorkpiece(), Is.EqualTo(item));
    }

    [Test]
    public void SlaveCannotClaimMasterOwnedCameraEvenWithValidPixelShaAndIdentity()
    {
        var item = Item();
        var master = Store("master");
        master.CreateAndQueueRegistration(item, Register(item));
        var invalid = SyntheticVision.Generate(item, "CH06", Path.Combine(_root, "assets"));
        var forged = PeerEnvelope.Create("slave", "master", 2, item,
            "channel_result", invalid, "CH06");
        Assert.Throws<InvalidDataException>(() => master.AcceptIncoming(forged, "master"));
        Assert.That(master.ResultsFor(item.WorkpieceId), Is.Empty);

        var legitimate = SyntheticVision.Generate(item, "CH02", Path.Combine(_root, "assets"));
        var correct = PeerEnvelope.Create("slave", "master", 3, item,
            "channel_result", legitimate, "CH02");
        Assert.That(master.AcceptIncoming(correct, "master"), Is.False);
        Assert.That(master.AcceptIncoming(correct, "master"), Is.True);
        Assert.That(master.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(1));
    }

    [Test]
    public void WrongPeerSenderCannotRegisterEvenWhenAllWorkpieceFieldsAreConsistent()
    {
        var item = Item();
        var slave = Store("slave");
        var invalid = PeerEnvelope.Create("slave", "slave", 1, item,
            "workpiece_registered", item);
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(invalid, "slave"));
        Assert.That(slave.LatestWorkpiece(), Is.Null);
    }

    [Test]
    public void LegacyInboxWithoutFullEnvelopeHashCannotCertifyRetryAsPersisted()
    {
        var item = Item();
        var slave = Store("slave");
        var packet = Register(item);
        Assert.That(slave.AcceptIncoming(packet, "slave"), Is.False);
        using (var db = new SqliteConnection($"Data Source={slave.DatabasePath};Pooling=False"))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE inbox SET envelope_hash=NULL WHERE message_id=$id";
            update.Parameters.AddWithValue("$id", packet.MessageId.ToString("D"));
            Assert.That(update.ExecuteNonQuery(), Is.EqualTo(1));
        }
        Assert.Throws<InvalidDataException>(() => slave.AcceptIncoming(packet, "slave"));
        Assert.That(slave.LatestWorkpiece(), Is.EqualTo(item));
    }
}
