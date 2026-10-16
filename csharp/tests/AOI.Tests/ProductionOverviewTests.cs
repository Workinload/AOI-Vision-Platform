using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class ProductionOverviewTests
{
    private string _path = string.Empty;
    [SetUp]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "aoi-live-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
    }
    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    [Test]
    public void SameBarcodeStillGeneratesSeparateReadOnlyHistoryAndCorrectCounters()
    {
        var store = new ProductionStore(Path.Combine(_path, "production.db"));
        var before = store.ReadProductionOverview();
        Assert.That(before.Total, Is.Zero);
        var first = WorkpieceIdentity.New("DUPLICATE", 1, "SIM_LIGHT_TEST_RELEASE");
        var second = WorkpieceIdentity.New("DUPLICATE", 1, "SIM_LIGHT_TEST_RELEASE");
        store.CreateAndQueueRegistration(first,
            PeerEnvelope.Create("master", "slave", 1, first, "workpiece_registered", first));
        store.CreateAndQueueRegistration(second,
            PeerEnvelope.Create("master", "slave", 2, second, "workpiece_registered", second));
        var frame = SyntheticVision.Generate(first, "CH06", Path.Combine(_path, "pixels"));
        store.SaveLocalResult(frame);
        var final = QualityRules.Evaluate(first, ["CH06"], [frame], true);
        Assert.That(final.Disposition, Is.EqualTo(FinalDisposition.Good));
        store.StoreFinal(first, final);
        var after = store.ReadProductionOverview(20);
        Assert.That(after.Total, Is.EqualTo(2));
        Assert.That(after.Good, Is.EqualTo(1));
        Assert.That(after.Reject, Is.Zero);
        Assert.That(after.Unfinished, Is.EqualTo(1));
        Assert.That(after.Recent, Has.Count.EqualTo(2));
        Assert.That(after.Recent.Select(x => x.Barcode).Distinct().Count(), Is.EqualTo(1));
        Assert.That(after.Recent.Select(x => x.WorkpieceId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(after.Recent.Single(x => x.WorkpieceId == first.WorkpieceId).CapturedChannels, Is.EqualTo(1));
        Assert.That(after.Recent.Single(x => x.WorkpieceId == second.WorkpieceId).Final, Is.Null);
        var snapshot = store.Snapshot("master", MachineRole.Master).Production!;
        Assert.That(snapshot.Total, Is.EqualTo(after.Total));
        Assert.That(snapshot.Recent.Select(x => x.WorkpieceId),
            Is.EquivalentTo(after.Recent.Select(x => x.WorkpieceId)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadProductionOverview(21));
    }

    [Test]
    public void ClientFacingHistoryRejectsCounterForgeryAndDuplicateIdentity()
    {
        var row = new WorkpieceProductionRow(Guid.NewGuid(), "SAME", 1, "SIM", 11,
            FinalDisposition.Good, DateTimeOffset.UtcNow);
        new ProductionOverview(2, 1, 0, 1, new[] { row }).Validate();
        Assert.Throws<InvalidDataException>(() =>
            new ProductionOverview(2, 2, 0, 1, new[] { row }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new ProductionOverview(2, 1, 0, 1, new[] { row, row }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            new ProductionOverview(1, 1, 0, 0,
                new[] { row with { CapturedChannels = 99 } }).Validate());
    }
}
