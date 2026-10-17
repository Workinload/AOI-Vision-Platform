using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class LiveConveyorProjectionTests
{
    private static SimConveyorOverview Sample() => new(21, 5, 1,
        new[]
        {
            new SimConveyorItem(Guid.NewGuid(), 2, "SIM-DUP", "ST04", true, 5, 3, "AT_STATION", false),
            new SimConveyorItem(Guid.NewGuid(), 3, "SIM-DUP", "ST03", true, 3, 2, "AT_STATION", false)
        }, Array.Empty<string>());

    private static MachineSnapshot Snapshot(MachineRole role, SimConveyorOverview? conveyor) => new(
        role == MachineRole.Master ? "master" : "slave", role,
        "SIM_PROCESSING", null, null, Array.Empty<ChannelSnapshot>(),
        DateTimeOffset.UtcNow, false, "SIM_FULL") { Conveyor = conveyor };

    [Test]
    public void DisplaysDistinctPhysicalWorkpiecesAtDifferentSimStationsDespiteEqualBarcodes()
    {
        var projection = MonitorProjection.FromSnapshot(Snapshot(MachineRole.Master, Sample()),
            MachineRole.Master, DateTimeOffset.UtcNow);
        Assert.That(projection.Conveyor, Is.Not.Null);
        Assert.That(projection.Conveyor!.Active, Has.Count.EqualTo(2));
        Assert.That(projection.Conveyor.Active.Select(p => p.WorkpieceId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(projection.Conveyor.Active.Select(p => p.Barcode).Distinct().Count(), Is.EqualTo(1));
        Assert.That(projection.Conveyor.Active.Select(p => p.StationId), Is.EquivalentTo(new[] { "ST03", "ST04" }));
    }

    [Test]
    public void RejectsDuplicateTokensAndSlaveClaimingPhysicalMasterMotion()
    {
        var original = Sample();
        var duplicate = new SimConveyorOverview(original.Tick, original.LoadedCount,
            original.ExitedCount, new[] { original.Active[0], original.Active[1] with {
                Token = original.Active[0].Token
            } }, Array.Empty<string>());
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Snapshot(MachineRole.Master, duplicate),
                MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() =>
            MonitorProjection.FromSnapshot(Snapshot(MachineRole.Slave, original),
                MachineRole.Slave, DateTimeOffset.UtcNow));
    }
}
