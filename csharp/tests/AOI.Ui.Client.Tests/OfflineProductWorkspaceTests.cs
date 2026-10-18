using AOI.Solutions;
using AOI.Ui.Avalonia;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OfflineProductWorkspaceTests
{
    private string db = string.Empty;
    private OfflineProductWorkspaceViewModel vm = null!;

    [SetUp]
    public void SetUp()
    {
        db = Path.Combine(Path.GetTempPath(), "aoi-offvm-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var seed = OfflineProductManager.Open(db, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Offline Workspace Product");
        vm = new OfflineProductWorkspaceViewModel();
        vm.Attach(db, "SIM_OPERATOR_TEST");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(db)) File.Delete(db);
    }

    [Test]
    public void ShowsNinePhysicalDevicesAndElevenLogicalChannels()
    {
        Assert.That(vm.ChannelCount, Is.EqualTo(11));
        Assert.That(vm.PhysicalDeviceCount, Is.EqualTo(9));
        Assert.That(vm.Channels.Select(c => c.ChannelId).Distinct().Count(), Is.EqualTo(11));
    }

    [Test]
    public void RefreshListsRealA6SolutionsNotInventedOnes()
    {
        Assert.That(vm.Solutions, Does.Contain("S1"));
        Assert.That(vm.ActiveSolutionId, Is.EqualTo("S1"));
    }

    [Test]
    public void LoadChannelReadsActualDraftRevisionAndTunables()
    {
        vm.SelectedChannelId = "CH02";
        vm.LoadChannel();
        Assert.That(vm.StationId, Is.Not.EqualTo("-"));
        Assert.That(vm.BrightThreshold, Is.EqualTo(127));
        Assert.That(vm.Status, Does.Contain("草稿"));
    }

    [Test]
    public void NonFiniteThresholdIsRejectedByValidateAndNeverSaved()
    {
        vm.SelectedChannelId = "CH02";
        vm.LoadChannel();
        var before = vm.StationRevision;
        vm.BrightThreshold = double.NaN;
        vm.Validate();
        Assert.That(vm.Status, Does.Contain("校验失败"));
        Assert.That(vm.StationRevision, Is.EqualTo(before));
    }

    [Test]
    public void PublishReturnsRealReleaseIdAndSha()
    {
        vm.SelectedChannelId = "CH02";
        vm.LoadChannel();
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"));
        Assert.That(vm.PublishedSha, Has.Length.EqualTo(64));
        Assert.That(vm.Status, Does.Contain("已发布"));
    }

    [Test]
    public void StaleRevisionSaveIsRejectedAndStatusExplains()
    {
        vm.SelectedChannelId = "CH02";
        vm.LoadChannel();
        var saved = new OfflineProductManager(new SolutionRepository(db), "OTHER")
            .Repository.GetDraft("S1", vm.StationId)!;
        new OfflineProductManager(new SolutionRepository(db), "OTHER")
            .SaveStation("S1", saved.Recipe, saved.Revision);
        vm.BrightThreshold = 100;
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("拒绝"));
    }

    [Test]
    public void DetachedWorkspaceRefusesEveryWrite()
    {
        var cold = new OfflineProductWorkspaceViewModel();
        cold.LoadChannel();
        Assert.That(cold.Status, Does.Contain("未启用"));
        cold.Publish();
        Assert.That(cold.PublishedRelease, Is.EqualTo("未发布"));
    }
}
