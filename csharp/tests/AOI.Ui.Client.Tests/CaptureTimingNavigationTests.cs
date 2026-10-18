using AOI.Ui.Avalonia;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class CaptureTimingNavigationTests
{
    [Test]
    public void SelectingActualChannelEventFocusesCameraButParentLineFrameDoesNotGuessChild()
    {
        var vm = new MainWindowViewModel("master", true);
        try
        {
            vm.SelectedCaptureTimingEvent = new OperatorTimingTile(
                "12:30:00.000 UTC", "master", "FrameReady", "DIRECT_OBSERVATION",
                "CH06", "CH06 · HIK-M-2", "D1011", "Capture ABCD", "PIXELS_VERIFIED");
            Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH06"));
            Assert.That(vm.SelectedMapStation, Is.EqualTo("ST05"));

            vm.SelectedCaptureTimingEvent = new OperatorTimingTile(
                "12:30:00.002 UTC", "master", "FrameArrived", "DIRECT_OBSERVATION",
                null, "物理事件 · LINE-M-2", "D1014", "Parent FRAME", "FRAME_ARRIVED");
            Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH06"),
                "A shared physical line frame must not guess between CH10 and CH11.");
        }
        finally { vm.Stop(); }
    }
}
