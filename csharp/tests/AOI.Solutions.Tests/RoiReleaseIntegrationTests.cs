using AOI.Domain;
using AOI.Solutions;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Solutions.Tests;

/// <summary>
/// A0 Core ROI WAL schema -> A6 station draft -> published hash -> native OpenCV
/// pixel probe -> dual-node next-epoch activation. No test impersonates REAL IO.
/// </summary>
[TestFixture]
public sealed class RoiReleaseIntegrationTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-roi-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SolutionRepository NewProduct()
    {
        var repo = new SolutionRepository(Path.Combine(_root, "solutions.sqlite"));
        repo.RegisterProfile(MachineProfile.SimBaseline(), "A6-operator");
        repo.CreateSolution(new ProductSolution("RoiDemo", "SIM_MACHINE_01", "Same Pixels A-B"), "A6-operator");
        foreach (var group in ChannelCatalog.All.GroupBy(c => c.StationId))
            repo.SaveDraft("RoiDemo", new StationRecipe(group.Key,
                group.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double>
                {
                    ["brightThreshold"] = 127,
                    ["minimumBrightFraction"] = 0.1
                }), 0, "A6-operator");
        return repo;
    }

    private void SaveOperatorRoi(string channelId, long revision,
        double x, double y, double width, double height)
    {
        var def = ChannelCatalog.Require(channelId);
        string node = def.OwnerRole == MachineRole.Master ? "master" : "slave";
        string folder = Path.Combine(_root, node);
        Directory.CreateDirectory(folder);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "sim-config.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sim_roi_drafts(
              channel_id TEXT PRIMARY KEY, x REAL NOT NULL, y REAL NOT NULL,
              width REAL NOT NULL, height REAL NOT NULL, revision INTEGER NOT NULL,
              actor TEXT NOT NULL, edited_utc TEXT NOT NULL);
            INSERT INTO sim_roi_drafts(channel_id,x,y,width,height,revision,actor,edited_utc)
            VALUES($channel,$x,$y,$width,$height,$revision,$actor,$utc)
            ON CONFLICT(channel_id) DO UPDATE SET
              x=excluded.x,y=excluded.y,width=excluded.width,
              height=excluded.height,revision=excluded.revision,
              actor=excluded.actor,edited_utc=excluded.edited_utc;
            """;
        cmd.Parameters.AddWithValue("$channel", channelId);
        cmd.Parameters.AddWithValue("$x", x);
        cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$width", width);
        cmd.Parameters.AddWithValue("$height", height);
        cmd.Parameters.AddWithValue("$revision", revision);
        cmd.Parameters.AddWithValue("$actor", "GUI-A6-test");
        cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void Ch06_ExistingPng_GoodToNg_AfterExplicitImportPreviewPublishAndNewEpoch()
    {
        var repo = NewProduct();
        var original = repo.Publish("RoiDemo", "operator");
        string sample = Path.Combine(_root, "same-ch06.png");
        RoiPixelPreview.WriteSimAreaExample(sample);

        // A0 64x64 frame has a bright 48x48 middle square. Original full-frame
        // inspection is GOOD; the 8x8 black corner is NG under the SAME rule.
        var oldStation = RoiReleaseWorkflow.RequireStation(original, "CH06");
        var initial = RoiPixelPreview.Measure(sample, oldStation, "CH06");
        Assert.That(initial.SourceWidth, Is.EqualTo(64));
        Assert.That(initial.SourceHeight, Is.EqualTo(64));
        Assert.That(initial.Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(initial.BrightFraction, Is.EqualTo(2304.0 / 4096.0).Within(1e-9));

        var inFlightBeforeEdit = WorkpieceIdentity.New("SAME", 1, original.ReleaseId);
        SaveOperatorRoi("CH06", 1, 0, 0, 0.125, 0.125);
        var imported = RoiReleaseWorkflow.ImportCoreDraft(repo, _root, "RoiDemo",
            "CH06", expectedSourceRevision: 1, expectedStationRevision: 1, "operator");
        Assert.That(imported.SourceRoiRevision, Is.EqualTo(1));
        Assert.That(imported.StationDraftRevision, Is.EqualTo(2));
        var staged = repo.GetDraft("RoiDemo", "ST05")!;
        Assert.That(staged.Recipe.Tunables["brightThreshold"], Is.EqualTo(127));
        Assert.That(staged.Recipe.Tunables["minimumBrightFraction"], Is.EqualTo(0.1));

        var preview = RoiPixelPreview.Compare(sample, oldStation, staged.Recipe, "CH06");
        Assert.That(preview.Before.Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(preview.After.Outcome, Is.EqualTo(ChannelOutcome.Ng));
        Assert.That(preview.Changed, Is.True);
        Assert.That(preview.After.BrightFraction, Is.Zero);

        // Explicit publish creates new immutable hash; old release and old
        // workpiece are untouched, and a new workpiece uses Epoch 2 / Release B.
        Assert.That(RoiReleaseWorkflow.ReadPublishedRoi(original, "CH06"), Is.Null);
        var revised = repo.Publish("RoiDemo", "operator");
        Assert.That(revised.ReleaseId, Is.Not.EqualTo(original.ReleaseId));
        Assert.That(revised.ContentSha256, Is.Not.EqualTo(original.ContentSha256));
        Assert.That(repo.RequireRelease(original.ReleaseId), Is.EqualTo(original));
        var publishedRoi = RoiReleaseWorkflow.ReadPublishedRoi(revised, "CH06");
        Assert.That(publishedRoi!.SourceRevision, Is.EqualTo(1));
        Assert.That(publishedRoi.Bounds.ToPixels(64, 64),
            Is.EqualTo(new PixelInspectionRoi(0, 0, 8, 8)));
        var afterPublication = RoiPixelPreview.Compare(sample,
            RoiReleaseWorkflow.RequireStation(original, "CH06"),
            RoiReleaseWorkflow.RequireStation(revised, "CH06"), "CH06");
        Assert.That(afterPublication.Before.Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(afterPublication.After.Outcome, Is.EqualTo(ChannelOutcome.Ng));

        var master = new SimChangeoverNode(Path.Combine(_root, "master", "sim-product-node.sqlite"),
            "master", MachineRole.Master);
        var slave = new SimChangeoverNode(Path.Combine(_root, "slave", "sim-product-node.sqlite"),
            "slave", MachineRole.Slave);
        var coordinator = new ChangeoverCoordinator(Path.Combine(_root, "sim-product-coordinator.sqlite"));
        var a = coordinator.Execute(new ChangeoverRequest(Guid.NewGuid(), original, 0,
            null, 1, "operator"), master, slave, SimInterlocks.AllSafeForOfflineTest);
        Assert.That(a.Phase, Is.EqualTo(ChangeoverPhase.Committed), a.Detail);
        var b = coordinator.Execute(new ChangeoverRequest(Guid.NewGuid(), revised, 1,
            original.ReleaseId, 2, "operator"), master, slave, SimInterlocks.AllSafeForOfflineTest);
        Assert.That(b.Phase, Is.EqualTo(ChangeoverPhase.Committed), b.Detail);
        Assert.That(master.Snapshot().ActiveReleaseId, Is.EqualTo(revised.ReleaseId));
        Assert.That(slave.Snapshot().ActiveReleaseId, Is.EqualTo(revised.ReleaseId));
        Assert.That(master.Snapshot().ActiveEpoch, Is.EqualTo(2));
        Assert.That(slave.Snapshot().ActiveEpoch, Is.EqualTo(2));
        Assert.That(inFlightBeforeEdit.Epoch, Is.EqualTo(1));
        Assert.That(inFlightBeforeEdit.ReleaseId, Is.EqualTo(original.ReleaseId));
        var nextItem = WorkpieceIdentity.New("SAME", 2, revised.ReleaseId);
        Assert.That(nextItem.WorkpieceId, Is.Not.EqualTo(inFlightBeforeEdit.WorkpieceId));
    }

    [Test]
    public void StaleRoiOrStationRevisionNeverImportsOrMutatesOldRelease()
    {
        var repo = NewProduct();
        var a = repo.Publish("RoiDemo", "operator");
        SaveOperatorRoi("CH06", 2, 0, 0, 0.125, 0.125);
        Assert.Throws<InvalidOperationException>(() => RoiReleaseWorkflow.ImportCoreDraft(
            repo, _root, "RoiDemo", "CH06", 1, 1, "operator"));
        Assert.Throws<InvalidOperationException>(() => RoiReleaseWorkflow.ImportCoreDraft(
            repo, _root, "RoiDemo", "CH06", 2, 0, "operator"));
        Assert.That(repo.GetDraft("RoiDemo", "ST05")!.Revision, Is.EqualTo(1));
        Assert.That(repo.RequireRelease(a.ReleaseId).ContentSha256, Is.EqualTo(a.ContentSha256));
    }

    [Test]
    public void CoreRoiCannotBeReadFromOtherMachineOrOutsideBounds()
    {
        var repo = NewProduct();
        SaveOperatorRoi("CH06", 1, 0.9, 0.1, 0.3, 0.2);
        Assert.Throws<InvalidDataException>(() => RoiReleaseWorkflow.ImportCoreDraft(
            repo, _root, "RoiDemo", "CH06", 1, 1, "operator"));
        Assert.Throws<FileNotFoundException>(() =>
            RoiReleaseWorkflow.ReadCoreDraft(_root, "CH02"));
        Assert.That(repo.GetDraft("RoiDemo", "ST05")!.Revision, Is.EqualTo(1));
    }

    [Test]
    public void MalformedPartialRoiCannotEnterPublishedManifest()
    {
        var repo = NewProduct();
        var draft = repo.GetDraft("RoiDemo", "ST05")!;
        var values = new Dictionary<string, double>(draft.Recipe.Tunables)
        {
            ["roi.CH06.x"] = 0.25
        };
        repo.SaveDraft("RoiDemo",
            new StationRecipe("ST05", draft.Recipe.ChannelIds, values),
            draft.Revision, "operator");
        Assert.Throws<InvalidDataException>(() => repo.Publish("RoiDemo", "operator"));
    }

    [Test]
    public void RoiWithTooSmallPixelRegionAndNaNDoesNotSlipThrough()
    {
        var invalid = new NormalizedInspectionRoi(double.NaN, 0, 0.1, 0.1);
        Assert.Throws<InvalidDataException>(() => invalid.Validate("CH06"));
        var roi = new NormalizedInspectionRoi(0.9, 0.9, 0.1, 0.1);
        roi.Validate("CH06");
        Assert.That(roi.ToPixels(64, 64).X, Is.EqualTo(57));
    }
}
