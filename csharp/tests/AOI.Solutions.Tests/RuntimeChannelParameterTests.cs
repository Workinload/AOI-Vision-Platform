using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Solutions.Tests;

[TestFixture]
public sealed class RuntimeChannelParameterTests
{
    private string _folder = "";

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "aoi-a6-params-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private SolutionRepository StartSolution()
    {
        var repository = new SolutionRepository(Path.Combine(_folder, "solutions.sqlite"));
        repository.RegisterProfile(MachineProfile.SimBaseline(), "operator");
        repository.CreateSolution(new ProductSolution("PRODUCT-A", "SIM_MACHINE_01", "Product A"), "operator");
        foreach (var station in ChannelCatalog.All.GroupBy(c => c.StationId))
            repository.SaveDraft("PRODUCT-A", new StationRecipe(station.Key,
                station.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double>
                {
                    ["brightThreshold"] = 127, ["minimumBrightFraction"] = 0.1
                }), 0, "operator");
        return repository;
    }

    private static CameraCaptureTarget Target(string camera) => new(
        camera, "sim.png.frame-source.v1", CaptureTriggerMode.ExternalHardwareEdge,
        "Line0", 10000, 4500, 1.5,
        new Dictionary<string, double> { ["packetSize"] = 1500 });

    private static InspectionAlgorithmTarget Inspector(string channel, string algorithm,
        double minimumFraction = 0.1) => new(
        channel, algorithm,
        new Dictionary<string, double>
        {
            ["brightThreshold"] = 127,
            ["minimumBrightFraction"] = minimumFraction
        },
        new Dictionary<string, string> { ["diagnosticLabel"] = "user-tunable" });

    private void InsertSavedCoreRoi(string channel, double x, double y, double width, double height)
    {
        string path = Path.Combine(_folder, "master", "sim-config.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sim_roi_drafts(
              channel_id TEXT PRIMARY KEY,x REAL,y REAL,width REAL,height REAL,
              revision INTEGER,actor TEXT,edited_utc TEXT);
            INSERT INTO sim_roi_drafts VALUES($id,$x,$y,$w,$h,1,'operator',$utc);
            """;
        cmd.Parameters.AddWithValue("$id", channel);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private sealed class OpenCvBrightness : IRecipeInspectionProcessor<Mat, ChannelOutcome>
    {
        public string AlgorithmId { get; }
        public OpenCvBrightness(string algorithmId) => AlgorithmId = algorithmId;

        public ChannelOutcome Inspect(Mat pixels, ChannelExecutionParameters config)
        {
            var area = config.InspectionRoi.ToPixels(pixels.Cols, pixels.Rows);
            using var cropped = new Mat(pixels,
                new Rect(area.X, area.Y, area.Width, area.Height));
            using var thresholded = new Mat();
            byte level = checked((byte)config.Algorithm.NumericParameters["brightThreshold"]);
            Cv2.Threshold(cropped, thresholded, level, 255, ThresholdTypes.Binary);
            double fraction = Cv2.CountNonZero(thresholded) / ((double)area.Width * area.Height);
            return fraction >= config.Algorithm.NumericParameters["minimumBrightFraction"]
                ? ChannelOutcome.Ok : ChannelOutcome.Ng;
        }
    }

    [Test]
    public void WorkpieceBindsOwnReleaseAndNativePixelProcessorAfterDraftAndEpochSwitch()
    {
        var repo = StartSolution();
        var legacy = repo.Publish("PRODUCT-A", "operator");
        // Old SHA must survive adding optional A6 parameter properties.
        var legacyDigest = legacy.ContentSha256;
        var boundA = new PublishedParameterSnapshot(legacy, 1).Bind(
            WorkpieceIdentity.New("SAME-BARCODE", 1, legacy.ReleaseId));

        var edited = SolutionParameterEditor.SetCaptureTarget(
            repo, "PRODUCT-A", Target("HIK-M-2"), 1, "operator");
        Assert.That(edited.Revision, Is.EqualTo(2));
        edited = SolutionParameterEditor.SetInspectionTarget(repo, "PRODUCT-A",
            Inspector("CH06", "example.brightness.v1"), 2, "operator");
        Assert.That(edited.Revision, Is.EqualTo(3));
        InsertSavedCoreRoi("CH06", 0, 0, 0.125, 0.125);
        var imported = RoiReleaseWorkflow.ImportCoreDraft(
            repo, _folder, "PRODUCT-A", "CH06", 1, 3, "operator");
        Assert.That(imported.StationDraftRevision, Is.EqualTo(4));
        var editedView = SolutionParameterEditor.ReadDraft(repo, "PRODUCT-A", "CH06");
        Assert.That(editedView.CaptureTarget!.TriggerMode, Is.EqualTo(CaptureTriggerMode.ExternalHardwareEdge));
        Assert.That(editedView.InspectionTarget!.AlgorithmId, Is.EqualTo("example.brightness.v1"));
        Assert.That(editedView.InspectionRoi.Width, Is.EqualTo(0.125));
        Assert.That(editedView.StationTunables["brightThreshold"], Is.EqualTo(127));

        var revised = repo.Publish("PRODUCT-A", "operator");
        Assert.That(revised.ReleaseId, Is.Not.EqualTo(legacy.ReleaseId));
        Assert.That(repo.RequireRelease(legacy.ReleaseId).ContentSha256, Is.EqualTo(legacyDigest));
        var boundB = new PublishedParameterSnapshot(revised, 2).Bind(
            WorkpieceIdentity.New("SAME-BARCODE", 2, revised.ReleaseId));
        var aParams = boundA.ForChannel("CH06", "HIK-M-2");
        var bParams = boundB.ForChannel("CH06", "HIK-M-2");
        Assert.That(aParams.Algorithm.AlgorithmId, Is.EqualTo("legacy.sim.brightness.v1"));
        Assert.That(aParams.Camera.State, Is.EqualTo(CameraConfigurationState.TargetUnspecified));
        Assert.That(bParams.Algorithm.AlgorithmId, Is.EqualTo("example.brightness.v1"));
        Assert.That(bParams.Camera.State, Is.EqualTo(CameraConfigurationState.ReadbackPending));

        var plugins = new RecipeInspectionDispatcher<Mat, ChannelOutcome>(
            [new OpenCvBrightness("legacy.sim.brightness.v1"),
             new OpenCvBrightness("example.brightness.v1")]);
        using var frame = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(frame, new Rect(8, 8, 48, 48), Scalar.White, -1);
        var oldOutcome = plugins.Inspect(frame, boundA, "CH06", "HIK-M-2");
        var nextOutcome = plugins.Inspect(frame, boundB, "CH06", "HIK-M-2");
        Assert.That(oldOutcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(nextOutcome, Is.EqualTo(ChannelOutcome.Ng));
        Assert.Throws<InvalidOperationException>(() =>
            new PublishedParameterSnapshot(revised, 2).Bind(boundA.Workpiece));
        Assert.That(boundA.ForChannel("CH06", "HIK-M-2").InspectionRoi,
            Is.EqualTo(NormalizedInspectionRoi.FullFrame));
        // Even if a mutable input dictionary changes, snapshot returns a read-only copy.
        Assert.That(bParams.Algorithm.NumericParameters["minimumBrightFraction"], Is.EqualTo(0.1));
    }

    [Test]
    public void OneLineCameraTargetCoversTwoLogicalChannelsButNoCrossBinding()
    {
        var repo = StartSolution();
        SolutionParameterEditor.SetCaptureTarget(
            repo, "PRODUCT-A", Target("LINE-S-8"), 1, "operator");
        var published = repo.Publish("PRODUCT-A", "operator");
        var bound = new PublishedParameterSnapshot(published, 1).Bind(
            WorkpieceIdentity.New("LINE", 1, published.ReleaseId));
        var physical = bound.ForPhysicalDevice("LINE-S-8");
        Assert.That(physical.ChannelIds, Is.EquivalentTo(new[] { "CH08", "CH09" }));
        Assert.That(physical.PlcSignalAddress, Is.EqualTo("D1013"));
        Assert.That(physical.Camera.Target!.AdapterId, Is.EqualTo("sim.png.frame-source.v1"));
        Assert.That(bound.ForChannel("CH08", "LINE-S-8").Camera.Target!.AdapterId,
            Is.EqualTo(bound.ForChannel("CH09", "LINE-S-8").Camera.Target!.AdapterId));
        Assert.That(bound.ForChannel("CH08", "LINE-S-8").Camera.Target!.TriggerMode,
            Is.EqualTo(bound.ForChannel("CH09", "LINE-S-8").Camera.Target!.TriggerMode));
        Assert.Throws<InvalidOperationException>(() => bound.ForChannel("CH06", "LINE-S-8"));
        Assert.Throws<InvalidDataException>(() =>
            SolutionParameterEditor.SetCaptureTarget(repo, "PRODUCT-A",
                Target("HIK-M-2") with { ExposureMicroseconds = -1 }, 1, "operator"));
    }

    [Test]
    public void ReadbackIsSeparateFromTargetAndNeverClaimsFieldAcceptance()
    {
        var repo = StartSolution();
        SolutionParameterEditor.SetCaptureTarget(
            repo, "PRODUCT-A", Target("HIK-M-2"), 1, "operator");
        var release = repo.Publish("PRODUCT-A", "operator");
        var bound = new PublishedParameterSnapshot(release, 1).Bind(
            WorkpieceIdentity.New("READBACK", 1, release.ReleaseId));
        var target = bound.ForChannel("CH06", "HIK-M-2").Camera.Target!;
        Assert.That(bound.ForChannel("CH06", "HIK-M-2").Camera.State,
            Is.EqualTo(CameraConfigurationState.ReadbackPending));
        var equal = new CameraConfigurationReadback(target.PhysicalDeviceId,
            target.AdapterId, target.TriggerMode, target.TriggerInput,
            target.FrameTimeoutMs, target.ExposureMicroseconds, target.GainDb,
            target.DeviceParameters, DateTimeOffset.UtcNow, true);
        Assert.That(bound.ForChannel("CH06", "HIK-M-2", equal).Camera.State,
            Is.EqualTo(CameraConfigurationState.MatchObserved));
        Assert.That(bound.ForChannel("CH06", "HIK-M-2",
            equal with { ExposureMicroseconds = 123 }).Camera.State,
            Is.EqualTo(CameraConfigurationState.MismatchObserved));
        Assert.That(bound.ForChannel("CH06", "HIK-M-2").Camera.State,
            Is.EqualTo(CameraConfigurationState.ReadbackPending));
    }

    [Test]
    public void StaleDraftAndUnknownPluginAreRejectedNotFabricatedGood()
    {
        var repo = StartSolution();
        SolutionParameterEditor.SetInspectionTarget(repo, "PRODUCT-A",
            Inspector("CH06", "custom.not-installed.v1"), 1, "operator");
        Assert.Throws<InvalidOperationException>(() =>
            SolutionParameterEditor.SetInspectionTarget(repo, "PRODUCT-A",
                Inspector("CH06", "another.plugin"), 1, "operator"));
        var published = repo.Publish("PRODUCT-A", "operator");
        var bind = new PublishedParameterSnapshot(published, 1).Bind(
            WorkpieceIdentity.New("P", 1, published.ReleaseId));
        var router = new RecipeInspectionDispatcher<Mat, ChannelOutcome>(
            [new OpenCvBrightness("legacy.sim.brightness.v1")]);
        using var frame = new Mat(8, 8, MatType.CV_8UC1, Scalar.White);
        Assert.Throws<NotSupportedException>(() =>
            router.Inspect(frame, bind, "CH06", "HIK-M-2"));
    }
}
