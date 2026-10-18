using AOI.Domain;
using AOI.Runtime.Vision;
using AOI.Solutions;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Solutions.Tests;

/// <summary>
/// Actual A3 OpenCvInspectionProcessor is called with settings from the real
/// immutable A6 Release. No mocked quality verdict or software PLC command.
/// </summary>
[TestFixture]
public sealed class PublishedRuntimeParameterResolverTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a6-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SolutionRepository NewProduct()
    {
        var db = new SolutionRepository(Path.Combine(_root, "solutions.sqlite"));
        db.RegisterProfile(MachineProfile.SimBaseline(), "operator");
        db.CreateSolution(new ProductSolution("A6_PRODUCT", "SIM_MACHINE_01", "A6 Runtime"), "operator");
        foreach (var group in ChannelCatalog.All.GroupBy(c => c.StationId))
            db.SaveDraft("A6_PRODUCT", new StationRecipe(
                group.Key, group.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double>
                {
                    ["brightThreshold"] = 127,
                    ["minimumBrightFraction"] = 0.1
                }), 0, "operator");
        return db;
    }

    private static CameraCaptureTarget Camera(string id, int deadlineMs) =>
        new(id, "sim.png.frame-source.v1", CaptureTriggerMode.ExternalHardwareEdge,
            "Line0", deadlineMs, 1234.5, 1.25,
            new Dictionary<string, double> { ["lineBufferFrames"] = 2 });

    private static InspectionAlgorithmTarget Contour(string channel, double minArea,
        IReadOnlyDictionary<string, double>? alternative = null) =>
        new(channel, PublishedRuntimeParameterResolver.OpenCvContourId,
            alternative ?? new Dictionary<string, double>
            {
                ["minimumContourAreaPixels"] = minArea,
                ["findBrightDefects"] = 1,
                ["maximumDetections"] = 20
            },
            new Dictionary<string, string> { ["modelNote"] = "operator-supplied" });

    private static ChannelOutcome RunActualA3Contour(Mat pixels,
        ResolvedChannelInspectionSettings settings)
    {
        Assert.That(settings.Kind, Is.EqualTo(PublishedInspectionKind.OpenCvContour));
        var roi = settings.PixelRoi(pixels.Cols, pixels.Rows);
        var inspection = new OpenCvInspectionProcessor().Inspect(
            ChannelCatalog.Require(settings.ChannelId), pixels,
            InspectionSettings.OpenCvContour(
                new ContourDefectRule(settings.BinaryThreshold,
                    settings.MinimumContourAreaPixels!.Value,
                    settings.FindBrightDefects!.Value,
                    settings.MaximumDetections!.Value),
                new ImageRegion(roi.X, roi.Y, roi.Width, roi.Height)));
        return inspection.Outcome;
    }

    [Test]
    public void PublishedNewAlgorithmAndRoiProduceRealA3Ng_WhileInFlightReleaseStaysGood()
    {
        var db = NewProduct();
        var first = SolutionParameterEditor.SetCaptureTarget(
            db, "A6_PRODUCT", Camera("HIK-M-2", 2500), 1, "operator");
        Assert.That(first.Revision, Is.EqualTo(2));
        // Existing station tunable is still a supported default if the per-channel
        // contour plugin supplies an area but no explicit binaryThreshold.
        var overrides = new Dictionary<string, double>(first.Recipe.Tunables)
        {
            ["brightThreshold"] = 210
        };
        var changed = db.SaveDraft("A6_PRODUCT",
            first.Recipe with { Tunables = overrides }, 2, "operator");
        Assert.That(changed.Revision, Is.EqualTo(3));
        var originalDraft = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", Contour("CH06", minArea: 200), 3, "operator");
        Assert.That(originalDraft.Revision, Is.EqualTo(4));
        var releaseA = db.Publish("A6_PRODUCT", "operator");
        var itemA = WorkpieceIdentity.New("REPEAT-BARCODE", 1, releaseA.ReleaseId);
        var bindingA = new PublishedParameterSnapshot(releaseA, 1).Bind(itemA);
        var planA = PublishedRuntimeParameterResolver.Resolve(bindingA, "HIK-M-2");
        Assert.That(planA.Capture.FrameDeadline, Is.EqualTo(TimeSpan.FromMilliseconds(2500)));
        Assert.That(planA.Capture.PlcSignalAddress, Is.EqualTo("D1011"));
        Assert.That(planA.Capture.TriggerMode, Is.EqualTo(CaptureTriggerMode.ExternalHardwareEdge));
        Assert.That(planA.Capture.ChannelIds, Is.EquivalentTo(new[] { "CH06" }));
        Assert.That(planA.Capture.Camera.State, Is.EqualTo(CameraConfigurationState.ReadbackPending));
        Assert.That(planA.Inspections[0].BinaryThreshold, Is.EqualTo(210));
        Assert.That(planA.Inspections[0].MinimumContourAreaPixels, Is.EqualTo(200));

        // Operator changes its algorithm + ROI. No active item is updated.
        var changedB = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", Contour("CH06", minArea: 8), 4, "operator");
        var withRoi = new Dictionary<string, double>(changedB.Recipe.Tunables)
        {
            ["roi.CH06.x"] = 0,
            ["roi.CH06.y"] = 0,
            ["roi.CH06.width"] = 0.5,
            ["roi.CH06.height"] = 0.5,
            ["roi.CH06.revision"] = 1
        };
        db.SaveDraft("A6_PRODUCT", changedB.Recipe with { Tunables = withRoi },
            changedB.Revision, "operator");
        var releaseB = db.Publish("A6_PRODUCT", "operator");
        Assert.That(releaseB.ReleaseId, Is.Not.EqualTo(releaseA.ReleaseId));
        Assert.That(releaseB.ContentSha256, Is.Not.EqualTo(releaseA.ContentSha256));
        Assert.That(db.RequireRelease(releaseA.ReleaseId).ContentSha256,
            Is.EqualTo(releaseA.ContentSha256));
        var itemB = WorkpieceIdentity.New("REPEAT-BARCODE", 2, releaseB.ReleaseId);
        var bindingB = new PublishedParameterSnapshot(releaseB, 2).Bind(itemB);
        var planB = PublishedRuntimeParameterResolver.Resolve(bindingB, "HIK-M-2");

        using var actualPixels = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(actualPixels, new Rect(5, 5, 10, 10), Scalar.White, -1);
        var before = RunActualA3Contour(actualPixels, planA.Inspections[0]);
        var after = RunActualA3Contour(actualPixels, planB.Inspections[0]);
        Assert.That(before, Is.EqualTo(ChannelOutcome.Ok),
            "Release A minimum contour area excludes the same small real defect.");
        Assert.That(after, Is.EqualTo(ChannelOutcome.Ng),
            "Release B smaller published minimum area detects that defect.");
        Assert.That(planB.Inspections[0].PixelRoi(64, 64),
            Is.EqualTo(new PixelInspectionRoi(0, 0, 32, 32)));
        Assert.That(PublishedRuntimeParameterResolver.Resolve(bindingA, "HIK-M-2")
            .Inspections[0].MinimumContourAreaPixels, Is.EqualTo(200));
        Assert.Throws<InvalidOperationException>(() =>
            new PublishedParameterSnapshot(releaseB, 2).Bind(itemA));
    }

    [Test]
    public void NinePhysicalTriggersProduceElevenScopedInspectionSettings()
    {
        var db = NewProduct();
        var release = db.Publish("A6_PRODUCT", "operator");
        var item = WorkpieceIdentity.New("ELEVEN", 1, release.ReleaseId);
        var bound = new PublishedParameterSnapshot(release, 1).Bind(item);
        var plans = ChannelCatalog.All.Select(c => c.PhysicalDeviceId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => PublishedRuntimeParameterResolver.Resolve(bound, id)).ToArray();
        Assert.That(plans, Has.Length.EqualTo(9));
        Assert.That(plans.Sum(p => p.Inspections.Count), Is.EqualTo(11));
        Assert.That(plans.SelectMany(p => p.Inspections).Select(c => c.ChannelId).Distinct().Count(),
            Is.EqualTo(11));
        Assert.That(plans.Where(p => p.Inspections.Count == 2).Select(p => p.Capture.PhysicalDeviceId),
            Is.EquivalentTo(new[] { "LINE-M-2", "LINE-S-8" }));
        Assert.That(plans.Single(p => p.Capture.PhysicalDeviceId == "LINE-S-8")
            .Capture.FrameDeadline, Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(plans.Single(p => p.Capture.PhysicalDeviceId == "HIK-M-2")
            .Capture.FrameDeadline, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(plans.SelectMany(p => p.Inspections).All(
            i => i.Kind == PublishedInspectionKind.LegacyBrightness &&
                 i.MinimumBrightFraction == 0.1 && i.BinaryThreshold == 127),
            Is.True);
    }

    [Test]
    public void LineCaptureTimeoutSharedWhileChildRoiAndAlgorithmRemainIndependent()
    {
        var db = NewProduct();
        var target = SolutionParameterEditor.SetCaptureTarget(
            db, "A6_PRODUCT", Camera("LINE-S-8", 17000), 1, "operator");
        var a = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", Contour("CH08", 10), target.Revision, "operator");
        var b = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", Contour("CH09", 350), a.Revision, "operator");
        var roiValues = new Dictionary<string, double>(b.Recipe.Tunables)
        {
            ["roi.CH08.x"] = 0, ["roi.CH08.y"] = 0,
            ["roi.CH08.width"] = 0.5, ["roi.CH08.height"] = 1,
            ["roi.CH08.revision"] = 1,
            ["roi.CH09.x"] = 0.5, ["roi.CH09.y"] = 0,
            ["roi.CH09.width"] = 0.5, ["roi.CH09.height"] = 1,
            ["roi.CH09.revision"] = 1
        };
        db.SaveDraft("A6_PRODUCT", b.Recipe with { Tunables = roiValues },
            b.Revision, "operator");
        var release = db.Publish("A6_PRODUCT", "operator");
        var bound = new PublishedParameterSnapshot(release, 1)
            .Bind(WorkpieceIdentity.New("LINE", 1, release.ReleaseId));
        var plan = PublishedRuntimeParameterResolver.Resolve(bound, "LINE-S-8");
        Assert.That(plan.Capture.FrameDeadline, Is.EqualTo(TimeSpan.FromSeconds(17)));
        Assert.That(plan.Capture.ChannelIds, Is.EquivalentTo(new[] { "CH08", "CH09" }));
        Assert.That(plan.Inspections[0].MinimumContourAreaPixels, Is.EqualTo(10));
        Assert.That(plan.Inspections[1].MinimumContourAreaPixels, Is.EqualTo(350));
        Assert.That(plan.Inspections[0].PixelRoi(64, 40).Width, Is.EqualTo(32));
        Assert.That(plan.Inspections[1].PixelRoi(64, 40).X, Is.EqualTo(32));
    }

    [Test]
    public void UnsupportedPublishedPluginOrIgnoredNumericOptionCannotBecomeGood()
    {
        var db = NewProduct();
        var custom = new InspectionAlgorithmTarget("CH06", "private.model.uninstalled",
            new Dictionary<string, double> { ["minScore"] = 0.5 },
            new Dictionary<string, string>());
        var draft = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", custom, 1, "operator");
        var release = db.Publish("A6_PRODUCT", "operator");
        var bound = new PublishedParameterSnapshot(release, 1)
            .Bind(WorkpieceIdentity.New("CUSTOM", 1, release.ReleaseId));
        Assert.Throws<NotSupportedException>(() =>
            PublishedRuntimeParameterResolver.Resolve(bound, "HIK-M-2"));

        var typo = Contour("CH06", 10, new Dictionary<string, double>
        {
            ["minimumContourAreaPixel"] = 10
        });
        var next = SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", typo, draft.Revision, "operator");
        var release2 = db.Publish("A6_PRODUCT", "operator");
        var bound2 = new PublishedParameterSnapshot(release2, 2)
            .Bind(WorkpieceIdentity.New("CUSTOM", 2, release2.ReleaseId));
        Assert.Throws<InvalidDataException>(() =>
            PublishedRuntimeParameterResolver.Resolve(bound2, "HIK-M-2"));
        Assert.That(next.Revision, Is.EqualTo(draft.Revision + 1));
    }

    [Test]
    public void WrongSoftwareTriggerInvalidThresholdAndConflictingAliasesAreRejected()
    {
        var db = NewProduct();
        var camera = Camera("HIK-M-2", 5000) with
        {
            TriggerMode = CaptureTriggerMode.SoftwareCommand
        };
        var step = SolutionParameterEditor.SetCaptureTarget(
            db, "A6_PRODUCT", camera, 1, "operator");
        var bad = Contour("CH06", 10, new Dictionary<string, double>
        {
            ["binaryThreshold"] = 80,
            ["brightThreshold"] = 210,
            ["minimumContourAreaPixels"] = 10
        });
        SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT", bad, step.Revision, "operator");
        var released = db.Publish("A6_PRODUCT", "operator");
        var bound = new PublishedParameterSnapshot(released, 1)
            .Bind(WorkpieceIdentity.New("BAD", 1, released.ReleaseId));
        Assert.Throws<NotSupportedException>(() =>
            PublishedRuntimeParameterResolver.Resolve(bound, "HIK-M-2"));

        var fixedCamera = SolutionParameterEditor.SetCaptureTarget(
            db, "A6_PRODUCT",
            camera with { TriggerMode = CaptureTriggerMode.ExternalHardwareEdge },
            3, "operator");
        var release2 = db.Publish("A6_PRODUCT", "operator");
        var bound2 = new PublishedParameterSnapshot(release2, 2)
            .Bind(WorkpieceIdentity.New("BAD", 2, release2.ReleaseId));
        Assert.Throws<InvalidDataException>(() =>
            PublishedRuntimeParameterResolver.Resolve(bound2, "HIK-M-2"));
        Assert.That(fixedCamera.Revision, Is.EqualTo(4));

        // A3 does not silently accept an unbounded or fractional threshold.
        SolutionParameterEditor.SetInspectionTarget(
            db, "A6_PRODUCT",
            Contour("CH06", 1, new Dictionary<string, double>
            {
                ["binaryThreshold"] = 100.5,
                ["minimumContourAreaPixels"] = 1
            }), 4, "operator");
        var release3 = db.Publish("A6_PRODUCT", "operator");
        var bound3 = new PublishedParameterSnapshot(release3, 3)
            .Bind(WorkpieceIdentity.New("BAD", 3, release3.ReleaseId));
        Assert.Throws<InvalidDataException>(() =>
            PublishedRuntimeParameterResolver.ResolveChannel(bound3, "CH06", "HIK-M-2"));
    }
}
