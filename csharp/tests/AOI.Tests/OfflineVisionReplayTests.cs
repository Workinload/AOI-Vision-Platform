using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Tests;

[TestFixture]
public sealed class OfflineVisionReplayTests
{
    private string _root = string.Empty;
    private string _images = string.Empty;
    private VisionAssetLedger _ledger = null!;
    private OfflineVisionReplay _replay = null!;
    private static readonly ContourDefectRule Rule = new(127, 5);

    [SetUp]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "a3-feature86-" + Guid.NewGuid().ToString("N"));
        _images = Path.Combine(_root, "import");
        Directory.CreateDirectory(_images);
        _ledger = new VisionAssetLedger(Path.Combine(_root, "vision"));
        _replay = new OfflineVisionReplay(_ledger);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ReplayBatch Run(WorkpieceIdentity workpiece, MachineRole role,
        IReadOnlyDictionary<string, ImageRegion>? rois = null, bool flat = true)
        => _replay.CaptureOwned(workpiece, role, _images, Path.Combine(_root, "overlays"),
            Rule, rois, flatDemoFolder: flat);

    [Test]
    public void DifferentLocalPng_ChangesRealContourVerdictAndSavesRedOverlayAndOriginalPixels()
    {
        string imagePath = Path.Combine(_images, "CH06.png");
        using (var clean = new Mat(90, 120, MatType.CV_8UC1, Scalar.Black))
            Assert.That(Cv2.ImWrite(imagePath, clean), Is.True);
        var first = Run(WorkpieceIdentity.New("photo-A", 5, "SIM_RELEASE"), MachineRole.Master);
        var good = first.Channels.Single(x => x.ChannelId == "CH06");
        Assert.Multiple(() =>
        {
            Assert.That(first.Channels, Has.Count.EqualTo(5));
            Assert.That(first.Missing, Is.EqualTo(4));
            Assert.That(good.Outcome, Is.EqualTo(ChannelOutcome.Ok));
            Assert.That(good.Reason, Is.EqualTo("NO_CONTOUR_DEFECT"));
            Assert.That(good.Observation!.IsSimulated, Is.True);
            Assert.That(File.Exists(good.OverlayPath), Is.True);
        });

        using (var defect = new Mat(90, 120, MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.Rectangle(defect, new Rect(34, 20, 18, 24), Scalar.White, -1);
            Assert.That(Cv2.ImWrite(imagePath, defect), Is.True);
        }
        var second = Run(WorkpieceIdentity.New("photo-B", 5, "SIM_RELEASE"), MachineRole.Master);
        var ng = second.Channels.Single(x => x.ChannelId == "CH06");
        Assert.That(ng.Outcome, Is.EqualTo(ChannelOutcome.Ng));
        Assert.That(ng.Reason, Is.EqualTo("CONTOUR_DEFECTS:1"));
        Assert.That(ng.Defects, Has.Count.EqualTo(1));
        Assert.That(ng.Defects[0].X, Is.EqualTo(34));
        Assert.That(ng.Defects[0].Y, Is.EqualTo(20));
        Assert.That(ng.Defects[0].Width, Is.EqualTo(18));
        Assert.That(ng.Defects[0].Height, Is.EqualTo(24));
        Assert.That(ng.Observation!.SourceFrameSha256, Is.Not.EqualTo(good.Observation!.SourceFrameSha256));
        using var replayed = _ledger.LoadVerified(ng.Observation.FrameId);
        Assert.That(replayed.At<byte>(25, 40), Is.EqualTo(255));
        using var annotation = Cv2.ImRead(ng.OverlayPath!, ImreadModes.Color);
        Assert.That(annotation.Rows, Is.EqualTo(90));
        Assert.That(annotation.Cols, Is.EqualTo(120));
        var red = annotation.At<Vec3b>(20, 34);
        Assert.That(red.Item2, Is.EqualTo(255), "Overlay's box border is red in BGR.");
        Assert.That(_ledger.ResultsFor(second.WorkpieceId).Single().Outcome, Is.EqualTo(ChannelOutcome.Ng));
    }

    [Test]
    public void OddEvenLineScan_RetainsOneOriginalAndTwoDifferentHeightChildren()
    {
        using (var parent = new Mat(9, 20, MatType.CV_8UC1, Scalar.Black))
        {
            int rows = parent.Rows;
            for (int y = 1; y < rows; y += 2)
                Cv2.Rectangle(parent, new Rect(0, y, 20, 1), Scalar.White, -1);
            Assert.That(Cv2.ImWrite(Path.Combine(_images, "LINE-S-8.png"), parent), Is.True);
        }
        var batch = Run(WorkpieceIdentity.New("line-1", 6, "SIM_RELEASE"), MachineRole.Slave);
        var even = batch.Channels.Single(x => x.ChannelId == "CH08");
        var odd = batch.Channels.Single(x => x.ChannelId == "CH09");
        Assert.Multiple(() =>
        {
            Assert.That(batch.Channels, Has.Count.EqualTo(6));
            Assert.That(even.Outcome, Is.EqualTo(ChannelOutcome.Ok));
            Assert.That(odd.Outcome, Is.EqualTo(ChannelOutcome.Ng));
            Assert.That(even.Observation!.ParentFrameId, Is.EqualTo(odd.Observation!.ParentFrameId));
            Assert.That(even.Observation.SourceFrameSha256, Is.EqualTo(odd.Observation.SourceFrameSha256));
        });
        var parentId = even.Observation!.ParentFrameId!.Value;
        var parentAsset = _ledger.GetAsset(parentId)!;
        var evenAsset = _ledger.GetAsset(even.Observation.FrameId)!;
        var oddAsset = _ledger.GetAsset(odd.Observation!.FrameId)!;
        Assert.That(parentAsset.Width, Is.EqualTo(20));
        Assert.That(parentAsset.Height, Is.EqualTo(9));
        Assert.That(evenAsset.PixelTransform, Is.EqualTo("interleaved-even-rows"));
        Assert.That(oddAsset.PixelTransform, Is.EqualTo("interleaved-odd-rows"));
        using var original = _ledger.LoadVerified(parentId);
        using var evenPixels = _ledger.LoadVerified(evenAsset.FrameId);
        using var oddPixels = _ledger.LoadVerified(oddAsset.FrameId);
        Assert.That(evenPixels.Rows, Is.EqualTo(5));
        Assert.That(oddPixels.Rows, Is.EqualTo(4));
        Assert.That(evenPixels.Cols, Is.EqualTo(20));
        Assert.That(Cv2.CountNonZero(evenPixels), Is.Zero);
        Assert.That(Cv2.CountNonZero(oddPixels), Is.EqualTo(80));
        Assert.That(_ledger.Audit().Problems, Is.Empty);
    }

    [Test]
    public void MissingImageAndInvalidPng_AreReportedAndNeverBecomeFakeGood()
    {
        var item = WorkpieceIdentity.New("missing", 1, "SIM_RELEASE");
        var missing = Run(item, MachineRole.Slave);
        Assert.That(missing.Channels, Has.Count.EqualTo(6));
        Assert.That(missing.Channels.All(x => x.Outcome == ChannelOutcome.Missing && x.Observation is null), Is.True);
        File.WriteAllText(Path.Combine(_images, "CH02.png"), "not-an-image");
        var corrupt = Run(WorkpieceIdentity.New("badpng", 1, "SIM_RELEASE"), MachineRole.Slave);
        var ch02 = corrupt.Channels.Single(x => x.ChannelId == "CH02");
        Assert.That(ch02.Outcome, Is.EqualTo(ChannelOutcome.Error));
        Assert.That(ch02.Reason, Does.StartWith("INVALID_IMAGE"));
        Assert.That(ch02.Observation, Is.Null);
        Assert.That(_ledger.Stats().Assets, Is.Zero);
    }

    [Test]
    public void OperatorRoiActuallyChangesContourResult_AndCoordinatesStayGlobal()
    {
        using (var pixels = new Mat(50, 80, MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.Rectangle(pixels, new Rect(35, 14, 14, 17), Scalar.White, -1);
            Assert.That(Cv2.ImWrite(Path.Combine(_images, "CH07.png"), pixels), Is.True);
        }
        var excluded = Run(WorkpieceIdentity.New("roi-left", 5, "SIM_RELEASE"), MachineRole.Master,
            new Dictionary<string, ImageRegion> { ["CH07"] = new(0, 0, 20, 50) });
        var included = Run(WorkpieceIdentity.New("roi-right", 5, "SIM_RELEASE"), MachineRole.Master,
            new Dictionary<string, ImageRegion> { ["CH07"] = new(30, 10, 30, 30) });
        Assert.That(excluded.Channels.Single(x => x.ChannelId == "CH07").Outcome, Is.EqualTo(ChannelOutcome.Ok));
        var hit = included.Channels.Single(x => x.ChannelId == "CH07");
        Assert.That(hit.Outcome, Is.EqualTo(ChannelOutcome.Ng));
        Assert.That(hit.Defects.Single().X, Is.EqualTo(35));
        Assert.That(hit.Defects.Single().Y, Is.EqualTo(14));
    }

    [Test]
    public void ScopeByWorkpieceId_AvoidsPickingWrongItemsOrForeignNodeRoi()
    {
        var id = WorkpieceIdentity.New("same-barcode", 5, "SIM_RELEASE");
        var dir = Path.Combine(_images, id.WorkpieceId.ToString("N"));
        Directory.CreateDirectory(dir);
        using (var img = new Mat(40, 40, MatType.CV_8UC1, Scalar.Black))
            Assert.That(Cv2.ImWrite(Path.Combine(dir, "CH06.png"), img), Is.True);
        var scoped = Run(id, MachineRole.Master, flat: false);
        Assert.That(scoped.Captured, Is.EqualTo(1));
        var different = Run(WorkpieceIdentity.New("same-barcode", 5, "SIM_RELEASE"), MachineRole.Master, flat: false);
        Assert.That(different.Missing, Is.EqualTo(5));
        Assert.Throws<ArgumentException>(() => Run(WorkpieceIdentity.New("different", 5, "SIM_RELEASE"),
            MachineRole.Master, new Dictionary<string, ImageRegion> { ["CH02"] = new(0, 0, 2, 2) }));
    }
}
