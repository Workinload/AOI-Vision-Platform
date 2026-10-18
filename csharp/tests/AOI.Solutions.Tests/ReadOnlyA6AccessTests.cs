using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class ReadOnlyA6AccessTests
{
    private string root = "";

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-readonly-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public void MissingReadOnlyRepositoryDoesNotCreateAnyDirectoryOrDatabase()
    {
        string folder = Path.Combine(root, "missing");
        var repository = new SolutionRepository(Path.Combine(folder, "solutions.sqlite"), readOnly: true);
        Assert.Throws<SqliteException>(() => repository.RequireRelease("missing"));
        Assert.That(Directory.Exists(folder), Is.False);
    }

    [Test]
    public void ReadOnlyRepositoryVerifiesStoredReleaseAndRejectsMutation()
    {
        string path = Path.Combine(root, "solutions.sqlite");
        var writer = new SolutionRepository(path);
        writer.RegisterProfile(MachineProfile.SimBaseline(), "qa");
        writer.CreateSolution(new ProductSolution("READONLY", "SIM_MACHINE_01", "Read-only test"), "qa");
        foreach (var station in ChannelCatalog.All.GroupBy(c => c.StationId))
            writer.SaveDraft("READONLY", new StationRecipe(station.Key,
                station.Select(c => c.ChannelId).ToArray(), new Dictionary<string, double>()), 0, "qa");
        var release = writer.Publish("READONLY", "qa");
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        var reader = new SolutionRepository(path, readOnly: true);
        var actual = reader.RequireRelease(release.ReleaseId);
        Assert.That(actual.ContentSha256, Is.EqualTo(release.ContentSha256));
        Assert.Throws<SqliteException>(() => reader.RegisterProfile(MachineProfile.SimBaseline(), "qa"));
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(before));
    }

    [Test]
    public void ReadOnlyNodeCannotInventAnIdleSafeHoldRowInIncompleteEvidence()
    {
        string path = Path.Combine(root, "incomplete.sqlite");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE original_evidence(value TEXT); INSERT INTO original_evidence VALUES('DO_NOT_REPAIR');";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(path);
        var node = new SimChangeoverNode(path, "master", MachineRole.Master, readOnly: true);
        Assert.Throws<SqliteException>(() => node.Snapshot());
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public void ExistingNodeSnapshotCanBeReadWhileItsCoreLeaseIsHeld()
    {
        string path = Path.Combine(root, "node.sqlite");
        var writer = new SimChangeoverNode(path, "master", MachineRole.Master);
        var expected = writer.Snapshot();
        byte[] before = File.ReadAllBytes(path);
        using var live = OfflineCoreOperationGate.HoldCoreForLifetime(root, MachineRole.Master);
        var reader = new SimChangeoverNode(path, "master", MachineRole.Master, readOnly: true);
        Assert.That(reader.Snapshot(), Is.EqualTo(expected));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }
}
