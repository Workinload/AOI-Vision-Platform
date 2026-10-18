using AOI.Domain;
using AOI.Runtime;
using AOI.Solutions;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Solutions.Tests;

/// <summary>Read-only real-frame size lookup used to enable offline ROI editing.</summary>
[TestFixture]
public sealed class OfflineVerifiedFrameSourceTests
{
    private string root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "aoi-frame-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public void LoadsTheRealCapturedPixelSizeAndRejectsATamperedPng()
    {
        var ledger = new VisionAssetLedger(Path.Combine(root, "master", "vision"));
        var item = WorkpieceIdentity.New("reference", 1, "REL_REFERENCE");
        using var pixels = new Mat(800, 1200, MatType.CV_8UC1, Scalar.Black);
        var captured = ledger.CaptureArea(item, "CH06", pixels, 1);

        var loaded = OfflineVerifiedFrameSource.LoadFromRunRoot(root, ["CH06", "CH01"]);
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Has.Count.EqualTo(1), "only the master-owned captured channel exists");
            Assert.That(loaded[0].ChannelId, Is.EqualTo("CH06"));
            Assert.That(loaded[0].Size.Width, Is.EqualTo(1200));
            Assert.That(loaded[0].Size.Height, Is.EqualTo(800));
            Assert.That(loaded[0].Sha256, Has.Length.EqualTo(64));
            Assert.That(loaded[0].Provenance, Does.Contain("只读"));
        });

        var asset = ledger.GetAsset(captured.FrameId);
        Assert.That(asset, Is.Not.Null);
        string png = Path.Combine(root, "master", "vision", asset!.RelativePath);
        Assert.That(File.Exists(png), Is.True);
        File.AppendAllText(png, "tampered");

        var afterTamper = OfflineVerifiedFrameSource.LoadFromRunRoot(root, ["CH06"]);
        Assert.That(afterTamper, Is.Empty, "a PNG that no longer matches the ledger digest is never used");
    }

    [Test]
    public void MissingReferenceRootFailsClosedInsteadOfInventingSizes()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            OfflineVerifiedFrameSource.LoadFromRunRoot(Path.Combine(root, "does-not-exist")));
        Assert.That(OfflineVerifiedFrameSource.LoadFromRunRoot(root), Is.Empty);
    }
}
