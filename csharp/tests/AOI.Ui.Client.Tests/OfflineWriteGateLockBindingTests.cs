using AOI.Solutions;
using AOI.Ui.Avalonia;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class OfflineWriteGateLockBindingTests
{
    private string rootA = string.Empty;
    private string rootB = string.Empty;

    [SetUp]
    public void SetUp()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "aoi-gate-" + Guid.NewGuid().ToString("N"));
        rootA = Path.Combine(baseDir, "runA");
        rootB = Path.Combine(baseDir, "runB");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(Path.GetDirectoryName(rootA)!, recursive: true); } catch { }
    }

    [Test]
    public void ClosedGateCoversNoDatabase()
    {
        using var gate = new OfflineWriteGate();
        Assert.That(gate.A6WritesAllowed, Is.False);
        Assert.That(gate.CoversDatabase(Path.Combine(rootA, "solutions.sqlite")), Is.False);
    }

    [Test]
    public void ArmedLeaseCoversOnlyItsOwnRunRoot()
    {
        using var gate = new OfflineWriteGate();
        try
        {
            gate.Arm(rootA);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Assert.That(gate.A6WritesAllowed, Is.False, "failed arm must leave writes closed");
            Assert.That(gate.CoversDatabase(Path.Combine(rootA, "solutions.sqlite")), Is.False);
            return;
        }
        Assert.That(gate.A6WritesAllowed, Is.True);
        Assert.That(gate.CoversDatabase(Path.Combine(rootA, "solutions.sqlite")), Is.True);
        Assert.That(gate.CoversDatabase(Path.Combine(rootB, "solutions.sqlite")), Is.False,
            "a lease for runA must never authorise writes in runB");
    }

    [Test]
    public void ReleaseClearsBinding()
    {
        var gate = new OfflineWriteGate();
        try { gate.Arm(rootA); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return; }
        gate.Release();
        Assert.That(gate.A6WritesAllowed, Is.False);
        Assert.That(gate.CoversDatabase(Path.Combine(rootA, "solutions.sqlite")), Is.False);
        gate.Dispose();
    }

    [Test]
    public void MissingDatabaseNeverArmsWrites()
    {
        var vm = new OfflineProductWorkspaceViewModel();
        vm.EntryPath = Path.Combine(rootA, "does-not-exist");
        vm.OperatorId = "SIM_OPERATOR_TEST";
        vm.EnableOfflineWrites();
        Assert.That(vm.A6WritesAllowed, Is.False);
        Assert.That(vm.WriteState, Does.Contain("失败"));
        Assert.That(Directory.Exists(Path.Combine(rootA, "master")), Is.False,
            "arming must never fabricate a run root");
    }
}
