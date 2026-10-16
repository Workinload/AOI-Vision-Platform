using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class MonitorProjectionTests
{
    private static MachineSnapshot Sample() => new("master", MachineRole.Master, "SIM_PROCESSING",
        WorkpieceIdentity.New("SIM-1", 1, "SIM_RELEASE"), null,
        [new ChannelSnapshot("CH06", "ST05", "HIK-M-2", "SIM_OK", null, true, DateTimeOffset.UtcNow)],
        DateTimeOffset.UtcNow, false, "SIM_LIGHT");

    [Test] public void MissingChannelsStayExplicitlyUnknown()
    {
        var result = MonitorProjection.FromSnapshot(Sample(), MachineRole.Master, DateTimeOffset.UtcNow);
        Assert.That(result.Channels, Has.Count.EqualTo(11));
        Assert.That(result.Channels.Single(x => x.Definition.ChannelId == "CH01").Status, Is.EqualTo("NO_DATA"));
    }

    [Test] public void RejectsRealWritesAndWrongRole()
    {
        var sample = Sample();
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            sample with { RealHardwareWritesEnabled = true }, MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            sample, MachineRole.Slave, DateTimeOffset.UtcNow));
    }

    [Test] public void RejectsStaleAndMisbinding()
    {
        var sample = Sample();
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            sample with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
            MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            sample with { Channels = [new ChannelSnapshot("CH06", "WRONG", "HIK-M-2", "SIM_OK",
                null, true, DateTimeOffset.UtcNow)] }, MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            sample with { Channels = [new ChannelSnapshot("CH06", "ST05", "HIK-M-2", "SIM_OK",
                Guid.NewGuid(), true, DateTimeOffset.UtcNow)] }, MachineRole.Master, DateTimeOffset.UtcNow));
    }

    [Test] public void RoiIsLocalAndValidated()
    {
        new RoiDraft("CH06", 0.1, 0.1, 0.4, 0.4).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RoiDraft("CH06", 0.9, 0.1, 0.2, 0.4).Validate());
    }
}
