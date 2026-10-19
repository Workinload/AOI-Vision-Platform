using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;

namespace AOI.Solutions.Tests;

/// <summary>Continuous changeover: previous production evidence is MOVED to archive/, never deleted.</summary>
[TestFixture]
public sealed class ProductionEvidenceArchiveTests
{
    private string root = string.Empty;
    private OfflineProductManager manager = null!;
    private string releaseId = string.Empty;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        manager = OfflineProductManager.Open(Path.Combine(root, "solutions.sqlite"), "SIM_OPERATOR_TEST");
        manager.EnsureSimProfile();
        manager.CreateSimSolution("ARCH", "Archive Product");
        releaseId = manager.Publish("ARCH").Release!.ReleaseId;
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void ArchiveMovesEvidenceWithHashesThenTheNextActivationIsAllowed()
    {
        OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST");
        string production = Path.Combine(root, "master", "production.db");
        byte[] evidence = [1, 2, 3, 4, 5];
        File.WriteAllBytes(production, evidence);
        string expectedSha = Convert.ToHexString(SHA256.HashData(evidence)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(root, "master", "host-peer-interlock.sqlite"), [9, 9, 9]);

        var archived = ProductionEvidenceArchive.Archive(root, "SIM_OPERATOR_TEST");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(production), Is.False, "the original path is vacated, not copied");
            Assert.That(archived.Files, Has.Count.EqualTo(2));
            var productionRow = archived.Files.Single(f => f.OriginalRelativePath == "master/production.db");
            Assert.That(productionRow.Sha256, Is.EqualTo(expectedSha));
            Assert.That(File.Exists(Path.Combine(root, productionRow.ArchivedRelativePath)), Is.True);
            Assert.That(archived.Files.Any(f => f.OriginalRelativePath == "master/host-peer-interlock.sqlite"),
                Is.True, "per-batch peer interlock state must leave the root with the batch");
            Assert.That(File.Exists(Path.Combine(archived.ArchiveDirectory, "archive.json")), Is.True);
        });

        using (var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(archived.ArchiveDirectory, "archive.json"))))
        {
            var row = manifest.RootElement.GetProperty("files").EnumerateArray()
                .Single(f => f.GetProperty("originalRelativePath").GetString() == "master/production.db");
            Assert.That(row.GetProperty("sha256").GetString(), Is.EqualTo(expectedSha));
        }

        // The same run root can now host the NEXT activation (continuous changeover).
        var next = OfflineDualActivation.Activate(root, releaseId, "SIM_OPERATOR_TEST");
        Assert.Multiple(() =>
        {
            Assert.That(next.Committed, Is.True, next.Detail);
            Assert.That(next.PreviousEpoch, Is.EqualTo(1));
            Assert.That(next.NextEpoch, Is.EqualTo(2));
        });
    }

    [Test]
    public void ArchivingAnEmptyRootFailsClosedWithoutCreatingAnArchiveFolder()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ProductionEvidenceArchive.Archive(root, "SIM_OPERATOR_TEST"));
        Assert.That(failure!.Message, Does.Contain("NOTHING_TO_ARCHIVE"));
        Assert.That(Directory.Exists(Path.Combine(root, "archive")), Is.False);
    }
}
