using AOI.Solutions;
using AOI.Ui.Avalonia;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OfflineWriterSessionViewModelTests
{
    private string root = string.Empty;
    private string db = string.Empty;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-vm-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        db = Path.Combine(root, "solutions.sqlite");
        var seed = OfflineProductManager.Open(db, "SIM_OPERATOR_TEST");
        seed.EnsureSimProfile();
        seed.CreateSimSolution("S1", "Gate Session Product");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Test]
    public void ManualAttachWithoutLeaseIsReadOnlyAndWritesStayClosed()
    {
        var vm = new OfflineProductWorkspaceViewModel();
        vm.Attach(db, "SIM_OPERATOR_TEST");
        Assert.That(vm.IsOfflineMode, Is.True);
        Assert.That(vm.A6WritesAllowed, Is.False, "no lease means no writes, even after a manual attach");
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("fail closed"));
    }

    [Test]
    public void DisablingWriterSessionDropsWritableManagerAndEvidence()
    {
        var vm = new OfflineProductWorkspaceViewModel();
        vm.Attach(db, "SIM_OPERATOR_TEST");
        vm.DisableOfflineWrites();
        Assert.That(vm.A6WritesAllowed, Is.False);
        Assert.That(vm.WriteState, Does.Contain("关闭"));
        vm.SaveDraft();
        Assert.That(vm.Status, Does.Contain("fail closed"));
    }

    [Test]
    public void LeaseForAnotherRootCannotEnableWritesOnThisDatabase()
    {
        string otherRoot = root + "-other";
        Directory.CreateDirectory(otherRoot);
        try
        {
            var gate = new OfflineWriteGate();
            bool armed;
            try { gate.Arm(otherRoot); armed = gate.A6WritesAllowed; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { armed = false; }
            if (armed)
            {
                Assert.That(gate.CoversDatabase(db), Is.False, "runA lease must not cover runB database");
            }
            gate.Dispose();
        }
        finally { try { Directory.Delete(otherRoot, recursive: true); } catch { } }
    }
}
