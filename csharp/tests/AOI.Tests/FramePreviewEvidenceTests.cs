using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;
using System.Security.Cryptography;

namespace AOI.Tests;

[TestFixture]
public sealed class FramePreviewEvidenceTests
{
    private string _root = string.Empty;
    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-frame-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private static WorkpieceIdentity Item() =>
        WorkpieceIdentity.New("SIM", 1, "SIM_LIGHT_TEST_RELEASE");
    private ProductionStore Store() => new(Path.Combine(_root, "production.db"));

    [Test]
    public void OnlyCurrentOwnerCanPreviewAndCorruptedAreaPngIsNeverShown()
    {
        var workpiece = Item();
        var store = Store();
        store.CreateAndQueueRegistration(workpiece,
            PeerEnvelope.Create("master", "slave", 1, workpiece, "workpiece_registered", workpiece));
        var ledger = new VisionAssetLedger(Path.Combine(_root, "vision"));
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.White);
        var result = ledger.CaptureArea(workpiece, "CH06", mat, simRule: new SimPixelRule(127, 0.1));
        store.SaveLocalResult(result);
        var bytes = FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, workpiece.WorkpieceId, "CH06");
        Assert.That(bytes, Is.Not.Null);
        Assert.That(Convert.ToHexString(SHA256.HashData(bytes!)).ToLowerInvariant(),
            Is.EqualTo(result.SourceFrameSha256));
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Slave,
            _root, workpiece.WorkpieceId, "CH06"), Is.Null);
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, Guid.NewGuid(), "CH06"), Is.Null);
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, workpiece.WorkpieceId, "../../production.db"), Is.Null);

        var asset = ledger.GetAsset(result.FrameId)!;
        File.WriteAllBytes(Path.Combine(_root, "vision", asset.RelativePath), [1, 2, 3, 4]);
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, workpiece.WorkpieceId, "CH06"), Is.Null);
    }

    [Test]
    public void MissingOrTamperedLineScanParentBlocksBothRoiPreviews()
    {
        var item = Item();
        var store = Store();
        store.CreateAndQueueRegistration(item,
            PeerEnvelope.Create("master", "slave", 1, item, "workpiece_registered", item));
        var ledger = new VisionAssetLedger(Path.Combine(_root, "vision"));
        using var mat = new Mat(32, 64, MatType.CV_8UC1, Scalar.White);
        var results = ledger.CaptureLinePair(item, "LINE-M-2", mat,
            new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32),
            simRule: new SimPixelRule(127, 0.1));
        foreach (var result in results) store.SaveLocalResult(result);
        foreach (var channel in new[] { "CH10", "CH11" })
        {
            var preview = FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
                _root, item.WorkpieceId, channel);
            Assert.That(preview, Is.Not.Null, channel);
            Assert.That(preview!.Take(8).ToArray(), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        }
        var parent = ledger.GetAsset(results[0].ParentFrameId!.Value)!;
        File.Delete(Path.Combine(_root, "vision", parent.RelativePath));
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, item.WorkpieceId, "CH10"), Is.Null);
        Assert.That(FramePreviewIpc.ReadVerifiedPng(store, MachineRole.Master,
            _root, item.WorkpieceId, "CH11"), Is.Null);
    }
}
