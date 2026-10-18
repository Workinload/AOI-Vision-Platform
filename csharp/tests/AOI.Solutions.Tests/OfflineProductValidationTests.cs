using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

/// <summary>
/// OFFLINE operator entry: installed SIM plugins/devices only, ranges, real image size
/// association for ROI, and "validate strictly before any immutable release row".
/// Low-level SolutionRepository behaviour is deliberately not changed; a legacy/imported
/// draft that bypasses the operator gate must still be refused by OfflineProductManager.
/// </summary>
[TestFixture]
public sealed class OfflineProductValidationTests
{
    private string _root = "";
    private string _db = "";
    private OfflineProductManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-offline-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "solutions.sqlite");
        _manager = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        _manager.EnsureSimProfile();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Seed(string id = "S1") => _manager.CreateSimSolution(id, "Offline " + id);

    private static IReadOnlyDictionary<string, RoiFrameSize> Frames(int width = 64, int height = 64) =>
        ChannelCatalog.All.ToDictionary(c => c.ChannelId,
            _ => new RoiFrameSize(width, height), StringComparer.Ordinal);

    private static StationRecipe WithTunables(StationRecipe recipe,
        params (string Key, double Value)[] values)
    {
        var tunables = new Dictionary<string, double>(recipe.Tunables, StringComparer.Ordinal);
        foreach (var (key, value) in values) tunables[key] = value;
        return recipe with { Tunables = tunables };
    }

    private static StationRecipe WithRoi(StationRecipe recipe, string channelId,
        double x, double y, double width, double height, long revision = 1)
    {
        var tunables = new Dictionary<string, double>(recipe.Tunables, StringComparer.Ordinal);
        string prefix = "roi." + channelId + ".";
        tunables[prefix + "x"] = x;
        tunables[prefix + "y"] = y;
        tunables[prefix + "width"] = width;
        tunables[prefix + "height"] = height;
        tunables[prefix + "revision"] = revision;
        return recipe with { Tunables = tunables };
    }

    private StationRecipe StationDraft(string solutionId = "S1", string stationId = "ST05") =>
        _manager.Repository.GetDraft(solutionId, stationId)!.Recipe;

    [Test]
    public void UnknownInspectionPluginFailsClosedAndWritesNoRelease()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var recipe = draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06", "custom.not-installed.v1",
                    new Dictionary<string, double>(), new Dictionary<string, string>())
            ]
        };
        _manager.Repository.SaveDraft("S1", recipe, draft.Revision, "legacy-import");

        var result = _manager.Publish("S1");
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Release, Is.Null);
        Assert.That(result.Message, Does.Contain("INSPECTION_PLUGIN_NOT_INSTALLED"));
        Assert.That(_manager.ListReleases(), Is.Empty, "no immutable release may be written");
    }

    [Test]
    public void OutOfRangeThresholdFailsClosedAndWritesNoRelease()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var bad = WithTunables(draft.Recipe, ("brightThreshold", 255));
        Assert.Throws<InvalidDataException>(() =>
                _manager.SaveStation("S1", bad, draft.Revision, Frames()),
            "operator SaveStation must reject an out-of-range SIM rule");
        Assert.That(_manager.Repository.GetDraft("S1", "ST05")!.Revision, Is.EqualTo(draft.Revision));

        // A legacy/imported draft bypassing the operator gate must still not publish.
        _manager.Repository.SaveDraft("S1", bad, draft.Revision, "legacy-import");
        var result = _manager.Publish("S1", Frames());
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("BRIGHTNESS_RULE_INVALID"));
        Assert.That(_manager.ListReleases(), Is.Empty);
    }

    [Test]
    public void RoiNeedsValidBoundsAndVerifiedFrameBeforeAnyRelease()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;

        var outside = WithRoi(draft.Recipe, "CH06", 0.9, 0.1, 0.3, 0.2);
        Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", outside, draft.Revision, Frames()));
        Assert.That(_manager.Repository.GetDraft("S1", "ST05")!.Revision, Is.EqualTo(draft.Revision));

        var roi = WithRoi(draft.Recipe, "CH06", 0.1, 0.1, 0.5, 0.5);
        Assert.Throws<InvalidDataException>(() =>
                _manager.SaveStation("S1", roi, draft.Revision, verifiedFrames: null),
            "a new ROI must be associated with a verified real image size");
        Assert.That(_manager.Repository.GetDraft("S1", "ST05")!.Revision, Is.EqualTo(draft.Revision));

        var saved = _manager.SaveStation("S1", roi, draft.Revision, Frames());
        Assert.That(saved.Revision, Is.EqualTo(draft.Revision + 1));
        Assert.That(RoiReleaseWorkflow.ReadStationRoi(saved.Recipe, "CH06")!.Bounds,
            Is.EqualTo(new NormalizedInspectionRoi(0.1, 0.1, 0.5, 0.5)));
        Assert.That(RoiReleaseWorkflow.ReadStationRoi(saved.Recipe, "CH06")!.Bounds
            .ToPixels(64, 64), Is.EqualTo(new PixelInspectionRoi(6, 6, 33, 33)));

        var rejected = _manager.Publish("S1", verifiedFrames: null);
        Assert.That(rejected.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(rejected.Message, Does.Contain("ROI_IMAGE_SIZE_UNVERIFIED"));
        Assert.That(_manager.ListReleases(), Is.Empty);

        var accepted = _manager.Publish("S1", Frames());
        Assert.That(accepted.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), accepted.Message);
        var release = accepted.Release!;
        Assert.That(release.ReleaseId, Does.StartWith("SIMREL-"));
        Assert.That(release.ContentSha256, Has.Length.EqualTo(64));
        Assert.That(RoiReleaseWorkflow.ReadPublishedRoi(release, "CH06")!.Bounds
            .ToPixels(64, 64), Is.EqualTo(new PixelInspectionRoi(6, 6, 33, 33)));

        // Reopen through a brand new manager/connection: the immutable row is real.
        var reopened = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST")
            .RequirePublished(release.ReleaseId);
        reopened.Verify();
        Assert.That(reopened.ContentSha256, Is.EqualTo(release.ContentSha256));
    }

    [Test]
    public void UnknownCaptureDeviceOrAdapterFailsClosed()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var unknownDevice = draft.Recipe with
        {
            CaptureTargets =
            [
                new CameraCaptureTarget("HIK-X-9", "sim.png.frame-source.v1",
                    CaptureTriggerMode.ExternalHardwareEdge, null, 1000, null, null,
                    new Dictionary<string, double>())
            ]
        };
        Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", unknownDevice, draft.Revision, Frames()));

        var unknownAdapter = draft.Recipe with
        {
            CaptureTargets =
            [
                new CameraCaptureTarget("HIK-M-2", "vendor.sdk.v1",
                    CaptureTriggerMode.ExternalHardwareEdge, null, 1000, null, null,
                    new Dictionary<string, double>())
            ]
        };
        var error = Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", unknownAdapter, draft.Revision, Frames()));
        Assert.That(error!.Message, Does.Contain("CAPTURE_ADAPTER_NOT_INSTALLED"));
        Assert.That(_manager.Repository.GetDraft("S1", "ST05")!.Revision, Is.EqualTo(draft.Revision));
    }

    [Test]
    public void ContourPluginRangesAndUnknownKeysFailClosed()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var badThreshold = draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06", OfflineRecipeValidator.OpenCvContourAlgorithm,
                    new Dictionary<string, double> { ["binaryThreshold"] = 0 },
                    new Dictionary<string, string>())
            ]
        };
        Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", badThreshold, draft.Revision, Frames()));

        var badDetections = draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06", OfflineRecipeValidator.OpenCvContourAlgorithm,
                    new Dictionary<string, double> { ["maximumDetections"] = 4096 },
                    new Dictionary<string, string>())
            ]
        };
        Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", badDetections, draft.Revision, Frames()));

        var unknownKey = draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06", OfflineRecipeValidator.OpenCvContourAlgorithm,
                    new Dictionary<string, double> { ["notAParameter"] = 1 },
                    new Dictionary<string, string>())
            ]
        };
        var issue = Assert.Throws<InvalidDataException>(() =>
            _manager.SaveStation("S1", unknownKey, draft.Revision, Frames()));
        Assert.That(issue!.Message, Does.Contain("INSPECTION_PARAMETER_UNKNOWN"));
        Assert.That(_manager.Repository.GetDraft("S1", "ST05")!.Revision, Is.EqualTo(draft.Revision));
    }

    [Test]
    public void UnsupportedLegacyTextParameterFailsClosedAtPublish()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var recipe = draft.Recipe with
        {
            InspectionTargets =
            [
                new InspectionAlgorithmTarget("CH06",
                    OfflineRecipeValidator.LegacyBrightnessAlgorithm,
                    new Dictionary<string, double> { ["brightThreshold"] = 127 },
                    new Dictionary<string, string> { ["model"] = "unexpected" })
            ]
        };
        _manager.Repository.SaveDraft("S1", recipe, draft.Revision, "legacy-import");
        var result = _manager.Publish("S1", Frames());
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Rejected));
        Assert.That(result.Message, Does.Contain("INSPECTION_TEXT_PARAMETER_UNSUPPORTED"));
        Assert.That(_manager.ListReleases(), Is.Empty);
    }

    [Test]
    public void UnchangedRoiKeepsEarlierFrameAssociationOnParameterOnlySave()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var roi = WithRoi(draft.Recipe, "CH06", 0.1, 0.1, 0.5, 0.5);
        var saved = _manager.SaveStation("S1", roi, draft.Revision, Frames());
        var edited = WithTunables(saved.Recipe, ("brightThreshold", 200));
        // No verified frame is supplied now, but the ROI itself is unchanged.
        var next = _manager.SaveStation("S1", edited, saved.Revision, verifiedFrames: null);
        Assert.That(next.Revision, Is.EqualTo(saved.Revision + 1));
        Assert.That(RoiReleaseWorkflow.ReadStationRoi(next.Recipe, "CH06")!.SourceRevision,
            Is.EqualTo(1));
        Assert.That(next.Recipe.Tunables["brightThreshold"], Is.EqualTo(200));
    }

    [Test]
    public void DraftRevisionAndReleaseSurviveManagerReopenWithCas()
    {
        Seed();
        var draft = _manager.Repository.GetDraft("S1", "ST05")!;
        var saved = _manager.SaveStation("S1",
            WithTunables(draft.Recipe, ("brightThreshold", 180)), draft.Revision, Frames());
        Assert.That(saved.Revision, Is.EqualTo(2));

        var reopened = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        var persisted = reopened.Repository.GetDraft("S1", "ST05")!;
        Assert.That(persisted.Revision, Is.EqualTo(2));
        Assert.That(persisted.Recipe.Tunables["brightThreshold"], Is.EqualTo(180));
        Assert.Throws<InvalidOperationException>(() =>
            reopened.SaveStation("S1", saved.Recipe, expectedRevision: 1, verifiedFrames: Frames()));

        var release = reopened.Publish("S1", Frames());
        Assert.That(release.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), release.Message);
        var again = OfflineProductManager.Open(_db, "SIM_OPERATOR_TEST");
        Assert.That(again.ListReleases().Single().ContentSha256,
            Is.EqualTo(release.Release!.ContentSha256));
    }

    [Test]
    public void ValidSimBaselineStillPublishesWithoutRoiOrExplicitPlugins()
    {
        Seed();
        var result = _manager.Publish("S1", Frames());
        Assert.That(result.Outcome, Is.EqualTo(OfflinePublishOutcome.Published), result.Message);
        Assert.That(result.Release!.Manifest.Stations.Count, Is.EqualTo(7));
        Assert.That(_manager.ListReleases(), Has.Count.EqualTo(1));
    }
}
