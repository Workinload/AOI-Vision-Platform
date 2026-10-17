using AOI.Ui.Avalonia;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class CaptureDebugViewModelTests
{
    [Test]
    public void OperatorCanPickExactWorkpieceIdWithoutNavigatingAwayFromDebugger()
    {
        var vm = new MainWindowViewModel("master", true);
        try
        {
            vm.NavigateWorkspaceCommand.Execute("4");
            var first = new WorkpieceHistoryTile(Guid.NewGuid(), "AAAA0001",
                "REPEAT", "Epoch 1", "5/11", "WAIT", "12:01:00");
            var second = new WorkpieceHistoryTile(Guid.NewGuid(), "BBBB0002",
                "REPEAT", "Epoch 2", "8/11", "WAIT", "12:01:01");
            vm.RecentWorkpieces.Add(first);
            vm.RecentWorkpieces.Add(second);
            vm.CaptureDebugPickedWorkpiece = second;

            Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(4));
            Assert.That(vm.SelectedHistoryItem?.WorkpieceId, Is.EqualTo(second.WorkpieceId));
            Assert.That(vm.ViewedWorkpieceLabel, Does.Contain(second.WorkpieceId.ToString("D")));
            Assert.That(vm.CaptureDebugChannels, Has.Count.EqualTo(11));
            Assert.That(vm.CaptureDebugChannels.Single(x => x.ChannelId == "CH06")
                .InspectionResult, Does.Contain("历史逐通道"));
            Assert.That(vm.CaptureDebugChannels.Single(x => x.ChannelId == "CH06")
                .CamDoneEvidence, Does.Contain(CaptureDebugProjection.NotReported));

            vm.FollowLiveCommand.Execute(null);
            Assert.That(vm.SelectedHistoryItem, Is.Null);
            Assert.That(vm.CaptureDebugPickedWorkpiece, Is.Null);
        }
        finally { vm.Stop(); }
    }
}
