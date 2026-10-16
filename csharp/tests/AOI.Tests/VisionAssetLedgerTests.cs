using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Tests;

[TestFixture]
public sealed class VisionAssetLedgerTests
{
    private string _root = string.Empty;
    private VisionAssetLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a3-" + Guid.NewGuid().ToString("N"));
        _ledger = new VisionAssetLedger(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static WorkpieceIdentity Item() => WorkpieceIdentity.New("SIM-A3", 4, "SIM_LAB_ONLY");

    [Test]
    public void NineDevicesElevenChannels_AreActualPixelsWithSharedLineParentAndWal()
    {
        var item = Item();
        var results = SimVisionFixture.CaptureAllMappedChannels(item, _ledger,
            new SimPixelRule(127, 0.1));
        Assert.That(results, Has.Count.EqualTo(11));
        Assert.That(results.Select(x => x.ChannelId), Is.EquivalentTo(ChannelCatalog.All.Select(x => x.ChannelId)));
        Assert.That(results.All(x => x.IsSimulated && x.Outcome == ChannelOutcome.Ok), Is.True);
        Assert.That(results.Select(x => x.PhysicalDeviceId).Distinct().Count(), Is.EqualTo(9));
        Assert.That(results.Count(x => x.ParentFrameId is not null), Is.EqualTo(4));
        foreach (var pair in new[] { new[] { "CH08", "CH09" }, new[] { "CH10", "CH11" } })
        {
            var children = results.Where(x => pair.Contains(x.ChannelId)).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(children.Select(x => x.ParentFrameId).Distinct().Count(), Is.EqualTo(1));
                Assert.That(children.Select(x => x.CaptureId).Distinct().Count(), Is.EqualTo(1));
                Assert.That(children.Select(x => x.SourceFrameSha256).Distinct().Count(), Is.EqualTo(1));
            });
            foreach (var child in children)
            {
                using var pixels = _ledger.LoadVerified(child.FrameId);
                Assert.That(pixels.Rows, Is.EqualTo(32));
                Assert.That(pixels.Cols, Is.EqualTo(32));
            }
        }
        var stats = _ledger.Stats();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Assets, Is.EqualTo(13));
            Assert.That(stats.AreaFrames, Is.EqualTo(7));
            Assert.That(stats.LineParents, Is.EqualTo(2));
            Assert.That(stats.LineChildren, Is.EqualTo(4));
            Assert.That(stats.Workpieces, Is.EqualTo(1));
            Assert.That(stats.ChannelResults, Is.EqualTo(11));
            Assert.That(stats.StoredBytes, Is.GreaterThan(0));
            Assert.That(_ledger.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(11));
        });
        var audit = _ledger.Audit();
        Assert.That(audit.VerifiedAssets, Is.EqualTo(13));
        Assert.That(audit.Problems, Is.Empty);
        using var db = new SqliteConnection($"Data Source={_ledger.DatabasePath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("wal"));
    }

    [Test]
    public void WithoutValidatedRule_GenuinePixelsDoNotProduceAnAutomaticGood()
    {
        var item = Item();
        var observations = SimVisionFixture.CaptureAllMappedChannels(item, _ledger);
        Assert.That(observations.All(o => o.Outcome == ChannelOutcome.Uncertain), Is.True);
        var decision = QualityRules.Evaluate(item,
            ChannelCatalog.All.Where(x => !x.IsPositioning).Select(x => x.ChannelId), observations, true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Hold));
        Assert.That(decision.Proof, Does.Contain("INCOMPLETE"));
    }

    [Test]
    public void ExplicitSimulationRule_CanDriveOnlySimulationMarkedDecision()
    {
        var item = Item();
        var observations = SimVisionFixture.CaptureAllMappedChannels(item, _ledger,
            new SimPixelRule(127, 0.5));
        var decision = QualityRules.Evaluate(item,
            ChannelCatalog.All.Where(x => !x.IsPositioning).Select(x => x.ChannelId), observations, true);
        Assert.That(decision.Disposition, Is.EqualTo(FinalDisposition.Good));
        Assert.That(decision.IsSimulation, Is.True);
        Assert.That(decision.Proof, Does.Contain("SIM_ONLY"));
    }

    [Test]
    public void RealNativeRoiCrop_PreservesExactParentSourcePixelsWithoutInterpolation()
    {
        using var parent = new Mat(20, 40, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(parent, new Rect(1, 1, 15, 17), new Scalar(224), -1);
        Cv2.Rectangle(parent, new Rect(24, 2, 12, 16), new Scalar(103), -1);
        var pair = _ledger.CaptureLinePair(Item(), "LINE-S-8", parent,
            new ImageRegion(0, 0, 20, 20), new ImageRegion(20, 0, 20, 20));
        Assert.That(pair, Has.Count.EqualTo(2));
        var first = _ledger.GetAsset(pair[0].FrameId)!;
        var second = _ledger.GetAsset(pair[1].FrameId)!;
        var parentMeta = _ledger.GetAsset(first.ParentFrameId!.Value)!;
        Assert.That(first.SourceFrameSha256, Is.EqualTo(parentMeta.ContentSha256));
        Assert.That(second.SourceFrameSha256, Is.EqualTo(parentMeta.ContentSha256));
        using var decodedParent = _ledger.LoadVerified(parentMeta.FrameId);
        using var decodedFirst = _ledger.LoadVerified(first.FrameId);
        using var decodedSecond = _ledger.LoadVerified(second.FrameId);
        using var expectedLeft = new Mat(parent, new Rect(0, 0, 20, 20));
        using var expectedRight = new Mat(parent, new Rect(20, 0, 20, 20));
        using var deltaL = new Mat();
        using var deltaR = new Mat();
        Cv2.Absdiff(decodedFirst, expectedLeft, deltaL);
        Cv2.Absdiff(decodedSecond, expectedRight, deltaR);
        Assert.That(Cv2.CountNonZero(deltaL) + Cv2.CountNonZero(deltaR), Is.Zero);
        Assert.That(decodedParent.Rows, Is.EqualTo(parent.Rows));
        Assert.That(_ledger.Audit().Problems, Is.Empty);
    }

    [Test]
    public void InvalidRoiAndUnknownGroup_FailClosedBeforeWritingAnything()
    {
        using var pixels = new Mat(20, 40, MatType.CV_8UC1, Scalar.Black);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ledger.CaptureLinePair(Item(),
            "LINE-M-2", pixels, new ImageRegion(0, 0, 20, 20), new ImageRegion(21, 0, 20, 20)));
        Assert.Throws<ArgumentException>(() => _ledger.CaptureLinePair(Item(),
            "WRONG-DEVICE", pixels, new ImageRegion(0, 0, 20, 20), new ImageRegion(20, 0, 20, 20)));
        Assert.Throws<ArgumentException>(() => _ledger.CaptureArea(Item(), "CH08", pixels));
        Assert.That(_ledger.Stats().Assets, Is.Zero);
        Assert.That(_ledger.Audit().Problems, Is.Empty);
    }

    [Test]
    public void UnsupportedLinePixelTypeAndUnboundedThreshold_AreRejected()
    {
        using var color = new Mat(10, 30, MatType.CV_8UC3, Scalar.Black);
        Assert.Throws<ArgumentException>(() => _ledger.CaptureLinePair(Item(), "LINE-S-8", color,
            new ImageRegion(0, 0, 15, 10), new ImageRegion(15, 0, 15, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ledger.CaptureArea(Item(), "CH02", color,
            simRule: new SimPixelRule(127, double.NaN)));
        Assert.That(_ledger.Stats().ChannelResults, Is.Zero);
    }

    [Test]
    public void CorruptedOrMissingAsset_IsNeverSilentlyLoadedOrCertified()
    {
        var item = Item();
        using var pixels = new Mat(32, 32, MatType.CV_8UC1, Scalar.White);
        var result = _ledger.CaptureArea(item, "CH02", pixels);
        var meta = _ledger.GetAsset(result.FrameId)!;
        File.AppendAllText(Path.Combine(_root, meta.RelativePath), "TAMPERED");
        Assert.Throws<InvalidDataException>(() => _ledger.LoadVerified(meta.FrameId).Dispose());
        Assert.That(_ledger.Audit().Problems.Any(x => x.Contains("SHA256")), Is.True);
        File.Delete(Path.Combine(_root, meta.RelativePath));
        Assert.Throws<FileNotFoundException>(() => _ledger.LoadVerified(meta.FrameId).Dispose());
        Assert.That(_ledger.Audit().VerifiedAssets, Is.Zero);
    }

    [Test]
    public void SimulatedReinspection_RequiresNewAttemptAndRetainsPreviousEvidence()
    {
        var item = Item();
        using var pixels = new Mat(16, 16, MatType.CV_8UC1, Scalar.White);
        var first = _ledger.CaptureArea(item, "CH03", pixels, attemptNo: 1);
        Assert.Throws<SqliteException>(() => _ledger.CaptureArea(item, "CH03", pixels, attemptNo: 1));
        Assert.That(_ledger.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(1));
        var second = _ledger.CaptureArea(item, "CH03", pixels, attemptNo: 2);
        Assert.That(first.FrameId, Is.Not.EqualTo(second.FrameId));
        Assert.That(_ledger.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(2));
        // Uncommitted PNGs are audit evidence, not fabricated SQLite results.
        Assert.That(_ledger.Audit().Problems.Any(x => x.Contains("UNCOMMITTED_OR_PENDING_ASSET")), Is.True);
    }

    [Test]
    public void OrphanAfterInterruptedWrite_IsFlaggedRatherThanAutoPromoted()
    {
        var item = Item();
        using var image = new Mat(10, 10, MatType.CV_8UC1, Scalar.White);
        var stored = _ledger.CaptureArea(item, "CH06", image);
        var asset = _ledger.GetAsset(stored.FrameId)!;
        var pending = Path.Combine(Path.GetDirectoryName(Path.Combine(_root, asset.RelativePath))!,
            Guid.NewGuid().ToString("N") + ".png.pending.png");
        Assert.That(Cv2.ImWrite(pending, image), Is.True);
        var reopened = new VisionAssetLedger(_root);
        Assert.That(reopened.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(1));
        Assert.That(reopened.Audit().Problems.Any(x => x.Contains("UNCOMMITTED_OR_PENDING_ASSET")), Is.True);
    }

    [Test]
    public void ColorFrame_IsLosslesslyRetainedAndPixelProbeUsesOpenCvGrayConversion()
    {
        using var color = new Mat(24, 24, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(color, new Rect(0, 0, 12, 24), new Scalar(255, 255, 255), -1);
        var result = _ledger.CaptureArea(Item(), "CH07", color, simRule: new SimPixelRule(127, 0.4));
        Assert.That(result.Outcome, Is.EqualTo(ChannelOutcome.Ok));
        using var restored = _ledger.LoadVerified(result.FrameId);
        Assert.That(restored.Type(), Is.EqualTo(MatType.CV_8UC3));
        var probe = VisionPixelOps.Probe(restored);
        Assert.That(probe.BrightFraction, Is.EqualTo(0.5).Within(0.00001));
        Assert.That(probe.Mean, Is.EqualTo(127.5).Within(0.01));
    }
}
