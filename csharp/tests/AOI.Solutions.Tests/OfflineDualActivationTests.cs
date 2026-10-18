using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

/// <summary>
/// The shared two-node offline activation used by BOTH the operator GUI and the Recipes CLI.
/// Every case is SIM_ONLY: no PLC, camera, FINS or production output is touched.
/// </summary>
[TestFixture]
public sealed class OfflineDualActivationTests
{
    private string root = string.Empty;
    private OfflineProductManager manager = null!;
    private string releaseId = string.Empty;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-dual-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        manager = OfflineProductManager.Open(
            Path.Combine(root, "solutions.sqlite"), "SIM_OPERATOR_TEST");
        manager.EnsureSimProfile();
        manager.CreateSimSolution("ACT_PRODUCT", "Dual Activation Product");
        var published = manager.Publish("ACT_PRODUCT");
        Assert.That(published.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), published.Message);
        releaseId = published.Release!.ReleaseId;
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void ActivationCommitsBothNodesToTheSameReleaseAndNextEpoch()
    {
        var first = OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST");
        Assert.Multiple(() =>
        {
            Assert.That(first.Committed, Is.True, first.Detail);
            Assert.That(first.ReleaseId, Is.EqualTo(releaseId));
            Assert.That(first.ContentSha256, Has.Length.EqualTo(64));
            Assert.That(first.PreviousEpoch, Is.EqualTo(0));
            Assert.That(first.NextEpoch, Is.EqualTo(1));
            Assert.That(first.Master.Phase, Is.EqualTo(NodeChangeoverPhase.CommittedHold));
            Assert.That(first.Slave.Phase, Is.EqualTo(NodeChangeoverPhase.CommittedHold));
            Assert.That(first.Master.ActiveReleaseId, Is.EqualTo(releaseId));
            Assert.That(first.Slave.ActiveReleaseId, Is.EqualTo(releaseId));
            Assert.That(first.Master.ActiveEpoch, Is.EqualTo(1));
            Assert.That(first.Slave.ActiveEpoch, Is.EqualTo(1));
        });

        // A second activation of the same immutable release advances exactly one epoch.
        var second = OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST");
        Assert.Multiple(() =>
        {
            Assert.That(second.Committed, Is.True, second.Detail);
            Assert.That(second.PreviousEpoch, Is.EqualTo(1));
            Assert.That(second.NextEpoch, Is.EqualTo(2));
            Assert.That(second.Master.ActiveEpoch, Is.EqualTo(2));
            Assert.That(second.Slave.ActiveEpoch, Is.EqualTo(2));
        });
    }

    [Test]
    public void UnknownReleaseIdIsRejectedBeforeAnyNodeIsWritten()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            OfflineDualActivation.Activate(root, "SIMREL-00000000000000000000000000000000",
                "SIM_OPERATOR_TEST"));
        Assert.That(failure!.Message, Does.Contain("UNKNOWN_RELEASE_ID"));
        Assert.That(File.Exists(Path.Combine(root, "master", "sim-product-node.sqlite")), Is.False);
    }

    [Test]
    public void ExistingProductionEvidenceBlocksActivationWithoutDeletingIt()
    {
        string production = Path.Combine(root, "master", "production.db");
        Directory.CreateDirectory(Path.GetDirectoryName(production)!);
        File.WriteAllBytes(production, [1, 2, 3]);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST"));
        Assert.Multiple(() =>
        {
            Assert.That(failure!.Message, Does.Contain("PRIOR_EVIDENCE"));
            Assert.That(File.Exists(production), Is.True, "original evidence must be preserved.");
        });
    }

    [Test]
    public void NodesThatAreNotJointlyInSafeHoldAreRejected()
    {
        OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST");
        string masterDb = Path.Combine(root, "master", "sim-product-node.sqlite");
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection("Data Source=" + masterDb))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE active SET epoch=9 WHERE slot=1";
            Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
        }
        SqliteConnection.ClearAllPools();
        var failure = Assert.Throws<InvalidOperationException>(() =>
            OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST"));
        Assert.That(failure!.Message, Does.Contain("ACTIVATION_BLOCKED"));
    }
}
