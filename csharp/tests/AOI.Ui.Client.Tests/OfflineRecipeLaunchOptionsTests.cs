using AOI.Solutions;
using AOI.Ui.Avalonia;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OfflineRecipeLaunchOptionsTests
{
    [Test]
    public void ExplicitEntryAndOperatorAreParsed()
    {
        var options = OfflineRecipeLaunchOptions.Parse(
            ["--master", "--headless", "--offline-recipe-management", @"D:\run-root", "--operator", "SIM_OPERATOR"]);
        Assert.That(options.Requested, Is.True);
        Assert.That(options.EntryPath, Is.EqualTo(@"D:\run-root"));
        Assert.That(options.OperatorId, Is.EqualTo("SIM_OPERATOR"));
        Assert.That(options.Error, Is.Null);
    }

    [Test]
    public void EqualsFormIsParsed()
    {
        var options = OfflineRecipeLaunchOptions.Parse(
            ["--offline-recipe-management=D:/a6/solutions.sqlite", "--operator=OP-A"]);
        Assert.That(options.Requested, Is.True);
        Assert.That(options.EntryPath, Is.EqualTo("D:/a6/solutions.sqlite"));
        Assert.That(options.OperatorId, Is.EqualTo("OP-A"));
        Assert.That(options.Error, Is.Null);
    }

    [Test]
    public void PartialEntryIsRejectedInsteadOfOpeningADatabase()
    {
        var missingOperator = OfflineRecipeLaunchOptions.Parse(
            ["--offline-recipe-management", "D:/a6/solutions.sqlite"]);
        Assert.That(missingOperator.Requested, Is.True);
        Assert.That(missingOperator.Error, Does.Contain("--operator"));

        var missingPath = OfflineRecipeLaunchOptions.Parse(
            ["--offline-recipe-management", "--operator", "OP"]);
        Assert.That(missingPath.Requested, Is.True);
        Assert.That(missingPath.Error, Does.Contain("路径"));

        var missingValue = OfflineRecipeLaunchOptions.Parse(
            ["--offline-recipe-management", "D:/a6/solutions.sqlite", "--operator"]);
        Assert.That(missingValue.Requested, Is.True);
        Assert.That(missingValue.Error, Does.Contain("--operator"));
    }

    [Test]
    public void UnrelatedLaunchArgumentsDoNotEnableOfflineManagement()
    {
        Assert.That(OfflineRecipeLaunchOptions.Parse(["--master", "--once", "--headless"]).Requested,
            Is.False);
        Assert.That(OfflineRecipeLaunchOptions.Parse([]).Requested, Is.False);
        Assert.That(OfflineRecipeLaunchOptions.Parse(null).Requested, Is.False);
    }
}

/// <summary>
/// The real MainWindow startup path: it selects the product/ROI page, attaches the explicit
/// OFFLINE entry and keeps A6 writes closed under the production gate.
/// </summary>
[TestFixture]
public sealed class OfflineRecipeLaunchWiringTests
{
    private string _root = "";
    private string _db = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "solutions.sqlite");
        var seed = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Launch Product");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void ProductionLaunchOpensProductPageReadOnlyAndBlocksWrites()
    {
        var vm = new MainWindowViewModel("master", true);
        vm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, _db, "SIM_OPERATOR_TEST", null));
        Assert.That(vm.SelectedWorkspaceIndex, Is.EqualTo(5));
        Assert.That(vm.OfflineProduct.IsOfflineMode, Is.True);
        Assert.That(vm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(1),
            "explicit offline launch shows the offline editor tab");
        Assert.That(vm.OfflineProduct.Solutions.Select(x => x.SolutionId), Does.Contain("S1"));
        Assert.That(vm.OfflineProduct.A6WritesAllowed, Is.False);
        Assert.That(vm.OfflineProduct.WriteState, Does.Contain("fail closed"));
        Assert.That(vm.OfflineProduct.ActivationState, Does.Contain("#149"));
    }

    [Test]
    public void MalformedLaunchNeverAttachesAnyDatabase()
    {
        var vm = new MainWindowViewModel("master", true);
        vm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, "", "",
            "OFFLINE_RECIPE_MANAGEMENT 需要显式 --operator <操作员>。"));
        Assert.That(vm.OfflineProduct.IsOfflineMode, Is.False);
        Assert.That(vm.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(1),
            "an explicit but malformed offline entry still shows its error page");
        Assert.That(vm.OfflineProduct.Status, Does.Contain("启动参数无效"));
        Assert.That(vm.OfflineProduct.Solutions, Is.Empty);
    }

    [Test]
    public void DefaultProductPageKeepsLegacyCoreRoiAndOfflineEntrySwitches()
    {
        var normal = new MainWindowViewModel("master", true);
        normal.ApplyOfflineLaunch(OfflineRecipeLaunchOptions.None);
        Assert.That(normal.SelectedWorkspaceIndex, Is.Zero);
        Assert.That(normal.OfflineProduct.ProductRoiTabIndex, Is.Zero,
            "ordinary --master/--slave startup must keep the legacy Core ROI tab");
        Assert.That(normal.OfflineProduct.IsLegacyCoreRoiTab, Is.True);
        Assert.That(normal.OfflineProduct.IsOfflineEditorTab, Is.False);

        normal.SelectedWorkspaceIndex = 5; // operator navigates to the product/ROI page
        Assert.That(normal.OfflineProduct.ProductRoiTabIndex, Is.Zero,
            "page navigation must not silently switch the inner tab");

        // Manual selection of the offline editor tab is two-way bound VM state.
        normal.OfflineProduct.ProductRoiTabIndex = 1;
        Assert.That(normal.OfflineProduct.IsOfflineEditorTab, Is.True);
        normal.OfflineProduct.ProductRoiTabIndex = 0;
        Assert.That(normal.OfflineProduct.IsLegacyCoreRoiTab, Is.True);

        // Explicitly enabling the offline entry selects the new editor page.
        var enabling = new MainWindowViewModel("master", true, OfflineWriteGate.IsolatedTestDatabase);
        enabling.OfflineProduct.EntryPath = _db;
        enabling.OfflineProduct.OperatorId = "SIM_OPERATOR_TEST";
        enabling.OfflineProduct.ProductRoiTabIndex = 0;
        enabling.OfflineProduct.AttachEntry();
        Assert.That(enabling.OfflineProduct.ProductRoiTabIndex, Is.EqualTo(1));
        Assert.That(enabling.OfflineProduct.IsOfflineMode, Is.True);
    }

    [Test]
    public void ChannelSelectionMovesThePreviewAndHostSelectionTogether()
    {
        var vm = new MainWindowViewModel("master", true, OfflineWriteGate.IsolatedTestDatabase);
        vm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, _db, "SIM_OPERATOR_TEST", null));
        var offlineChannel = vm.OfflineProduct.Channels.First(x => x.ChannelId == "CH04");
        vm.OfflineProduct.SelectedChannel = offlineChannel;
        Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo("CH04"));

        var hostChannel = vm.Channels.First(x => x.ChannelId == "CH06");
        vm.SelectedChannel = hostChannel;
        Assert.That(vm.OfflineProduct.SelectedChannel?.ChannelId, Is.EqualTo("CH06"));
        Assert.That(vm.OfflineProduct.SelectedChannelId, Is.EqualTo("CH06"));
    }

    [Test]
    public void CreatedProductStatusUsesTheRealSevenStationTopology()
    {
        var vm = new MainWindowViewModel("master", true, OfflineWriteGate.IsolatedTestDatabase);
        vm.ApplyOfflineLaunch(new OfflineRecipeLaunchOptions(true, _db, "SIM_OPERATOR_TEST", null));
        vm.OfflineProduct.NewSolutionId = "S7";
        vm.OfflineProduct.NewSolutionName = "Seven Station Product";
        vm.OfflineProduct.CreateProduct();
        Assert.That(vm.OfflineProduct.Status, Does.Contain("7 工位"));
        Assert.That(vm.OfflineProduct.Status, Does.Contain("9 物理设备"));
        Assert.That(vm.OfflineProduct.Status, Does.Contain("11 逻辑通道"));
        Assert.That(vm.OfflineProduct.Status, Does.Not.Contain("9 工位"));
    }
}