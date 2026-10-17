using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class RoiDraftUiProjectionTests
{
    private static MachineSnapshot Sample(params RoiDraftState[] rows) =>
        new("master", MachineRole.Master, "SIM_READY", null, null,
            Array.Empty<ChannelSnapshot>(), DateTimeOffset.UtcNow,
            false, "SIM_FULL") { RoiDrafts = rows };

    private static RoiDraftState Valid() =>
        new("CH06", .1, .15, .75, .7, 1, "sim_operator", DateTimeOffset.UtcNow);

    [Test]
    public void SavedLocalRoiIsVisibleButNotAppliedToPublishedProduct()
    {
        var projection = MonitorProjection.FromSnapshot(Sample(Valid()),
            MachineRole.Master, DateTimeOffset.UtcNow);
        Assert.That(projection.RoiDrafts, Is.Not.Null);
        Assert.That(projection.RoiDrafts!.Single().ChannelId, Is.EqualTo("CH06"));
        Assert.That(projection.RoiDrafts.Single().Revision, Is.EqualTo(1));
        Assert.That(projection.ActiveProduct, Is.Null);
    }

    [Test]
    public void RoiSnapshotsMustRejectForeignDuplicateAndInvalidCoordinates()
    {
        var saved = Valid();
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(saved, saved),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(saved with { ChannelId = "CH02" }),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(saved with { Width = .99 }),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Sample(saved with { Revision = 0 }),
                MachineRole.Master, DateTimeOffset.UtcNow));
    }
}
