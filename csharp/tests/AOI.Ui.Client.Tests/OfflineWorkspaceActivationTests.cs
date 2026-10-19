using AOI.Domain;
using AOI.Runtime;
using AOI.Solutions;
using AOI.Ui.Avalonia;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Ui.Client.Tests;

/// <summary>
/// End-to-end operator workspace chain on a REAL run root and a REAL dual-Core lease:
/// reference verified frame -> ROI edit -> CAS save -> immutable publish -> dual activation.
/// SIM_ONLY; no Core, PLC, camera or production write is started.
/// </summary>
[TestFixture]
public sealed class OfflineWorkspaceActivationTests
{
    private string root = string.Empty;
    private string db = string.Empty;
    private OfflineWriteGate gate = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-ws-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        db = Path.Combine(root, "solutions.sqlite");
        var seed = OfflineProductManager.Open(db, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Workspace Activation Product");
        gate = new OfflineWriteGate();
    }

    [TearDown]
    public void TearDown()
    {
        gate.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void ReferenceFrameEnablesRoiEditThenPublishAndDualActivation()
    {
        // A prior run root that really captured CH06 at 1200x800, used read-only.
        string referenceRoot = Path.Combine(root, "ref-run");
        var ledger = new VisionAssetLedger(Path.Combine(referenceRoot, "master", "vision"));
        using (var pixels = new Mat(800, 1200, MatType.CV_8UC1, Scalar.Black))
            ledger.CaptureArea(WorkpieceIdentity.New("ref", 1, "REL_REFERENCE"), "CH06", pixels, 1);

        gate.Arm(root);
        var vm = new OfflineProductWorkspaceViewModel(gate);
        vm.Attach(db, "SIM_OPERATOR_TEST");
        vm.ReferenceRunRoot = referenceRoot;
        vm.LoadReferenceFramesCommand.Execute(null);
        Assert.That(vm.ReferenceFrameState, Does.Contain("1 个通道"), vm.ReferenceFrameState);

        vm.SelectedChannelId = "CH06";
        vm.LoadChannelCommand.Execute(null);
        vm.RoiWidth = "0.5";
        vm.SaveDraftCommand.Execute(null);
        var saved = OfflineProductManager.Open(db, "SIM_OPERATOR_TEST").Repository
            .GetDraft("S1", "ST05")!.Recipe;
        string prefix = "roi.CH06.";
        Assert.That(saved.Tunables[prefix + "width"], Is.EqualTo(0.5), vm.Status);

        vm.PublishConfirmed = true;
        vm.PublishCommand.Execute(null);
        Assert.That(vm.PublishedRelease, Does.StartWith("SIMREL-"), vm.Status);

        vm.ActivationConfirmed = true;
        vm.ActivateCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(vm.ActivatedRelease, Is.EqualTo(vm.PublishedRelease), vm.Status);
            Assert.That(vm.ActivatedEpoch, Is.EqualTo("1"));
            Assert.That(vm.ActivationProof, Does.Contain("CommittedHold"));
            Assert.That(vm.ActivationState, Does.Contain("已双机激活"));
        });

        var master = new SimChangeoverNode(Path.Combine(root, "master", "sim-product-node.sqlite"),
            "master", MachineRole.Master, readOnly: true).Snapshot();
        var slave = new SimChangeoverNode(Path.Combine(root, "slave", "sim-product-node.sqlite"),
            "slave", MachineRole.Slave, readOnly: true).Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(master.ActiveReleaseId, Is.EqualTo(vm.PublishedRelease));
            Assert.That(slave.ActiveReleaseId, Is.EqualTo(vm.PublishedRelease));
            Assert.That(master.ActiveEpoch, Is.EqualTo(1));
            Assert.That(slave.ActiveEpoch, Is.EqualTo(1));
            Assert.That(master.Phase, Is.EqualTo(NodeChangeoverPhase.CommittedHold));
            Assert.That(slave.Phase, Is.EqualTo(NodeChangeoverPhase.CommittedHold));
        });
    }

    [Test]
    public void ActivationWithoutConfirmationOrLeaseStaysClosed()
    {
        var vm = new OfflineProductWorkspaceViewModel(gate);
        vm.Attach(db, "SIM_OPERATOR_TEST");
        vm.ActivationConfirmed = true;
        vm.ActivateCommand.Execute(null);
        // The closed writer session refuses BEFORE any changeover code runs.
        Assert.That(vm.Status, Does.Contain("写入会话未开启"));
        Assert.That(vm.ActivatedRelease, Is.EqualTo("未激活"));
        Assert.That(File.Exists(Path.Combine(root, "master", "sim-product-node.sqlite")), Is.False);
    }
}
