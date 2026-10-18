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
/// releases, real OpenCV pixel results. Scoped acceptance IDs TC-154-AB-01..07.
/// SIM_ONLY; no PLC, camera or production write.
/// </summary>
[TestFixture]
public sealed class OfflineReleaseAbCompareTests
{
    private string _root = string.Empty;
    private string _run = string.Empty;
    private string _images = string.Empty;
    private string _database = string.Empty;
    private AbFixtureSolution.FixtureReleases _releases = null!;

    [SetUp]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-dshb-154-" + Guid.NewGuid().ToString("N"));
        // The A6 database lives in its own run folder, exactly like a real Core run
        // root, so the "report must not enter the run folder" rule is exercised.
        _run = Path.Combine(_root, "run");
        _images = Path.Combine(_root, "fixtures", "images");
        _database = Path.Combine(_run, "fixture-solutions.sqlite");
        Directory.CreateDirectory(_run);
        AbFixtureImages.Write(_images);
        _releases = AbFixtureSolution.ScaffoldDatabase(_database);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string ReportDir(string name) => Path.Combine(_root, "reports", name);

    private AbCompareManifest Run(string name = "report", string? releaseA = null,
        string? releaseB = null, string database = "",
        LineScanRowMapping mapping = LineScanRowMapping.DemoEvenOdd,
        bool includeAbsolutePaths = false) =>
        new ReleaseAbComparer().Compare(new AbCompareRequest(
            database.Length == 0 ? _database : database, _images, ReportDir(name),
            releaseA ?? _releases.ReleaseA, releaseB ?? _releases.ReleaseB,
            LineMapping: mapping, IncludeAbsolutePaths: includeAbsolutePaths));

    private static AbChannelResult Channel(AbCompareManifest manifest, string channelId) =>
        manifest.Channels.Single(c => c.ChannelId == channelId);

    [Test]
    [Description("TC-154-AB-01 same immutable PNGs, two persisted releases: only ROI/threshold channels change")]
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

            // 7 stations / 11 logical channels / 9 physical devices are distinct facts.
            Assert.That(manifest.ReleaseA.StationCount, Is.EqualTo(7));
            Assert.That(manifest.ReleaseA.ChannelCount, Is.EqualTo(11));
            Assert.That(manifest.ReleaseB.StationCount, Is.EqualTo(7));
            Assert.That(manifest.ReleaseB.ChannelCount, Is.EqualTo(11));
            Assert.That(manifest.Devices, Has.Count.EqualTo(9));

            // Committed evidence must not disclose the developer machine or absolute paths.
            Assert.That(manifest.AbsolutePathsIncluded, Is.False);
            Assert.That(manifest.DatabasePath, Is.EqualTo("fixture-solutions.sqlite"));
            Assert.That(manifest.ImageRoot, Is.EqualTo("images"));
            Assert.That(manifest.OutputRoot, Is.EqualTo("."));
            Assert.That(manifest.DatabasePath, Does.Not.Contain(":"));

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
            Assert.That(Directory.Exists(Path.Combine(ReportDir("report"), "_db-snapshot")), Is.True);
        });

        var overlayA = Path.Combine(ReportDir("report"),
            ch06.A.AnnotationRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        var overlayB = Path.Combine(ReportDir("report"),
            ch06.B.AnnotationRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(overlayA), Is.True);
            Assert.That(File.Exists(overlayB), Is.True);
            Assert.That(File.Exists(Path.Combine(ReportDir("report"), "ab-manifest.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(ReportDir("report"), "channel-results.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(ReportDir("report"), "inputs.sha256.txt")), Is.True);
            Assert.That(ch06.A.AnnotationRelativePath, Is.Not.EqualTo(ch06.B.AnnotationRelativePath));
        });

        // Operators can opt in to absolute paths for a local-only run.
        var absolute = Run("report-absolute", includeAbsolutePaths: true);
        Assert.Multiple(() =>
        {
            Assert.That(absolute.AbsolutePathsIncluded, Is.True);
            Assert.That(Path.IsPathRooted(absolute.DatabasePath), Is.True);
            Assert.That(absolute.DatabasePath, Is.EqualTo(_database));
        });
    }

    [Test]
    [Description("TC-154-AB-02 one line-scan parent feeds two logical children with an explicit row mapping")]
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
    [Description("TC-154-AB-03 the CH01 positioning channel is never reported as an inspection OK")]
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
    [Description("TC-154-AB-04 missing PNG, corrupt PNG and an unsupported plugin never become OK")]
    public void MissingCorruptPngAndUnsupportedPluginNeverBecomeOk()
    {
        // A truncated file with a valid PNG signature must surface per channel, not crash.
        File.WriteAllBytes(Path.Combine(_images, "CH03.png"), [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
        File.Delete(Path.Combine(_images, "CH07.png"));
        var broken = Run("report-broken");

        var ch03 = Channel(broken, "CH03");
        var ch07 = Channel(broken, "CH07");
        Assert.Multiple(() =>
        {
            Assert.That(ch03.A.Status, Is.EqualTo(AbChannelStatus.Error));
            Assert.That(ch03.A.Reason, Does.StartWith("INVALID_IMAGE"));
            Assert.That(ch03.B.Status, Is.EqualTo(AbChannelStatus.Error));
            Assert.That(ch03.Changed, Is.False);

            Assert.That(ch07.A.Status, Is.EqualTo(AbChannelStatus.Missing));
            Assert.That(ch07.B.Status, Is.EqualTo(AbChannelStatus.Missing));
            Assert.That(ch07.A.Reason, Is.EqualTo("MISSING_IMAGE"));
            Assert.That(ch07.Changed, Is.False);
            Assert.That(broken.Devices.Single(d => d.PhysicalDeviceId == "HIK-M-3").DecodeStatus,
                Is.EqualTo(AbChannelStatus.Missing));

            // A broken sibling must not stop the remaining physical devices.
            Assert.That(Channel(broken, "CH06").B.Status, Is.EqualTo(AbChannelStatus.Ng));
        });

        var otherImages = Path.Combine(_root, "fixtures-unsupported", "images");
        AbFixtureImages.Write(otherImages);
        var otherDatabase = Path.Combine(_root, "run-unsupported", "fixture-unsupported.sqlite");
        var ids = AbFixtureSolution.ScaffoldDatabase(otherDatabase, includeUnsupportedRelease: true);
        var unsupported = new ReleaseAbComparer().Compare(new AbCompareRequest(otherDatabase,
            otherImages, ReportDir("report-unsupported"), ids.ReleaseB, ids.UnsupportedRelease!));
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
    [Description("TC-154-AB-05 report root is never reused, never inside the image tree, never inside the A6 run folder")]
    public void ReportRootNeverReusedOrInsideImageTreeOrDatabaseRunRoot()
    {
        Run("report");
        var reuse = Assert.Throws<InvalidOperationException>(() => Run("report"));
        Assert.That(reuse!.Message, Does.Contain("OUTPUT_ROOT_NOT_EMPTY"));

        var insideImages = Assert.Throws<InvalidOperationException>(() =>
            new ReleaseAbComparer().Compare(new AbCompareRequest(_database, _images,
                Path.Combine(_images, "report"), _releases.ReleaseA, _releases.ReleaseB)));
        Assert.That(insideImages!.Message, Does.Contain("OUTPUT_ROOT_INSIDE_IMAGE_TREE"));

        // The A6 database's own run folder is off limits even though it is not the image tree.
        var insideRun = Assert.Throws<InvalidOperationException>(() =>
            new ReleaseAbComparer().Compare(new AbCompareRequest(_database, _images,
                Path.Combine(_run, "reports", "x"), _releases.ReleaseA, _releases.ReleaseB)));
        Assert.That(insideRun!.Message, Does.Contain("OUTPUT_ROOT_INSIDE_DATABASE_RUN_ROOT"));

        // A '..' alias that normalises back into the run folder is rejected as well.
        var alias = Assert.Throws<InvalidOperationException>(() =>
            new ReleaseAbComparer().Compare(new AbCompareRequest(_database, _images,
                Path.Combine(_run, "scratch", "..", "aliased-report"),
                _releases.ReleaseA, _releases.ReleaseB)));
        Assert.That(alias!.Message, Does.Contain("OUTPUT_ROOT_INSIDE_DATABASE_RUN_ROOT"));
        Assert.That(Directory.Exists(Path.Combine(_run, "aliased-report")), Is.False);
    }

    [Test]
    [Description("TC-154-AB-06 unknown release, identical releases and a live WAL snapshot are rejected")]
    public void UnknownReleaseIdenticalReleasesAndLiveWalAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Run("report-unknown", releaseA: "SIMREL-00000000000000000000000000000000"));
        Assert.Throws<ArgumentException>(() =>
            Run("report-same", releaseA: _releases.ReleaseA, releaseB: _releases.ReleaseA));

        // A live WAL is not a certified offline snapshot: fail closed, write no report,
        // and never delete or rewrite the source sidecar.
        string wal = _database + "-wal";
        File.WriteAllBytes(wal, [1, 2, 3, 4]);
        try
        {
            var live = Assert.Throws<InvalidOperationException>(() => Run("report-live-wal"));
            Assert.That(live!.Message, Does.Contain("SOURCE_WAL_PRESENT"));
            Assert.That(File.Exists(wal), Is.True, "The tool must never delete a source WAL.");
            Assert.That(File.Exists(Path.Combine(ReportDir("report-live-wal"), "ab-manifest.json")),
                Is.False, "A rejected run must not produce a report.");
        }
        finally
        {
            File.Delete(wal);
        }
    }

    [Test]
    [Description("TC-154-AB-07 a tampered published release body is rejected by digest verification")]
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
        var failure = Assert.Throws<InvalidDataException>(() => Run("report-tampered"));
        Assert.That(failure!.Message, Does.Contain("digest").IgnoreCase);
    }
}
