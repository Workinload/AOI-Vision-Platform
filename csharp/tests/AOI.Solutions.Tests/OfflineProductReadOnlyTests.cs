using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Solutions.Tests;

/// <summary>
/// OFFLINE operator service: truly read-only A6 access plus transactional CAS publication.
/// No Core, PLC, camera or activation path is involved.
/// </summary>
[TestFixture]
public sealed class OfflineProductReadOnlyTests
{
    private string _root = "";
    private string _db = "";
    private OfflineProductManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-offline-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "solutions.sqlite");
        _manager = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        _manager.EnsureSimProfile();
        _manager.CreateSimSolution("S1", "Read-only Product");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Test]
    public void ReadOnlyOpenOfMissingDatabaseCreatesNoDirectoryOrFile()
    {
        string folder = Path.Combine(_root, "missing");
        string missing = Path.Combine(folder, "solutions.sqlite");
        var reader = OfflineProductManager.Open(missing, "READ_ONLY", readOnly: true);
        Assert.Throws<SqliteException>(() => reader.ListSolutions());
        Assert.That(Directory.Exists(folder), Is.False);
        Assert.That(File.Exists(missing), Is.False);
    }

    [Test]
    public void ReadOnlyOpenOfCorruptStoreReportsAndDoesNotRepair()
    {
        string corrupt = Path.Combine(_root, "corrupt.sqlite");
        File.WriteAllText(corrupt, "not an A6 store");
        string before = Hash(corrupt);
        var reader = OfflineProductManager.Open(corrupt, "READ_ONLY", readOnly: true);
        Assert.Throws<SqliteException>(() => reader.ListSolutions());
        Assert.That(Hash(corrupt), Is.EqualTo(before));
    }

    [Test]
    public void ReadOnlyOpenLeavesDatabaseBytesUnchangedAndReadsPersistedRelease()
    {
        _manager.Publish("S1");
        SqliteConnection.ClearAllPools();
        string before = Hash(_db);
        var reader = OfflineProductManager.Open(_db, "READ_ONLY", readOnly: true);
        Assert.That(reader.ListSolutions().Select(x => x.SolutionId), Does.Contain("S1"));
        var releases = reader.ListReleases();
        Assert.That(releases, Has.Count.EqualTo(1));
        var release = reader.RequirePublished(releases[0].ReleaseId);
        Assert.That(release.ContentSha256, Is.EqualTo(releases[0].ContentSha256));
        Assert.That(reader.Repository.GetDraft("S1", "ST03"), Is.Not.Null);
        Assert.That(Hash(_db), Is.EqualTo(before));
    }

    [Test]
    public void ReadOnlyOpenNeverRewritesTheA6DatabaseFile()
    {
        _manager.Publish("S1");
        SqliteConnection.ClearAllPools();
        string folder = Path.GetDirectoryName(_db)!;
        string baseName = Path.GetFileName(_db);
        var before = Directory.GetFiles(folder).ToDictionary(x => Path.GetFileName(x)!, Hash);
        var reader = OfflineProductManager.Open(_db, "READ_ONLY", readOnly: true);
        Assert.That(reader.ListSolutions(), Has.Count.EqualTo(1));
        Assert.That(reader.ListReleases(), Has.Count.EqualTo(1));
        Assert.That(reader.Repository.GetDraft("S1", "ST05"), Is.Not.Null);
        var after = Directory.GetFiles(folder).ToDictionary(x => Path.GetFileName(x)!, Hash);
        foreach (var (name, hash) in before)
        {
            Assert.That(after, Does.ContainKey(name), "read-only access removed " + name);
            Assert.That(after[name], Is.EqualTo(hash),
                "read-only access rewrote the original file " + name);
        }
        // SQLite may create its WAL shared-memory sidecars for a read-only WAL connection;
        // the A6 database file itself (hashed above) is never written or repaired.
        var extra = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToArray();
        Assert.That(extra, Is.All.AnyOf(baseName + "-shm", baseName + "-wal"),
            "read-only access created an unexpected file");
    }

    [Test]
    public void SafePublishRejectsDraftChangedAfterOperatorSnapshotWithoutNewRelease()
    {
        var expected = _manager.ListDraftRevisions("S1");
        Assert.That(expected, Has.Count.EqualTo(ChannelCatalog.All
            .Select(c => c.StationId).Distinct(StringComparer.Ordinal).Count()));
        var other = OfflineProductManager.Open(_db, "CONCURRENT");
        var draft = other.Repository.GetDraft("S1", "ST05")!;
        other.SaveStation("S1", draft.Recipe with
        {
            Tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
            {
                ["brightThreshold"] = 200
            }
        }, draft.Revision);

        var result = _manager.Publish("S1", expected, verifiedFrames: null);
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("DRAFT_REVISION_CONFLICT"));
        Assert.That(_manager.ListReleases(), Is.Empty);
    }

    [Test]
    public void SafePublishRejectsMissingStationInOperatorSnapshot()
    {
        var expected = new Dictionary<string, long>(_manager.ListDraftRevisions("S1"),
            StringComparer.Ordinal);
        expected.Remove("ST05");
        var result = _manager.Publish("S1", expected, verifiedFrames: null);
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("DRAFT_REVISION_CONFLICT"));
        Assert.That(_manager.ListReleases(), Is.Empty);
    }

    [Test]
    public void SafePublishValidatesInstalledPluginsInsideTransactionWithoutRelease()
    {
        var repository = _manager.Repository;
        var draft = repository.GetDraft("S1", "ST05")!;
        repository.SaveDraft("S1", draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06", "custom.not-installed.v1",
                    new Dictionary<string, double>(), new Dictionary<string, string>())
            ]
        }, draft.Revision, "legacy-import");
        var expected = _manager.ListDraftRevisions("S1");

        var result = _manager.Publish("S1", expected, verifiedFrames: null);
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("INSPECTION_PLUGIN_NOT_INSTALLED"));
        Assert.That(_manager.ListReleases(), Is.Empty);
    }

    [Test]
    public void SafePublishWithMatchingSnapshotPersistsAndReopensReadOnly()
    {
        var expected = _manager.ListDraftRevisions("S1");
        var result = _manager.Publish("S1", expected, verifiedFrames: null);
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), result.Message);
        var release = result.Release!;
        Assert.That(release.ContentSha256, Has.Length.EqualTo(64));

        var reader = OfflineProductManager.Open(_db, "READ_ONLY", readOnly: true);
        var reopened = reader.RequirePublished(release.ReleaseId);
        Assert.That(reopened.ContentSha256, Is.EqualTo(release.ContentSha256));
        Assert.That(reader.ListReleases(), Has.Count.EqualTo(1));
    }
}
