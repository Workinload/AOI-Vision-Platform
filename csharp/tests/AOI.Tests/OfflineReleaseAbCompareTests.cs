using AOI.Domain;
using AOI.OfflineCompare;
using AOI.Runtime;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AOI.Tests;

/// <summary>
/// DSH-B offline A/B re-inspection: one immutable PNG set, two persisted A6
/// releases, real OpenCV pixel results. SIM_ONLY; no PLC, camera or production write.
/// </summary>
[TestFixture]
public sealed class OfflineReleaseAbCompareTests
{
    private string _root = string.Empty;
    private string _images = string.Empty;
    private string _database = string.Empty;
    private AbFixtureSolution.FixtureReleases _releases = null!;

    [SetUp]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-dshb-154-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _images = Path.Combine(_root, "images");
        _database = Path.Combine(_root, "fixture-solutions.sqlite");
        AbFixtureImages.Write(_images);
        _releases = AbFixtureSolution.ScaffoldDatabase(_database);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AbCompareManifest Run(string name = "report", string? releaseA = null,
        string? releaseB = null, string database = "",
        LineScanRowMapping mapping = LineScanRowMapping.DemoEvenOdd) =>
        new ReleaseAbComparer().Compare(new AbCompareRequest(
            database.Length == 0 ? _database : database, _images, Path.Combine(_root, name),
            releaseA ?? _releases.ReleaseA, releaseB ?? _releases.ReleaseB,
            LineMapping: mapping));

    private static AbChannelResult Channel(AbCompareManifest manifest, string channelId) =>
        manifest.Channels.Single(c => c.ChannelId == channelId);

    [Test]
    public void SameImmutablePngs_TwoPersistedReleases_ChangeOnlyRoiAndThresholdChannels()
    {
        var manifest = Run();
        var ch02 = Channel(manifest, "CH02");
        var ch06 = Channel(manifest, "CH06");
        string expected = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(_images, "CH06.png")))).ToLowerInvariant();

        Assert.Multiple(() =>
        {
            Assert.That(manifest.ReleaseA.ReleaseId, Is.Not.EqualTo(manifest.ReleaseB.ReleaseId));
            Assert.That(manifest.ReleaseA.DigestVerified, Is.True);
            Assert.That(manifest.ReleaseB.DigestVerified, Is.True);
            Assert.That(manifest.ReleaseA.RecomputedContentSha256, Has.Length.EqualTo(64));
            Assert.That(manifest.DatabaseUnchanged, Is.True);
            Assert.That(manifest.DatabaseSnapshotSha256, Is.EqualTo(manifest.DatabaseSha256Before));
            Assert.That(manifest.InputImagesUnchanged, Is.True);
            Assert.That(manifest.Mode, Is.EqualTo("OFFLINE_ONLY_NOT_PRODUCTION"));

            Assert.That(manifest.Channels, Has.Count.EqualTo(11));
            Assert.That(manifest.ChangedChannels, Is.EquivalentTo(new[] { "CH02", "CH06" }));

            Assert.That(ch06.InputSha256, Is.EqualTo(expected));
            Assert.That(ch06.A.Status, Is.EqualTo(AbChannelStatus.Ok));
            Assert.That(ch06.B.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(ch06.B.Defects, Has.Count.EqualTo(1));
            Assert.That(ch06.A.RoiWidth, Is.EqualTo(600));
            Assert.That(ch06.B.RoiWidth, Is.EqualTo(1200));
            Assert.That(ch06.Changed, Is.True);

            Assert.That(ch02.A.Status, Is.EqualTo(AbChannelStatus.Ok));
            Assert.That(ch02.B.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(ch02.A.BinaryThreshold, Is.EqualTo((byte)180));
            Assert.That(ch02.B.BinaryThreshold, Is.EqualTo((byte)90));

            Assert.That(Channel(manifest, "CH04").A.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(Channel(manifest, "CH04").B.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(Channel(manifest, "CH04").Changed, Is.False);
        });

        Assert.Multiple(() =>
        {
            // The production database directory is untouched: not even SQLite sidecars.
            Assert.That(File.Exists(_database + "-wal"), Is.False);
            Assert.That(File.Exists(_database + "-shm"), Is.False);
            Assert.That(Directory.Exists(Path.Combine(manifest.OutputRoot, "_db-snapshot")), Is.True);
        });

        var overlayA = Path.Combine(manifest.OutputRoot,
            ch06.A.AnnotationRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        var overlayB = Path.Combine(manifest.OutputRoot,
            ch06.B.AnnotationRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(overlayA), Is.True);
            Assert.That(File.Exists(overlayB), Is.True);
            Assert.That(File.Exists(Path.Combine(manifest.OutputRoot, "ab-manifest.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(manifest.OutputRoot, "channel-results.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(manifest.OutputRoot, "inputs.sha256.txt")), Is.True);
            Assert.That(ch06.A.AnnotationRelativePath, Is.Not.EqualTo(ch06.B.AnnotationRelativePath));
        });
    }

    [Test]
    public void LineScanParentFeedsTwoLogicalChannelsWithExplicitRowMapping()
    {
        var manifest = Run();
        var ch08 = Channel(manifest, "CH08");
        var ch09 = Channel(manifest, "CH09");
        var ch10 = Channel(manifest, "CH10");
        var ch11 = Channel(manifest, "CH11");

        Assert.Multiple(() =>
        {
            Assert.That(manifest.LineRowMapping, Is.EqualTo("DemoEvenOdd"));
            Assert.That(ch08.PhysicalDeviceId, Is.EqualTo("LINE-S-8"));
            Assert.That(ch09.PhysicalDeviceId, Is.EqualTo("LINE-S-8"));
            Assert.That(ch08.InputSha256, Is.EqualTo(ch09.InputSha256));
            Assert.That(ch08.ParentInputSha256, Is.EqualTo(ch08.InputSha256));
            Assert.That(ch09.ParentInputSha256, Is.EqualTo(ch08.InputSha256));
            Assert.That(ch08.InputHeight, Is.EqualTo(1200));
            Assert.That(ch08.PixelsHeight, Is.EqualTo(600));
            Assert.That(ch09.PixelsHeight, Is.EqualTo(600));
            Assert.That(ch08.A.Status, Is.EqualTo(AbChannelStatus.Ok));
            Assert.That(ch09.A.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(ch10.A.Status, Is.EqualTo(AbChannelStatus.Ng));
            Assert.That(ch11.A.Status, Is.EqualTo(AbChannelStatus.Ok));
            Assert.That(ch10.InputSha256, Is.Not.EqualTo(ch08.InputSha256));
        });

        var legacy = Run("report-legacy", mapping: LineScanRowMapping.LegacySkipFirstRow);
        Assert.Multiple(() =>
        {
            Assert.That(legacy.LineRowMapping, Is.EqualTo("LegacySkipFirstRow"));
            Assert.That(Channel(legacy, "CH08").PixelsHeight, Is.EqualTo(600));
            Assert.That(Channel(legacy, "CH09").PixelsHeight, Is.EqualTo(599));
        });
    }

    [Test]
    public void PositioningChannelIsNeverReportedAsInspectionOk()
    {
        var manifest = Run();
        var ch01 = Channel(manifest, "CH01");
        Assert.Multiple(() =>
        {
            Assert.That(ch01.IsPositioning, Is.True);
            Assert.That(ch01.A.Status, Is.EqualTo(AbChannelStatus.Positioning));
            Assert.That(ch01.B.Status, Is.EqualTo(AbChannelStatus.Positioning));
            Assert.That(ch01.A.Defects, Is.Empty);
            Assert.That(ch01.Changed, Is.False);
            Assert.That(ch01.A.RoiWidth, Is.EqualTo(900));
        });
    }

    [Test]
    public void MissingPngAndUnsupportedPluginNeverBecomeOk()
    {
        File.Delete(Path.Combine(_images, "CH07.png"));
        var missing = Run();
        var ch07 = Channel(missing, "CH07");
        Assert.Multiple(() =>
        {
            Assert.That(ch07.A.Status, Is.EqualTo(AbChannelStatus.Missing));
            Assert.That(ch07.B.Status, Is.EqualTo(AbChannelStatus.Missing));
            Assert.That(ch07.A.Reason, Is.EqualTo("MISSING_IMAGE"));
            Assert.That(ch07.Changed, Is.False);
            Assert.That(missing.Devices.Single(d => d.PhysicalDeviceId == "HIK-M-3").DecodeStatus,
                Is.EqualTo(AbChannelStatus.Missing));
        });

        // Independent image set: the CH07 PNG stays present for the plugin case.
        var otherImages = Path.Combine(_root, "images-unsupported");
        AbFixtureImages.Write(otherImages);
        var otherDatabase = Path.Combine(_root, "fixture-unsupported.sqlite");
        var ids = AbFixtureSolution.ScaffoldDatabase(otherDatabase, includeUnsupportedRelease: true);
        var unsupported = new ReleaseAbComparer().Compare(new AbCompareRequest(otherDatabase,
            otherImages, Path.Combine(_root, "report-unsupported"),
            ids.ReleaseB, ids.UnsupportedRelease!));
        var ch07c = Channel(unsupported, "CH07");
        Assert.Multiple(() =>
        {
            Assert.That(ch07c.A.Status, Is.EqualTo(AbChannelStatus.Ok));
            Assert.That(ch07c.B.Status, Is.EqualTo(AbChannelStatus.Unsupported));
            Assert.That(ch07c.B.Reason, Does.Contain("INSPECTION_PROCESSOR_NOT_READY"));
            Assert.That(ch07c.Changed, Is.True);
            Assert.That(unsupported.ChangedChannels, Does.Contain("CH07"));
        });
    }

    [Test]
    public void ReportRootIsNeverReusedOrPlacedInsideTheImageFixture()
    {
        Run("report");
        var reuse = Assert.Throws<InvalidOperationException>(() => Run("report"));
        Assert.That(reuse!.Message, Does.Contain("OUTPUT_ROOT_NOT_EMPTY"));
        var inside = Assert.Throws<InvalidOperationException>(() =>
            new ReleaseAbComparer().Compare(new AbCompareRequest(_database, _images,
                Path.Combine(_images, "report"), _releases.ReleaseA, _releases.ReleaseB)));
        Assert.That(inside!.Message, Does.Contain("OUTPUT_ROOT_INSIDE_IMAGE_TREE"));
    }

    [Test]
    public void UnknownReleaseAndIdenticalReleasesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Run(releaseA: "SIMREL-00000000000000000000000000000000"));
        Assert.Throws<ArgumentException>(() =>
            Run(releaseA: _releases.ReleaseA, releaseB: _releases.ReleaseA));
    }

    [Test]
    public void TamperedReleaseDigestInDatabaseIsRejected()
    {
        var known = new SolutionRepository(_database, readOnly: true).RequireRelease(_releases.ReleaseA);
        using (var connection = new SqliteConnection("Data Source=" + _database))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE releases SET body = replace(body, $real, $fake) WHERE id = $id";
            command.Parameters.AddWithValue("$real", known.ContentSha256);
            command.Parameters.AddWithValue("$fake", new string('a', 64));
            command.Parameters.AddWithValue("$id", _releases.ReleaseA);
            Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
        }
        SqliteConnection.ClearAllPools();
        var failure = Assert.Throws<InvalidDataException>(() => Run());
        Assert.That(failure!.Message, Does.Contain("digest").IgnoreCase);
    }
}
