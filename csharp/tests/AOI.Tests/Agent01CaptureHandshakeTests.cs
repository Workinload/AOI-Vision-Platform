using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenCvSharp;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent01CaptureHandshakeTests
{
    private string _root = string.Empty;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-a1-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private CaptureHandshakeCoordinator Coordinator() =>
        new(Path.Combine(_root, "master", "capture-events.db"));

    private VisionAssetLedger AssetLedger() => new(Path.Combine(_root, "vision"));

    private static WorkpieceIdentity Item(string barcode = "DUPLICATE") =>
        WorkpieceIdentity.New(barcode, 3, "SIM_LIGHT_TEST_RELEASE");

    private static CaptureTicket Ticket(WorkpieceIdentity item, ChannelResult result) =>
        new(item, result.CaptureId, result.AttemptNo, result.PhysicalDeviceId,
            DateTimeOffset.UtcNow.AddMinutes(2), IsSimulation: true);

    private static CaptureFrameEvidence Evidence(WorkpieceIdentity item, ChannelResult result) =>
        new(item.WorkpieceId, item.Epoch, item.ReleaseId, item.TraceId,
            result.CaptureId, result.AttemptNo, result.PhysicalDeviceId,
            result.ParentFrameId ?? result.FrameId, result.SourceFrameSha256, DateTimeOffset.UtcNow);

    private static ChannelResult NativeArea(VisionAssetLedger assets, WorkpieceIdentity item,
        int attemptNo = 1)
    {
        using var pixels = new Mat(48, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(pixels, new Rect(6, 8, 48, 30), Scalar.White, -1);
        return assets.CaptureArea(item, "CH06", pixels, attemptNo,
            new SimPixelRule(127, 0.2));
    }

    private static void CompleteArea(CaptureHandshakeCoordinator coordinator,
        WorkpieceIdentity item, ChannelResult result, bool frameFirst)
    {
        var ticket = Ticket(item, result);
        var frame = Evidence(item, result);
        Assert.That(coordinator.ExpectCapture(ticket), Is.True);
        if (frameFirst)
        {
            Assert.That(coordinator.FrameAcquired(frame), Is.True);
            Assert.That(coordinator.FrameValidated(ticket.CaptureId, frame.SourceFrameId), Is.True);
            Assert.That(coordinator.Get(ticket.CaptureId).Phase,
                Is.EqualTo("FRAME_READY_BEFORE_REQUEST"));
            Assert.That(coordinator.PendingCamDone(), Is.Empty);
            Assert.That(coordinator.ObservePlcRequest(ticket.CaptureId, "D1011"), Is.True);
        }
        else
        {
            Assert.That(coordinator.ObservePlcRequest(ticket.CaptureId, "D1011"), Is.True);
            Assert.That(coordinator.Get(ticket.CaptureId).Phase,
                Is.EqualTo("REQUEST_FIRST_WAIT_FRAME"));
            Assert.That(coordinator.FrameAcquired(frame), Is.True);
            Assert.That(coordinator.FrameValidated(ticket.CaptureId, frame.SourceFrameId), Is.True);
        }

        Assert.That(coordinator.Get(ticket.CaptureId).Phase, Is.EqualTo("CAM_DONE_READY"));
        Assert.That(coordinator.Get(ticket.CaptureId).ConfirmedCamDone, Is.Null);
        Assert.That(coordinator.PendingCamDone().Single().PendingCamDone, Is.EqualTo((ushort)2));
        // In a real Host A2 confirms this only after a successful FINS response to
        // D1011=2. The A1 module never writes to a PLC.
        Assert.That(coordinator.CamDoneConfirmed(ticket.CaptureId, 2), Is.True);
        Assert.That(coordinator.InspectionCompleted(result), Is.True);
        Assert.That(coordinator.InspectionCompleted(result), Is.False);
        var completed = coordinator.Get(ticket.CaptureId);
        Assert.That(completed.ReadyForProductionResult, Is.True);
        Assert.That(completed.Phase, Is.EqualTo("INSPECTION_COMPLETED"));
        Assert.That(completed.InspectionChannels, Is.EquivalentTo(new[] { "CH06" }));
        Assert.That(completed.Outcomes["CH06"], Is.EqualTo(ChannelOutcome.Ok));
        Assert.That(coordinator.PendingCamDone(), Is.Empty);
    }

    [Test]
    public void St05RealOpenCvRequestFirstThenFramePersistsResultAndReplaysOrderedEvents()
    {
        var coordinator = Coordinator();
        var assets = AssetLedger();
        var item = Item();
        var result = NativeArea(assets, item);
        using (var image = assets.LoadVerified(result.FrameId))
        {
            Assert.That(image.Rows, Is.EqualTo(48));
            Assert.That(Cv2.CountNonZero(image), Is.GreaterThan(0));
        }

        CompleteArea(coordinator, item, result, frameFirst: false);
        var store = new ProductionStore(Path.Combine(_root, "master", "production.db"));
        store.CreateAndQueueRegistration(item,
            AOI.Contracts.PeerEnvelope.Create("master", "slave", 1, item,
                "workpiece_registered", item));
        store.SaveLocalResult(result);
        Assert.That(store.ResultsFor(item.WorkpieceId), Has.Count.EqualTo(1));

        var reopened = Coordinator();
        var events = reopened.EventsFor(result.CaptureId).Select(e => e.Kind).ToArray();
        Assert.That(events, Is.EqualTo(new[]
        {
            CaptureHandshakeEventKind.CaptureExpected,
            CaptureHandshakeEventKind.PlcRequestObserved,
            CaptureHandshakeEventKind.FrameAcquired,
            CaptureHandshakeEventKind.FrameValidated,
            CaptureHandshakeEventKind.CamDoneConfirmed,
            CaptureHandshakeEventKind.InspectionCompleted
        }));
        Assert.That(reopened.Get(result.CaptureId).ReadyForProductionResult, Is.True);
        Assert.That(reopened.CompletedResults(result.CaptureId), Is.EqualTo(new[] { result }));
        Console.WriteLine("A1_ST05_REQUEST_FIRST_PASS " + reopened.ExportJson());
    }

    [Test]
    public void St05HardTriggeredFrameBeforePlcRequestUsesSameCaptureIdWithoutSoftTrigger()
    {
        var coordinator = Coordinator();
        var item = Item();
        var result = NativeArea(AssetLedger(), item);
        CompleteArea(coordinator, item, result, frameFirst: true);
        var sequence = coordinator.EventsFor(result.CaptureId).Select(e => e.Kind).ToArray();
        Assert.That(Array.IndexOf(sequence, CaptureHandshakeEventKind.FrameAcquired),
            Is.LessThan(Array.IndexOf(sequence, CaptureHandshakeEventKind.PlcRequestObserved)));
        Assert.That(Coordinator().Get(result.CaptureId).ReadyForProductionResult, Is.True);
        Console.WriteLine("A1_ST05_FRAME_FIRST_PASS capture=" + result.CaptureId);
    }

    [Test]
    public void MissingFrameTimesOutWithErrorCamDoneButDoesNotBlockSecondWorkpiece()
    {
        var coordinator = Coordinator();
        var first = Item();
        var second = Item(); // same barcode; a different physical item
        var missing = new CaptureTicket(first, Guid.NewGuid(), 1, "HIK-M-2",
            DateTimeOffset.UtcNow.AddSeconds(10), true);
        Assert.That(coordinator.ExpectCapture(missing), Is.True);
        Assert.That(coordinator.ObservePlcRequest(missing.CaptureId, "D1011"), Is.True);
        var expired = coordinator.ExpireDue(missing.ExpiresAtUtc.AddSeconds(1));
        Assert.That(expired, Has.Count.EqualTo(1));
        Assert.That(expired[0].FailureReason, Is.EqualTo("FRAME_MISSING_TIMEOUT"));
        Assert.That(expired[0].PendingCamDone, Is.EqualTo((ushort)3));
        Assert.That(coordinator.CamDoneConfirmed(missing.CaptureId, 3), Is.True);
        Assert.That(coordinator.Get(missing.CaptureId).Phase, Is.EqualTo("FAILED"));

        var result = NativeArea(AssetLedger(), second);
        CompleteArea(coordinator, second, result, frameFirst: true);
        Assert.That(coordinator.Get(missing.CaptureId).ReadyForProductionResult, Is.False);
        Assert.That(coordinator.Get(result.CaptureId).ReadyForProductionResult, Is.True);
        Assert.That(Coordinator().Get(missing.CaptureId).FailureReason,
            Is.EqualTo("FRAME_MISSING_TIMEOUT"));
        Console.WriteLine("A1_ST05_MISSING_FRAME_ISOLATED_PASS failed=1 healthy=1 ack_error=3");
    }

    [Test]
    public void WrongPhysicalItemAndWrongEpochCannotBorrowFrameEvenWhenBarcodeMatches()
    {
        var coordinator = Coordinator();
        var assets = AssetLedger();
        var a = Item();
        var b = Item();
        var result = NativeArea(assets, a);
        var ticket = Ticket(a, result);
        coordinator.ExpectCapture(ticket);
        var frame = Evidence(a, result);
        Assert.Throws<InvalidDataException>(() =>
            coordinator.FrameAcquired(frame with { WorkpieceId = b.WorkpieceId }));
        Assert.Throws<InvalidDataException>(() =>
            coordinator.FrameAcquired(frame with { Epoch = a.Epoch - 1 }));
        Assert.Throws<InvalidDataException>(() =>
            coordinator.ObservePlcRequest(ticket.CaptureId, "D1012"));
        Assert.That(coordinator.Get(ticket.CaptureId).FrameAcquired, Is.False);
        Assert.That(coordinator.Get(ticket.CaptureId).PlcRequestObserved, Is.False);
        CompleteAreaAfterExistingTicket(coordinator, ticket, frame, result);
    }

    private static void CompleteAreaAfterExistingTicket(CaptureHandshakeCoordinator c,
        CaptureTicket ticket, CaptureFrameEvidence frame, ChannelResult result)
    {
        Assert.That(c.ObservePlcRequest(ticket.CaptureId, ticket.PlcAddress), Is.True);
        Assert.That(c.FrameAcquired(frame), Is.True);
        c.FrameValidated(ticket.CaptureId, frame.SourceFrameId);
        c.CamDoneConfirmed(ticket.CaptureId, 2);
        c.InspectionCompleted(result);
        Assert.That(c.Get(ticket.CaptureId).ReadyForProductionResult, Is.True);
    }

    [Test]
    public void BadFrameIsJournaledWithoutInterruptingAHealthyPhysicalCapture()
    {
        var coordinator = Coordinator();
        var item = Item();
        var foreignItem = Item();
        var result = NativeArea(AssetLedger(), item);
        var ticket = Ticket(item, result);
        coordinator.ExpectCapture(ticket);
        var wrong = Evidence(item, result) with { WorkpieceId = foreignItem.WorkpieceId };
        Assert.That(coordinator.TryFrameAcquired(wrong), Is.False);
        Assert.That(coordinator.Get(ticket.CaptureId).RejectedFrameCount, Is.EqualTo(1));
        Assert.That(coordinator.Get(ticket.CaptureId).FrameAcquired, Is.False);
        Assert.That(coordinator.Get(ticket.CaptureId).LastRejectedFrameReason,
            Is.EqualTo("FRAME_IDENTITY_OR_SOURCE_MISMATCH"));

        CompleteAreaAfterExistingTicket(coordinator, ticket, Evidence(item, result), result);
        var reopened = Coordinator();
        Assert.That(reopened.Get(ticket.CaptureId).RejectedFrameCount, Is.EqualTo(1));
        Assert.That(reopened.Get(ticket.CaptureId).ReadyForProductionResult, Is.True);
        Assert.That(reopened.EventsFor(ticket.CaptureId).Count(e =>
            e.Kind == CaptureHandshakeEventKind.FrameRejected), Is.EqualTo(1));
        Console.WriteLine("A1_MISBOUND_FRAME_ISOLATED_PASS rejects=1 valid_capture=1");
    }

    [Test]
    public void AlgorithmErrorRemainsExportableButNeverQualifiesAsGoodFinalEvidence()
    {
        var coordinator = Coordinator();
        var item = Item();
        var result = NativeArea(AssetLedger(), item);
        var ticket = Ticket(item, result);
        coordinator.ExpectCapture(ticket);
        coordinator.ObservePlcRequest(ticket.CaptureId, "D1011");
        var frame = Evidence(item, result);
        coordinator.FrameAcquired(frame);
        coordinator.FrameValidated(ticket.CaptureId, frame.SourceFrameId);
        coordinator.CamDoneConfirmed(ticket.CaptureId, 2);
        var failedInspection = result with { Outcome = ChannelOutcome.Error };
        coordinator.InspectionCompleted(failedInspection);
        var progress = coordinator.Get(ticket.CaptureId);
        Assert.That(progress.Phase, Is.EqualTo("INSPECTION_ERROR"));
        Assert.That(progress.ReadyForProductionResult, Is.False);
        Assert.That(coordinator.CompletedResults(ticket.CaptureId),
            Is.EqualTo(new[] { failedInspection }),
            "ERROR should remain available for durable per-item trace, but never become GOOD.");
        Console.WriteLine("A1_ALGORITHM_ERROR_ISOLATED_PASS outcome=ERROR good_eligible=0");
    }

    [Test]
    public void RetakeAttemptTwoOverridesFailedAttemptAndLateFirstFrameNeverBecomesCurrent()
    {
        var coordinator = Coordinator();
        var assets = AssetLedger();
        var item = Item();
        var first = NativeArea(assets, item);
        var ticket1 = Ticket(item, first);
        coordinator.ExpectCapture(ticket1);
        coordinator.ObservePlcRequest(ticket1.CaptureId, ticket1.PlcAddress);
        coordinator.FailCapture(ticket1.CaptureId, "CAMERA_NO_FRAME");
        coordinator.CamDoneConfirmed(ticket1.CaptureId, 3);
        var second = NativeArea(assets, item, attemptNo: 2);
        var ticket2 = Ticket(item, second);
        Assert.That(coordinator.ExpectCapture(ticket2), Is.True);
        Assert.That(coordinator.FrameAcquired(Evidence(item, first)), Is.False);
        Assert.That(coordinator.Get(ticket1.CaptureId).Phase, Is.EqualTo("SUPERSEDED"));
        Assert.That(coordinator.Get(ticket2.CaptureId).Phase, Is.EqualTo("CAPTURE_EXPECTED"));
        Assert.That(coordinator.Latest(item.WorkpieceId, "HIK-M-2")?.Ticket.AttemptNo,
            Is.EqualTo(2));
        CompleteAreaAfterExistingTicket(coordinator, ticket2, Evidence(item, second), second);
        Assert.That(coordinator.InspectionCompleted(first), Is.False);
        Assert.That(coordinator.Get(ticket2.CaptureId).ReadyForProductionResult, Is.True);
    }

    [Test]
    public void OneLineCameraSharedParentRequiresTwoDistinctInspectionChannelResults()
    {
        var coordinator = Coordinator();
        var item = Item();
        var assets = AssetLedger();
        using var parent = new Mat(32, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(parent, new Rect(4, 4, 24, 24), Scalar.White, -1);
        Cv2.Rectangle(parent, new Rect(36, 4, 24, 24), Scalar.White, -1);
        var pair = assets.CaptureLinePair(item, "LINE-S-8", parent,
            new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32),
            simRule: new SimPixelRule(127, 0.2));
        Assert.That(pair, Has.Count.EqualTo(2));
        Assert.That(pair[0].CaptureId, Is.EqualTo(pair[1].CaptureId));
        var ticket = Ticket(item, pair[0]);
        Assert.That(ticket.ChannelIds, Is.EquivalentTo(new[] { "CH08", "CH09" }));
        var frame = Evidence(item, pair[0]);
        Assert.That(frame.SourceFrameId, Is.EqualTo(pair[1].ParentFrameId));

        coordinator.ExpectCapture(ticket);
        coordinator.FrameAcquired(frame);
        coordinator.FrameValidated(ticket.CaptureId, frame.SourceFrameId);
        coordinator.ObservePlcRequest(ticket.CaptureId, "D1013");
        Assert.That(coordinator.PendingCamDone().Single().PendingCamDone, Is.EqualTo((ushort)2));
        coordinator.CamDoneConfirmed(ticket.CaptureId, 2);
        coordinator.InspectionCompleted(pair[0]);
        Assert.That(coordinator.CompletedResults(ticket.CaptureId), Is.Empty);
        Assert.That(coordinator.Get(ticket.CaptureId).HasAllInspections, Is.False);
        coordinator.InspectionCompleted(pair[1]);
        Assert.That(coordinator.CompletedResults(ticket.CaptureId),
            Is.EquivalentTo(pair));
        Assert.That(coordinator.Get(ticket.CaptureId).ReadyForProductionResult, Is.True);
        Assert.That(coordinator.Get(ticket.CaptureId).InspectionChannels,
            Is.EquivalentTo(new[] { "CH08", "CH09" }));
        Console.WriteLine("A1_LINE_PARENT_TWO_RESULTS_PASS capture=" + ticket.CaptureId);
    }
}
