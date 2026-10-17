using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OperatorWorkspaceTopologyTests
{
    [Test]
    public void MapReallyContainsAllElevenChannelsFromNinePhysicalDevices()
    {
        Assert.That(OperatorWorkspaceTopology.Stations.SelectMany(s => s.ChannelIds)
            .OrderBy(x => x), Is.EqualTo(ChannelCatalog.All.Select(c => c.ChannelId).OrderBy(x => x)));
        Assert.That(OperatorWorkspaceTopology.Stations.SelectMany(s => s.DeviceIds)
            .Distinct().Count(), Is.EqualTo(9));
        var st05 = OperatorWorkspaceTopology.Require("ST05");
        Assert.That(st05.ChannelIds, Is.EqualTo(new[] { "CH06" }));
        Assert.That(OperatorWorkspaceTopology.FramePreviewPort("CH06"), Is.EqualTo(19631));
        Assert.That(OperatorWorkspaceTopology.FramePreviewPort("CH02"), Is.EqualTo(19632));
    }

    [Test]
    public void SharedLineScanSignalMapsToBothActualChannels()
    {
        Assert.That(OperatorWorkspaceTopology.ResolveSignalChannels("D1013"),
            Is.EquivalentTo(new[] { "CH08", "CH09" }));
        Assert.That(OperatorWorkspaceTopology.ResolveSignalChannels("D1014"),
            Is.EquivalentTo(new[] { "CH10", "CH11" }));
    }

    [Test]
    public void NonCameraSignalUsesDeclaredStationWithoutInventingChannel()
    {
        var signal = new PlcEngineeringSignal("MOTOR", "D9999", "ST05",
            "master", "READ", "WORD", "Fixture test", "0=off", "UNKNOWN", "", false);
        Assert.That(OperatorWorkspaceTopology.ResolveSignalChannels(signal.DmAddress), Is.Empty);
        Assert.That(OperatorWorkspaceTopology.ResolveSignalStation(signal), Is.EqualTo("ST05"));
        Assert.That(OperatorWorkspaceTopology.ResolveSignalStation(signal with { StationId = "UNKNOWN" }), Is.Null);
        Assert.Throws<ArgumentException>(() => OperatorWorkspaceTopology.Require("ST99"));
    }
}
