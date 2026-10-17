using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class DurableWorkpieceHoldTests
{
    private string _dir = string.Empty;
    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aoi-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Test]
    public void HoldIsDurableIdempotentAndCannotLaterBecomeGoodOrNg()
    {
        var path = Path.Combine(_dir, "production.db");
        var store = new ProductionStore(path);
        var first = WorkpieceIdentity.New("SAME_BARCODE", 1, "SIM_LIGHT_TEST_RELEASE");
        var second = WorkpieceIdentity.New("SAME_BARCODE", 1, "SIM_LIGHT_TEST_RELEASE");
        store.CreateAndQueueRegistration(first,
            PeerEnvelope.Create("master", "slave", 1, first, "workpiece_registered", first));
        store.CreateAndQueueRegistration(second,
            PeerEnvelope.Create("master", "slave", 2, second, "workpiece_registered", second));
        Assert.That(store.RecordWorkpieceHold(first, "END_BARCODE_MISMATCH",
            "SIM_FULL virtual end barcode differs"), Is.True);
        Assert.That(store.RecordWorkpieceHold(first, "END_BARCODE_MISMATCH",
            "SIM_FULL virtual end barcode differs"), Is.False);
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordWorkpieceHold(first, "UNKNOWN_SOURCE", "Inconsistent reason"));
        Assert.Throws<InvalidDataException>(() =>
            store.RecordWorkpieceHold(second with { TraceId = Guid.NewGuid() },
                "UNKNOWN_SOURCE", "Forged lineage"));

        var fake = new FinalDecision(FinalDisposition.Good, true,
            new[] { "CH02" }, new[] { "CH02" }, "COMPLETE_OK:SIM_ONLY");
        Assert.Throws<InvalidOperationException>(() => store.StoreFinal(first, fake));
        var hold = store.ReadWorkpieceHolds();
        hold.Validate();
        Assert.That(hold.ActiveCount, Is.EqualTo(1));
        Assert.That(hold.Recent.Single().WorkpieceId, Is.EqualTo(first.WorkpieceId));
        Assert.That(hold.Recent.Single().ReasonCode, Is.EqualTo("END_BARCODE_MISMATCH"));
        Assert.That(store.LatestFinal(first.WorkpieceId), Is.Null);
        Assert.That(store.Snapshot("master", MachineRole.Master).Holds!.ActiveCount, Is.EqualTo(1));
        store = new ProductionStore(path);
        Assert.That(store.ReadWorkpieceHolds().ActiveCount, Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => store.StoreFinal(first, fake));

        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM flight_events WHERE kind='WORKPIECE_HOLD'";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.EqualTo(1));
    }

    [Test]
    public void RejectsUnsafeReasonsAndForbidsRetrospectiveHoldOfFinal()
    {
        var store = new ProductionStore(Path.Combine(_dir, "prod.db"));
        var item = WorkpieceIdentity.New("SIM-0001", 1, "SIM_LIGHT_TEST_RELEASE");
        store.CreateAndQueueRegistration(item,
            PeerEnvelope.Create("master", "slave", 1, item, "workpiece_registered", item));
        Assert.Throws<ArgumentException>(() => store.RecordWorkpieceHold(item, "../BAD", "unsafe"));
        Assert.Throws<ArgumentException>(() => store.RecordWorkpieceHold(item, "BAD", ""));
        Assert.Throws<ArgumentException>(() => store.RecordWorkpieceHold(item, "BAD", new string('A', 513)));
        Assert.That(store.ReadWorkpieceHolds().ActiveCount, Is.Zero);
        var final = new FinalDecision(FinalDisposition.Good, true,
            new[] { "CH06" }, new[] { "CH06" }, "COMPLETE_OK:SIM_ONLY");
        store.StoreFinal(item, final);
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordWorkpieceHold(item, "LATE_ALERT", "Cannot hold completed output"));
        Assert.That(store.ReadWorkpieceHolds().ActiveCount, Is.Zero);
    }
}
