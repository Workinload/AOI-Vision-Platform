using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Tests;

[TestFixture]
public sealed class TriggeredVisionTests
{
    private string _root = string.Empty;
    private VisionAssetLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a3-trigger-" + Guid.NewGuid().ToString("N"));
        _ledger = new VisionAssetLedger(Path.Combine(_root, "vision"));
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static CaptureTicket Ticket(WorkpieceIdentity? item = null, string device = "HIK-M-2",
        int attempt = 1) => new(item ?? WorkpieceIdentity.New("BARCODE-A", 7, "RELEASE-A"),
            device, Guid.NewGuid(), attempt);

    private static InspectionSettings Settings(ChannelDefinition channel) =>
        InspectionSettings.OpenCvContour(new ContourDefectRule(127, 5));

    [TestCase(true)]
    [TestCase(false)]
    public async Task St05HardTriggeredFrame_InBothOrders_PersistsActualCaptureAndFrameIds(bool frameFirst)
    {
        var ticket = Ticket();
        var frameId = Guid.NewGuid();
        using var inbox = new BoundedCameraFrameInbox();
        var engine = new TriggeredVisionEngine(_ledger, new OpenCvInspectionProcessor());
        Task<FrameRead>? pending = frameFirst ? null :
            inbox.ReadAsync(ticket, TimeSpan.FromSeconds(3));
        using var vendor = new Mat(40, 60, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(vendor, new Rect(17, 9, 12, 12), Scalar.White, -1);
        var physicalFrame = CameraFrame.CopyFrom(ticket, frameId, vendor);
        Assert.That(inbox.TryOffer(physicalFrame), Is.True);
        // Camera SDK may reuse this buffer immediately; A3 owned copy stays unchanged.
        vendor.SetTo(Scalar.Black);
        var ready = frameFirst ? await inbox.ReadAsync(ticket, TimeSpan.FromSeconds(3)) :
            await pending!;
        Assert.That(ready.Status, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(ready.Frame!.FrameId, Is.EqualTo(frameId));
        // A0 sends FINS CamDone now, BEFORE inspection/SQLite result creation.
        var report = engine.InspectAndPersist(ticket, ready, Settings);
        var output = report.Channels.Single();
        Assert.Multiple(() =>
        {
            Assert.That(report.FrameAcquired, Is.True);
            Assert.That(output.ChannelId, Is.EqualTo("CH06"));
            Assert.That(output.Outcome, Is.EqualTo(ChannelOutcome.Ng));
            Assert.That(output.Defects, Has.Count.EqualTo(1));
            Assert.That(output.Observation!.WorkpieceId, Is.EqualTo(ticket.Workpiece.WorkpieceId));
            Assert.That(output.Observation.CaptureId, Is.EqualTo(ticket.CaptureId));
            Assert.That(output.Observation.FrameId, Is.EqualTo(frameId));
            Assert.That(output.Observation.AttemptNo, Is.EqualTo(ticket.AttemptNo));
            Assert.That(output.AnnotationPng, Is.Not.Empty);
        });
        using var committedPixels = _ledger.LoadVerified(frameId);
        Assert.That(committedPixels.At<byte>(12, 20), Is.EqualTo(255));
        Assert.That(_ledger.ResultsFor(ticket.Workpiece.WorkpieceId), Has.Count.EqualTo(1));
        Assert.That(_ledger.Audit().Problems, Is.Empty);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(ticket, Guid.NewGuid(), vendor)), Is.False);
        Assert.Throws<ObjectDisposedException>(() => _ = ready.Frame!.Pixels);
    }

    [Test]
    public async Task TimedOutItem_DoesNotBorrowLateFrameOrNextWorkpieceWithSameBarcode()
    {
        var first = Ticket();
        var second = Ticket(WorkpieceIdentity.New("BARCODE-A", 7, "RELEASE-A"));
        using var inbox = new BoundedCameraFrameInbox();
        var timedOut = await inbox.ReadAsync(first, TimeSpan.FromMilliseconds(30));
        Assert.That(timedOut.Status, Is.EqualTo(ChannelOutcome.Missing));
        using var pixels = new Mat(24, 24, MatType.CV_8UC1, Scalar.Black);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(first, Guid.NewGuid(), pixels)), Is.False);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(second, Guid.NewGuid(), pixels)), Is.True);
        var receivedSecond = await inbox.ReadAsync(second, TimeSpan.FromSeconds(1));
        var engine = new TriggeredVisionEngine(_ledger, new OpenCvInspectionProcessor());
        var failed = engine.InspectAndPersist(first, timedOut, Settings);
        var good = engine.InspectAndPersist(second, receivedSecond, Settings);
        Assert.That(failed.Channels.Single().Outcome, Is.EqualTo(ChannelOutcome.Missing));
        Assert.That(failed.PersistedResults, Is.Zero);
        Assert.That(good.Channels.Single().Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(_ledger.ResultsFor(first.Workpiece.WorkpieceId), Is.Empty);
        Assert.That(_ledger.ResultsFor(second.Workpiece.WorkpieceId), Has.Count.EqualTo(1));
    }

    [Test]
    public void ForeignTicketOrDuplicateFrame_CannotBeRebound()
    {
        var first = Ticket();
        var wrong = first with { Workpiece = WorkpieceIdentity.New("SAME", 7, "RELEASE-A") };
        using var inbox = new BoundedCameraFrameInbox();
        using var pixels = new Mat(20, 20, MatType.CV_8UC1, Scalar.Black);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(first, Guid.NewGuid(), pixels)), Is.True);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(wrong, Guid.NewGuid(), pixels)), Is.False);
        Assert.That(inbox.TryOffer(CameraFrame.CopyFrom(first, Guid.NewGuid(), pixels)), Is.False);
        Assert.That(inbox.BufferedBytes, Is.EqualTo(400));
        var rejected = inbox.ReadAsync(wrong, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Assert.That(rejected.Status, Is.EqualTo(ChannelOutcome.Error));
        Assert.That(rejected.Reason, Is.EqualTo("CAPTURE_IDENTITY_MISMATCH"));
        var good = inbox.ReadAsync(first, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Assert.That(good.Status, Is.EqualTo(ChannelOutcome.Ok));
        good.Frame!.Dispose();
        Assert.That(inbox.BufferedBytes, Is.Zero);
    }

    [Test]
    public async Task SourceMissingAndCorruptPng_ReportSingleItemMissingOrError_NoFakeResults()
    {
        var ticket = Ticket();
        var adapter = new PngFileFrameSource(Path.Combine(_root, "images"));
        var engine = new TriggeredVisionEngine(_ledger, new OpenCvInspectionProcessor());
        var missing = await engine.CaptureAndInspectAsync(ticket, adapter, Settings, TimeSpan.FromSeconds(1));
        Assert.That(missing.Channels.Single().Outcome, Is.EqualTo(ChannelOutcome.Missing));
        Assert.That(missing.PersistedResults, Is.Zero);
        var folder = Path.Combine(_root, "images", ticket.Workpiece.WorkpieceId.ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "CH06.png"), "not real pixels");
        var corruptTicket = ticket with { CaptureId = Guid.NewGuid() };
        var corrupted = await engine.CaptureAndInspectAsync(corruptTicket, adapter, Settings,
            TimeSpan.FromSeconds(1));
        Assert.That(corrupted.Channels.Single().Outcome, Is.EqualTo(ChannelOutcome.Error));
        Assert.That(corrupted.PersistedResults, Is.Zero);
        Assert.That(_ledger.Stats().Assets, Is.Zero);
    }

    [Test]
    public void LineScan_OnePhysicalCapture_ProducesTwoResultsAndOriginalParentLineage()
    {
        var ticket = Ticket(device: "LINE-S-8");
        var parentFrameId = Guid.NewGuid();
        using var parent = new Mat(9, 30, MatType.CV_8UC1, Scalar.Black);
        for (int y = 1; y < parent.Rows; y += 2)
            Cv2.Rectangle(parent, new Rect(0, y, 30, 1), Scalar.White, -1);
        var ready = FrameRead.Ready(CameraFrame.CopyFrom(ticket, parentFrameId, parent));
        var report = new TriggeredVisionEngine(_ledger, new OpenCvInspectionProcessor())
            .InspectAndPersist(ticket, ready, Settings);
        Assert.That(report.PersistedResults, Is.EqualTo(2));
        Assert.That(report.Channels.Select(c => c.ChannelId), Is.EqualTo(new[] { "CH08", "CH09" }));
        Assert.That(report.Channels[0].Outcome, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(report.Channels[1].Outcome, Is.EqualTo(ChannelOutcome.Ng));
        var first = report.Channels[0].Observation!;
        var second = report.Channels[1].Observation!;
        Assert.That(first.CaptureId, Is.EqualTo(ticket.CaptureId));
        Assert.That(second.CaptureId, Is.EqualTo(ticket.CaptureId));
        Assert.That(first.ParentFrameId, Is.EqualTo(parentFrameId));
        Assert.That(second.ParentFrameId, Is.EqualTo(parentFrameId));
        using var savedParent = _ledger.LoadVerified(parentFrameId);
        using var savedEven = _ledger.LoadVerified(first.FrameId);
        using var savedOdd = _ledger.LoadVerified(second.FrameId);
        Assert.That(savedParent.Rows, Is.EqualTo(9));
        Assert.That(savedEven.Rows, Is.EqualTo(5));
        Assert.That(savedOdd.Rows, Is.EqualTo(4));
        Assert.That(Cv2.CountNonZero(savedEven), Is.Zero);
        Assert.That(Cv2.CountNonZero(savedOdd), Is.EqualTo(120));
        Assert.That(_ledger.Audit().Problems, Is.Empty);
    }

    [Test]
    public async Task QueueHasFinitePixelBudget_AndDoesNotRetainDisposedVendorMemory()
    {
        var first = Ticket();
        var second = Ticket();
        using var inbox = new BoundedCameraFrameInbox(maxPending: 2, maxBufferedBytes: 4096);
        using var original = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
        var one = CameraFrame.CopyFrom(first, Guid.NewGuid(), original);
        var two = CameraFrame.CopyFrom(second, Guid.NewGuid(), original);
        Assert.That(inbox.TryOffer(one), Is.True);
        Assert.That(inbox.TryOffer(two), Is.False, "At most one 4096-byte frame fits.");
        Assert.Throws<ObjectDisposedException>(() => _ = two.Pixels);
        Assert.That(inbox.BufferedBytes, Is.EqualTo(4096));
        var read = await inbox.ReadAsync(first, TimeSpan.FromSeconds(1));
        Assert.That(read.Status, Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(inbox.BufferedBytes, Is.Zero);
        read.Frame!.Dispose();
    }

    [Test]
    public void UnknownAlgorithm_CannotBecomeGoodOrAccidentalModelProof()
    {
        var ticket = Ticket();
        using var pixels = new Mat(32, 32, MatType.CV_8UC1, Scalar.Black);
        var output = new TriggeredVisionEngine(_ledger, new OpenCvInspectionProcessor())
            .InspectAndPersist(ticket, FrameRead.Ready(CameraFrame.CopyFrom(ticket,
                Guid.NewGuid(), pixels)), _ => new InspectionSettings("model.pending"));
        Assert.That(output.Channels.Single().Outcome, Is.EqualTo(ChannelOutcome.Uncertain));
        Assert.That(output.Channels.Single().Observation!.IsSimulated, Is.True);
        Assert.That(_ledger.ResultsFor(ticket.Workpiece.WorkpieceId).Single().Outcome,
            Is.EqualTo(ChannelOutcome.Uncertain));
    }
}
