using AOI.Domain;
using AOI.Solutions;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class OfflineCoreOperationGateTests
{
    private string _folder = "";

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "aoi-offline-lease-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Test]
    public void ActualRunningMasterOrSlaveBlocksOfflinePublishAndActivation()
    {
        using (var master = OfflineCoreOperationGate.HoldCoreForLifetime(
            _folder, MachineRole.Master))
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                OfflineCoreOperationGate.AcquireStoppedCores(_folder));
            Assert.That(ex!.Message, Does.Contain("OFFLINE_CHANGEOVER_BLOCKED"));
        }
        using (var slave = OfflineCoreOperationGate.HoldCoreForLifetime(
            _folder, MachineRole.Slave))
        {
            Assert.Throws<InvalidOperationException>(() =>
                OfflineCoreOperationGate.AcquireStoppedCores(_folder));
        }
        using var stopped = OfflineCoreOperationGate.AcquireStoppedCores(_folder);
        stopped.RequireCleanActivationRoot();
    }

    [Test]
    public void OfflineLeasePreventsBothCoreStartupsUntilLastSqliteCommit()
    {
        using (var stopped = OfflineCoreOperationGate.AcquireStoppedCores(_folder))
        {
            Assert.Throws<IOException>(() =>
            {
                using var ignored = OfflineCoreOperationGate.HoldCoreForLifetime(
                    _folder, MachineRole.Master);
            });
            Assert.Throws<IOException>(() =>
            {
                using var ignored = OfflineCoreOperationGate.HoldCoreForLifetime(
                    _folder, MachineRole.Slave);
            });
            Assert.Throws<InvalidOperationException>(() =>
                OfflineCoreOperationGate.AcquireStoppedCores(_folder));
        }
        using var newlyStarted = OfflineCoreOperationGate.HoldCoreForLifetime(
            _folder, MachineRole.Master);
        Assert.That(newlyStarted, Is.Not.Null);
    }

    [TestCase("production.db")]
    [TestCase("production.db-wal")]
    [TestCase("production.db-shm")]
    [TestCase("sim-full-cycle.db")]
    [TestCase("final-plc-output.db")]
    [TestCase("timing-events.sqlite-wal")]
    public void OriginalProductionEvidenceAlwaysBlocksNewReleaseWithoutMutation(
        string filename)
    {
        string directory = Path.Combine(_folder, "slave");
        Directory.CreateDirectory(directory);
        string witness = Path.Combine(directory, filename);
        File.WriteAllText(witness, "DO_NOT_ERASE_OLD_PLC_OUTPUT");
        using (var stopped = OfflineCoreOperationGate.AcquireStoppedCores(_folder))
        {
            var ex = Assert.Throws<InvalidOperationException>(
                stopped.RequireCleanActivationRoot);
            Assert.That(ex!.Message, Does.Contain("PRIOR_EVIDENCE"));
        }
        Assert.That(File.ReadAllText(witness),
            Is.EqualTo("DO_NOT_ERASE_OLD_PLC_OUTPUT"));
    }

    [Test]
    public void HistoricalLockFileAfterCrashDoesNotPretendCoreStillRunning()
    {
        using (var live = OfflineCoreOperationGate.HoldCoreForLifetime(
            _folder, MachineRole.Master))
        {
            Assert.That(live, Is.Not.Null);
        }
        // The file remains for forensics, but OS lock lifetime ends with
        // its handle; mere existence must not strand a fresh empty root.
        Assert.That(File.Exists(Path.Combine(
            _folder, "master", ".aoi-core-running.lease")), Is.True);
        using var stopped = OfflineCoreOperationGate.AcquireStoppedCores(_folder);
        stopped.RequireCleanActivationRoot();
    }
}
