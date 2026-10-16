using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class LiveProductionHistoryProjectionTests
{
    private static MachineSnapshot Sample(ProductionOverview? overview) => new(
        "master", MachineRole.Master, "SIM_FULL",
        WorkpieceIdentity.New("X", 1, "SIM_LIGHT_TEST_RELEASE"),
        null, Array.Empty<ChannelSnapshot>(), DateTimeOffset.UtcNow,
        false, "SIM_FULL")
    {
        Production = overview
    };

    [Test]
    public void ReadOnlyHistoryPreservesSeparateWorkpieceIdsWithTheSameBarcode()
    {
        var a = new WorkpieceProductionRow(Guid.NewGuid(), "SAME", 1, "SIM", 11,
            FinalDisposition.Good, DateTimeOffset.UtcNow);
        var b = new WorkpieceProductionRow(Guid.NewGuid(), "SAME", 1, "SIM", 7,
            null, DateTimeOffset.UtcNow);
        var overview = new ProductionOverview(2, 1, 0, 1, new[] { a, b });
        var projected = MonitorProjection.FromSnapshot(Sample(overview), MachineRole.Master,
            DateTimeOffset.UtcNow);
        Assert.That(projected.Production, Is.Not.Null);
        Assert.That(projected.Production!.Recent.Select(r => r.WorkpieceId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(projected.Production.Recent.Select(r => r.Barcode).Distinct().Count(), Is.EqualTo(1));
        Assert.That(projected.Production.Unfinished, Is.EqualTo(1));
        Assert.That(projected.Channels, Has.Count.EqualTo(11));
    }

    [Test]
    public void RejectsUntrustworthyHistoryInsteadOfDisplayingFalseProductionTotals()
    {
        var row = new WorkpieceProductionRow(Guid.NewGuid(), "X", 1, "SIM", 0,
            FinalDisposition.Good, DateTimeOffset.UtcNow);
        var fake = new ProductionOverview(1, 1, 0, 0, new[] { row });
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(fake), MachineRole.Master, DateTimeOffset.UtcNow));
    }
}
