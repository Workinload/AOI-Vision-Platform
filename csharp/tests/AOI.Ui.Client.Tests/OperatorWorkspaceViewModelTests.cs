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

            vm.FollowLiveCommand.Execute();
            Assert.That(vm.ViewedWorkpieceLabel, Does.Contain("无可用工件"));
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
