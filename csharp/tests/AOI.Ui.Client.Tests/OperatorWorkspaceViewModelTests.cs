using AOI.Ui.Avalonia;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OperatorWorkspaceViewModelTests
{
    [Test]
    public void StationClickAndChannelClickStayLinkedAndGalleryIsRealViewModel()
    {
        var vm = new MainWindowViewModel("master", true);
        try
        {
            vm.FocusStationCommand.Execute("ST05");
            Assert.That(vm.SelectedMapStation, Is.EqualTo("ST05"));
            Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH06"));
            Assert.That(vm.MapStations.Single(x => x.Id == "ST05").IsFocused, Is.True);

            vm.SelectedChannel = vm.Channels.Single(x => x.ChannelId == "CH08");
            Assert.That(vm.SelectedMapStation, Is.EqualTo("ST09"));
            Assert.That(vm.MapStations.Single(x => x.Id == "ST09").IsFocused, Is.True);

            vm.NavigateWorkspaceCommand.Execute("2");
            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(2));
            Assert.That(vm.GalleryChannels.Count, Is.EqualTo(11));
            Assert.That(vm.GalleryChannels.All(x => x.Preview is null), Is.True,
                "UI must not invent images before verified Core IPC returns pixels.");

            vm.FollowLiveCommand.Execute(null);
            Assert.That(vm.ViewedWorkpieceLabel, Does.Contain("无可用工件"));

            // A map click must also select its exact PLC row, without forcing page navigation.
            var st05Signal = new PlcSignalTile("CAM_ST05", "D1011", "ST05",
                "MASTER", "PLC_TO_PC", "WORD", "ST05 camera", "UNKNOWN", "");
            var st03Signal = new PlcSignalTile("CAM_ST03", "D1008", "ST03",
                "SLAVE", "PLC_TO_PC", "WORD", "ST03 camera", "UNKNOWN", "");
            vm.PlcSignals.Add(st05Signal);
            vm.PlcSignals.Add(st03Signal);
            vm.FocusStationCommand.Execute("ST05");
            Assert.That(vm.SelectedPlcSignal, Is.SameAs(st05Signal));
            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(2));

            // A PLC row click must navigate back to the physical station and camera.
            vm.SelectedPlcSignal = st03Signal;
            Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH02"));
            Assert.That(vm.SelectedMapStation, Is.EqualTo("ST03"));
            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(1));
        }
        finally { vm.Stop(); }
    }

    [Test]
    public void UnknownMapStationAndInvalidNavigationCannotChangeOperatorSelection()
    {
        var vm = new MainWindowViewModel("master", true);
        try
        {
            vm.FocusStationCommand.Execute("ST04");
            Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH04"));
            vm.FocusStationCommand.Execute("ST99");
            Assert.That(vm.SelectedMapStation, Is.EqualTo("ST04"));
            vm.NavigateWorkspaceCommand.Execute("999");
            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(0));
            vm.NavigateWorkspaceCommand.Execute("anything");
            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(0));
        }
        finally { vm.Stop(); }
    }
}
