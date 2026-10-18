using AOI.Domain;
using AOI.Solutions;
using AOI.Ui.Avalonia;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

/// <summary>
/// OFFLINE_RECIPE_MANAGEMENT operator workspace. These tests use an isolated temporary
/// A6 database only; the production write gate is separately proven to stay fail-closed.
/// No test connects a Core, PLC, camera or activation path.
/// </summary>
[TestFixture]
public sealed class OfflineProductWorkspaceTests
{
    private string _root = "";
    private string _db = "";
    private OfflineProductWorkspaceViewModel vm = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-offvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "solutions.sqlite");
        var seed = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Offline Workspace Product");
        seed.CreateSimSolution("S2", "Second Product");
        vm = NewIsolatedVm();
        vm.Attach(_db, "SIM_OPERATOR_TEST");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static OfflineProductWorkspaceViewModel NewIsolatedVm() =>
        new(OfflineWriteGate.IsolatedTestDatabase);

    private void SelectChannel(string channelId)
    {
        vm.SelectedChannelId = channelId;
        Assert.That(vm.SelectedChannel?.ChannelId, Is.EqualTo(channelId));
    }

    private StationRecipe PersistedRecipe(string stationId, string solutionId = "S1") =>
        OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").Repository
            .GetDraft(solutionId, stationId)!.Recipe;

    [Test]
    public void ChannelsComeFromRealTopologyWithActualCoreOwnership()
    {
        Assert.That(vm.ChannelCount, Is.EqualTo(11));
        Assert.That(vm.PhysicalDeviceCount, Is.EqualTo(9));
        Assert.That(vm.Channels.Select(c => c.ChannelId).Distinct(StringComparer.Ordinal).Count(),
            Is.EqualTo(11));
        foreach (var tile in vm.Channels)
        {
            var definition = ChannelCatalog.Require(tile.ChannelId);
            Assert.That(tile.OwnerRole, Is.EqualTo(definition.OwnerRole), tile.ChannelId);
            Assert.That(tile.Owner, Is.EqualTo(definition.OwnerRole == MachineRole.Master
                ? "MASTER" : "SLAVE"), tile.ChannelId);
            Assert.That(tile.StationId, Is.EqualTo(definition.StationId));
            Assert.That(tile.PhysicalDeviceId, Is.EqualTo(definition.PhysicalDeviceId));
        }
        Assert.That(vm.Channels.Count(c => c.OwnerRole == MachineRole.Master), Is.EqualTo(5));
        Assert.That(vm.Channels.Count(c => c.OwnerRole == MachineRole.Slave), Is.EqualTo(6));
        Assert.That(vm.Channels.Select(c => c.Owner), Has.None.EqualTo("MASTER/SLAVE"));
    }

    [Test]
    public void RefreshListsRealA6ProductsNotInventedOnes()
    {
        Assert.That(vm.IsOfflineMode, Is.True);
        Assert.That(vm.Solutions.Select(x => x.SolutionId),
            Is.EquivalentTo(new[] { "S1", "S2" }));
        Assert.That(vm.ActiveSolutionId, Is.EqualTo("S1"));
        Assert.That(vm.Status, Does.Contain("真实 A6").Or.Contain("只读"));
    }

    [Test]
    public void LoadChannelReadsActualDraftRevisionAndTunables()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        Assert.That(vm.StationId, Is.EqualTo("ST03"));
        Assert.That(vm.StationRevision, Is.EqualTo(1));
        Assert.That(vm.BrightThreshold, Is.EqualTo(127));
        Assert.That(vm.MinimumBrightFraction, Is.EqualTo(0.1));
        Assert.That(vm.Status, Does.Contain("草稿"));
        Assert.That(vm.IsDirty, Is.False);
    }

    [Test]
    public void NonFiniteThresholdIsRejectedByValidateAndNeverSaved()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        var before = vm.StationRevision;
        vm.BrightThreshold = double.NaN;
        vm.Validate();
        Assert.That(vm.Status, Does.Contain("校验失败"));
        Assert.That(vm.StationRevision, Is.EqualTo(before));
    }

    [Test]
    public void PublishNeedsExplicitConfirmationThenReadsRealReleaseIdAndFullSha()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.Publish();
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.PublishState, Does.Contain("确认"));
        Assert.That(OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").ListReleases(), Is.Empty);

        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"), vm.Status);
        Assert.That(vm.PublishedSha, Has.Length.EqualTo(64));
        Assert.That(vm.PublishState, Does.Contain("数据库回读"));
        Assert.That(vm.ReleaseProvenance, Does.Contain("SQLite"));
        Assert.That(vm.PublishedSha, Does.Match("^[0-9a-f]{64}$"));
        var releases = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").ListReleases();
        Assert.That(releases, Has.Count.EqualTo(1));
        Assert.That(releases[0].ReleaseId, Is.EqualTo(vm.PublishedRelease));
        Assert.That(releases[0].ContentSha256, Is.EqualTo(vm.PublishedSha));
        // Publication is NOT activation: activation never becomes available here.
        Assert.That(vm.ActivationState, Does.Contain("不可激活"));
        Assert.That(vm.ActivationState, Does.Contain("双 Core 独占锁"));
        Assert.That(vm.PublishConfirmed, Is.False);
    }

    [Test]
    public void StaleRevisionSaveIsRejectedAndNoWriteHappens()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        var external = OfflineProductManager.Open(_db, "OTHER");
        var saved = external.Repository.GetDraft("S1", vm.StationId)!;
        external.SaveStation("S1", saved.Recipe, saved.Revision);
        vm.BrightThreshold = 100;
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("冲突"));
        Assert.That(PersistedRecipe("ST03").Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(external.Repository.GetDraft("S1", "ST03")!.Revision, Is.EqualTo(2));
    }

    [Test]
    public void DetachedWorkspaceRefusesEveryWrite()
    {
        var cold = new OfflineProductWorkspaceViewModel();
        cold.LoadChannel();
        Assert.That(cold.Status, Does.Contain("未启用"));
        cold.PublishConfirmed = true;
        cold.Publish();
        Assert.That(cold.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(cold.Status, Does.Contain("fail closed"));
        cold.SaveDraft();
        Assert.That(cold.Status, Does.Contain("fail closed"));
    }

    [Test]
    public void ProductionGateKeepsA6WritesFailClosedWhileReadsStillWork()
    {
        var production = new OfflineProductWorkspaceViewModel();
        production.Attach(_db, "SIM_OPERATOR_TEST");
        Assert.That(production.IsOfflineMode, Is.True);
        Assert.That(production.Solutions.Select(x => x.SolutionId), Does.Contain("S1"));
        Assert.That(production.WriteState, Does.Contain("fail closed"));

        production.SelectedChannelId = "CH02";
        production.LoadChannel();
        production.BrightThreshold = 200;
        production.Validate();
        Assert.That(production.ValidationMessage, Does.Contain("校验通过"));

        production.SaveDraft();
        Assert.That(production.Status, Does.Contain("fail closed"));
        Assert.That(PersistedRecipe("ST03").Tunables["brightThreshold"], Is.EqualTo(127));

        production.PublishConfirmed = true;
        production.Publish();
        Assert.That(production.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(production.Status, Does.Contain("fail closed"));
        Assert.That(OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").ListReleases(), Is.Empty);

        production.CreateProduct();
        Assert.That(production.Status, Does.Contain("fail closed"));
        Assert.That(production.Solutions, Has.Count.EqualTo(2));
    }

    [Test]
    public void SwitchingProductDiscardsDirtyDraftAndCannotLeakToNewProduct()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.BrightThreshold = 200;
        Assert.That(vm.IsDirty, Is.True);
        var second = vm.Solutions.Single(x => x.SolutionId == "S2");
        vm.SelectedSolution = second;
        Assert.That(vm.IsDirty, Is.False);
        Assert.That(vm.Status, Does.Contain("不会写入"));
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("请先读取"));

        Assert.That(PersistedRecipe("ST03", "S1").Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(PersistedRecipe("ST03", "S2").Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(OfflineProductManager.Open(_db, "TEST")
            .Repository.GetDraft("S2", "ST03")!.Revision, Is.EqualTo(1));
    }

    [Test]
    public void SwitchingChannelDiscardsDirtyDraftAndCannotLeakToNewChannel()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.BrightThreshold = 201;
        SelectChannel("CH04");
        Assert.That(vm.IsDirty, Is.False);
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("请先读取"));
        Assert.That(PersistedRecipe("ST03").Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(PersistedRecipe("ST04").Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(vm.StationRevision, Is.Zero);
    }

    [Test]
    public void RoiRequiresVerifiedRealImageAndPersistsPixelBounds()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        var before = vm.StationRevision;
        vm.RoiX = "0.1";
        vm.RoiY = "0.2";
        vm.RoiWidth = "0.5";
        vm.RoiHeight = "0.6";
        vm.Validate();
        Assert.That(vm.Status, Does.Contain("拒绝").Or.Contain("校验失败"));
        Assert.That(vm.RoiImageState, Does.Contain("fail closed"));
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("ROI").Or.Contain("拒绝"));
        Assert.That(vm.StationRevision, Is.EqualTo(before));
        Assert.That(RoiReleaseWorkflow.ReadStationRoi(PersistedRecipe("ST03"), "CH02"), Is.Null);

        vm.ReportVerifiedImage("CH02", 64, 64, "SIM fixture verified PNG");
        Assert.That(vm.HasVerifiedImage, Is.True);
        Assert.That(vm.RoiPixelPreviewText, Does.Contain("X=6"));
        vm.SaveDraft();
        Assert.That(vm.StationRevision, Is.EqualTo(before + 1), vm.Status);
        var saved = RoiReleaseWorkflow.ReadStationRoi(PersistedRecipe("ST03"), "CH02");
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Bounds, Is.EqualTo(new NormalizedInspectionRoi(0.1, 0.2, 0.5, 0.6)));
        Assert.That(saved.Bounds.ToPixels(64, 64), Is.EqualTo(new PixelInspectionRoi(6, 12, 33, 40)));

        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"), vm.Status);
        Assert.That(vm.PublishedSha, Has.Length.EqualTo(64));
    }

    [Test]
    public void InvalidRoiIsRejectedEvenWithVerifiedImage()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.ReportVerifiedImage("CH02", 64, 64, "SIM fixture verified PNG");
        vm.RoiX = "0.9";
        vm.RoiY = "0.1";
        vm.RoiWidth = "0.3";
        vm.RoiHeight = "0.2";
        vm.Validate();
        Assert.That(vm.Status, Does.Contain("ROI 无效"));
        var before = vm.StationRevision;
        vm.SaveDraft();
        Assert.That(vm.StationRevision, Is.EqualTo(before));
        Assert.That(vm.Status, Does.Contain("拒绝").Or.Contain("校验失败"));
    }

    [Test]
    public void DirtyOrRejectedPublishNeverShowsFakeSuccess()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.BrightThreshold = 200;
        vm.Publish();
        Assert.That(vm.PublishState, Does.Contain("未保存"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));

        vm.SaveDraft();
        // A legacy/imported draft with an uninstalled plugin bypasses the operator editor.
        var repository = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").Repository;
        var draft = repository.GetDraft("S1", "LOADING")!;
        repository.SaveDraft("S1", draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH01", "custom.not-installed.v1",
                    new Dictionary<string, double>(), new Dictionary<string, string>())
            ]
        }, draft.Revision, "legacy-import");

        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishState, Does.Contain("拒绝"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.PublishedSha, Is.Empty);
        Assert.That(OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").ListReleases(), Is.Empty);
    }

    [Test]
    public void ReopenFromDatabaseShowsSameReleaseIdAndFullShaAcrossInstances()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.Publish();
        string releaseId = vm.PublishedRelease;
        string sha = vm.PublishedSha;
        Assert.That(releaseId, Does.StartWith("SIMREL-"));

        var reopenedVm = NewIsolatedVm();
        reopenedVm.Attach(_db, "SIM_OPERATOR_TEST");
        reopenedVm.ReopenPersistedRelease();
        Assert.That(reopenedVm.PublishedRelease, Is.EqualTo(releaseId));
        Assert.That(reopenedVm.PublishedSha, Is.EqualTo(sha));
        Assert.That(reopenedVm.PublishedSha, Has.Length.EqualTo(64));
        Assert.That(reopenedVm.ReleaseProvenance, Does.Contain("重新打开回读"));
        Assert.That(reopenedVm.ActivationState, Does.Contain("不可激活"));
    }

    [Test]
    public void DraftChangedAfterOperatorConfirmationRejectsPublishWithNoNewRelease()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("已读取并冻结"));
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("7 工位"));
        vm.PublishConfirmed = true;
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("已确认绑定"));

        // A different connection changes another station's draft after the operator
        // confirmed the whole-product revision snapshot.
        var external = OfflineProductManager.Open(_db, "CONCURRENT");
        var draft = external.Repository.GetDraft("S1", "ST04")!;
        external.SaveStation("S1", draft.Recipe with
        {
            Tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
            {
                ["brightThreshold"] = 200
            }
        }, draft.Revision);

        vm.Publish();
        Assert.That(vm.PublishState, Does.Contain("拒绝"));
        Assert.That(vm.Status, Does.Contain("DRAFT_REVISION_CONFLICT"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(external.ListReleases(), Is.Empty, "CAS rejection must not create a Release");
        Assert.That(vm.PublishConfirmed, Is.False, "a rejected publish invalidates confirmation");
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("尚未确认"));
    }

    [Test]
    public void ProductionAttachIsReadOnlyAndLeavesOriginalDatabaseBytesUntouched()
    {
        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(_db);
        var production = new OfflineProductWorkspaceViewModel();
        production.Attach(_db, "SIM_OPERATOR_TEST");
        Assert.That(production.IsOfflineMode, Is.True);
        Assert.That(production.Solutions.Select(x => x.SolutionId), Does.Contain("S1"));
        production.SelectedChannelId = "CH02";
        production.LoadChannel();
        Assert.That(production.StationId, Is.EqualTo("ST03"));
        production.ReopenPersistedRelease();
        Assert.That(production.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(File.ReadAllBytes(_db), Is.EqualTo(before),
            "read-only attach/readback must not rewrite (or repair) the A6 database");
    }

    [Test]
    public void CorruptDatabaseFailsClosedWithoutRepairOrRecreation()
    {
        string corrupt = Path.Combine(_root, "corrupt-solutions.sqlite");
        File.WriteAllText(corrupt, "this is not an A6 solutions database");
        byte[] before = File.ReadAllBytes(corrupt);
        var production = new OfflineProductWorkspaceViewModel();
        production.Attach(corrupt, "SIM_OPERATOR_TEST");
        Assert.That(production.IsOfflineMode, Is.False);
        Assert.That(production.Status, Does.Contain("失败"));
        Assert.That(File.ReadAllBytes(corrupt), Is.EqualTo(before),
            "a damaged store must be reported, never repaired or replaced");
    }

    [Test]
    public void PublishedReadbackIsClearedWhenAnotherProductIsSelected()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"), vm.Status);
        Assert.That(vm.PublishedSha, Has.Length.EqualTo(64));

        vm.SelectedSolution = vm.Solutions.Single(x => x.SolutionId == "S2");
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.PublishedSha, Is.Empty);
        Assert.That(vm.PublishState, Is.EqualTo("未发布"));
        Assert.That(vm.ReleaseProvenance, Does.Contain("失效"));
        Assert.That(vm.PublishConfirmed, Is.False);
    }

    [Test]
    public void ReattachingAnotherDatabaseWithSameProductIdClearsPreviousEvidence()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"));

        string otherRoot = Path.Combine(_root, "other-db");
        Directory.CreateDirectory(otherRoot);
        string otherDb = Path.Combine(otherRoot, "solutions.sqlite");
        var seed = OfflineProductManager.Open(otherDb, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Other Database Product");

        vm.Attach(otherDb, "SIM_OPERATOR_TEST");
        Assert.That(vm.IsOfflineMode, Is.True);
        Assert.That(vm.Solutions.Select(x => x.SolutionId), Does.Contain("S1"));
        Assert.That(vm.StationId, Is.EqualTo("-"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.PublishedSha, Is.Empty);
        Assert.That(vm.PublishState, Is.EqualTo("未发布"));
        vm.ReopenPersistedRelease();
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.ReleaseProvenance, Does.Contain("没有匹配"));
    }

    [Test]
    public void FailedAttachDoesNotLeavePreviousProductsAsNewDatabaseData()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"));

        vm.Attach(Path.Combine(_root, "does-not-exist"), "SIM_OPERATOR_TEST");
        Assert.That(vm.IsOfflineMode, Is.False);
        Assert.That(vm.Solutions, Is.Empty);
        Assert.That(vm.SelectedSolution, Is.Null);
        Assert.That(vm.StationId, Is.EqualTo("-"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(vm.PublishedSha, Is.Empty);
        Assert.That(vm.Status, Does.Contain("失败"));
    }

    [Test]
    public void ConcurrentChangeBeforeConfirmationStillBindsFrozenSnapshotAndRejectsPublish()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("ST04=r1"));

        var external = OfflineProductManager.Open(_db, "CONCURRENT");
        var draft = external.Repository.GetDraft("S1", "ST04")!;
        external.SaveStation("S1", draft.Recipe with
        {
            Tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
            {
                ["brightThreshold"] = 201
            }
        }, draft.Revision);

        vm.PublishConfirmed = true;
        // Confirmation must bind the frozen r1 view, not silently adopt the external r2.
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("ST04=r1"));
        Assert.That(vm.RevisionsSnapshotLabel, Does.Not.Contain("ST04=r2"));
        vm.Publish();
        Assert.That(vm.PublishState, Does.Contain("拒绝"));
        Assert.That(vm.Status, Does.Contain("DRAFT_REVISION_CONFLICT"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(external.ListReleases(), Is.Empty);
        Assert.That(vm.PublishConfirmed, Is.False);
    }

    [Test]
    public void RefreshDoesNotSilentlyAdoptNewerRevisionsIntoAConfirmation()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        var external = OfflineProductManager.Open(_db, "CONCURRENT");
        var draft = external.Repository.GetDraft("S1", "ST04")!;
        external.SaveStation("S1", draft.Recipe with
        {
            Tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
            {
                ["brightThreshold"] = 202
            }
        }, draft.Revision);

        vm.Refresh();
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("尚未确认"));
        Assert.That(vm.RevisionsSnapshotLabel, Does.Contain("数据库已重新读取"));
        Assert.That(vm.PublishConfirmed, Is.False);

        vm.PublishConfirmed = true;
        Assert.That(vm.PublishConfirmed, Is.False, "confirmation without a frozen read is refused");
        Assert.That(vm.Status, Does.Contain("确认被拒绝"));
        Assert.That(external.ListReleases(), Is.Empty);
    }

    [Test]
    public void InstalledPluginValidationInsidePublishTransactionWritesNoRelease()
    {
        SelectChannel("CH02");
        vm.LoadChannel();
        var repository = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").Repository;
        var loading = repository.GetDraft("S1", "LOADING")!;
        repository.SaveDraft("S1", loading.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH01", "custom.not-installed.v1",
                    new Dictionary<string, double>(), new Dictionary<string, string>())
            ]
        }, loading.Revision, "legacy-import");
        // Re-read the channel so the frozen snapshot includes the imported revision.
        vm.LoadChannel();
        vm.PublishConfirmed = true;
        vm.Publish();
        Assert.That(vm.PublishState, Does.Contain("拒绝"));
        Assert.That(vm.Status, Does.Contain("INSPECTION_PLUGIN_NOT_INSTALLED"));
        Assert.That(vm.PublishedRelease, Is.EqualTo("未发布"));
        Assert.That(OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST").ListReleases(), Is.Empty);
    }

    [Test]
    public void CreateProductRejectsInvalidInputsBeforeAnyWrite()
    {
        vm.NewSolutionId = "BAD ID";
        vm.NewSolutionName = "Invalid";
        vm.CreateProduct();
        Assert.That(vm.Status, Does.Contain("创建产品失败"));
        Assert.That(vm.Status, Does.Contain("未写入"));
        Assert.That(vm.Solutions, Has.Count.EqualTo(2));
        Assert.That(OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST")
            .ListSolutions().Select(x => x.SolutionId), Is.EquivalentTo(new[] { "S1", "S2" }));

        vm.NewSolutionId = "S9";
        vm.NewSolutionName = "   ";
        vm.CreateProduct();
        Assert.That(vm.Status, Does.Contain("未写入"));
        Assert.That(vm.Solutions, Has.Count.EqualTo(2));
    }

    [Test]
    public void CreateProductOnlyWorksThroughUnlockedGate()
    {
        var production = new OfflineProductWorkspaceViewModel();
        production.Attach(_db, "SIM_OPERATOR_TEST");
        production.NewSolutionId = "S3";
        production.NewSolutionName = "Blocked Product";
        production.CreateProduct();
        Assert.That(production.Status, Does.Contain("fail closed"));
        Assert.That(production.Solutions, Has.Count.EqualTo(2));

        vm.NewSolutionId = "S3";
        vm.NewSolutionName = "Real Product";
        vm.CreateProduct();
        Assert.That(vm.Solutions.Select(x => x.SolutionId), Does.Contain("S3"));
        Assert.That(vm.ActiveSolutionId, Is.EqualTo("S3"));
        Assert.That(vm.Status, Does.Contain("已创建"));
    }
}
