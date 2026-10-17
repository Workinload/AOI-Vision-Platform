using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class Agent01ProductionFlowTests
{
    private string _root = string.Empty;
    private string JournalPath => Path.Combine(_root, "master", "station-progress.db");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aoi-stations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static WorkpieceIdentity Item(string barcode = "SIM-DUPLICATE") =>
        WorkpieceIdentity.New(barcode, 7, "SIM_LIGHT_TEST_RELEASE");

    private static ChannelResult Result(WorkpieceIdentity item, string channel,
        ChannelOutcome outcome = ChannelOutcome.Ok)
    {
        var definition = ChannelCatalog.Require(channel);
        return new ChannelResult(Guid.NewGuid(), item.WorkpieceId, item.Epoch, item.ReleaseId,
            item.TraceId, channel, definition.StationId, definition.PhysicalDeviceId,
            Guid.NewGuid(), Guid.NewGuid(),
            definition.SharedFrameGroup is null ? null : Guid.NewGuid(),
            1, new string('a', 64), outcome, true, DateTimeOffset.UtcNow);
    }

    private static void FeedStation(ProductionFlowCoordinator flow,
        WorkpieceIdentity item, string station, IReadOnlyList<ChannelResult> captures,
        ProductionStore? productionStore = null)
    {
        Assert.That(flow.EnterStation(item.WorkpieceId, station), Is.True);
        foreach (var frame in captures.Where(c => c.StationId == station))
        {
            productionStore?.SaveLocalResult(frame);
            Assert.That(flow.RecordChannel(frame), Is.True);
        }
        Assert.That(flow.CompleteStation(item.WorkpieceId, station), Is.True);
    }

    [Test]
    public void FiveRepeatedBarcodeItemsAdvanceSevenStationsUsingRealOpenCvPixelsAndDurableFinals()
    {
        var flow = new ProductionFlowCoordinator(JournalPath);
        var master = new ProductionStore(Path.Combine(_root, "master", "production.db"));
        var assetLedger = new VisionAssetLedger(Path.Combine(_root, "master", "vision"));
        var items = Enumerable.Range(0, 5).Select(_ => Item()).ToArray();
        var frames = new Dictionary<Guid, IReadOnlyList<ChannelResult>>();

        foreach (var item in items)
        {
            var registration = PeerEnvelope.Create("master", "slave", 1, item,
                "workpiece_registered", item);
            master.CreateAndQueueRegistration(item, registration);
            Assert.That(flow.Register(item), Is.True);
            // Real OpenCvSharp5 synthetic pixels, lossless assets and A3 lineage, not dummy
            // result counts. A0 can supply identical DTOs from imported image capture.
            var actualFrames = SimVisionFixture.CaptureAllMappedChannels(
                item, assetLedger, new SimPixelRule(127, 0.1));
            Assert.That(actualFrames, Has.Count.EqualTo(11));
            frames.Add(item.WorkpieceId, actualFrames);
        }

        // Interleaved station events: at each station five distinct physical items
        // enter simultaneously, then finish independently. The A2 machine owns timing.
        long beforeSt03 = 0;
        foreach (string station in ProductionFlowCoordinator.StationOrder)
        {
            foreach (var item in items)
                Assert.That(flow.EnterStation(item.WorkpieceId, station), Is.True);
            foreach (var item in items.Reverse())
            {
                foreach (var frame in frames[item.WorkpieceId].Where(x => x.StationId == station))
                {
                    master.SaveLocalResult(frame);
                    Assert.That(flow.RecordChannel(frame), Is.True);
                }
                Assert.That(flow.CompleteStation(item.WorkpieceId, station), Is.True);
            }
            if (station == "LOADING")
                beforeSt03 = flow.Get(items[0].WorkpieceId).LastSequence;
        }

        Assert.That(flow.GetAll(), Has.Count.EqualTo(5));
        Assert.That(flow.GetAll().Select(x => x.Workpiece.WorkpieceId).Distinct().Count(),
            Is.EqualTo(5), "Same barcode must never merge physical items.");
        Assert.That(flow.GetAll().All(x => x.CapturedChannelCount == 11 &&
            x.CompletedStationCount == 7 && x.CurrentStationId == "ST10"), Is.True);
        Assert.That(flow.Replay(items[0].WorkpieceId, beforeSt03).CapturedChannelCount,
            Is.EqualTo(1), "Historic replay must not leak later captures into an old position.");

        foreach (var item in items)
        {
            Assert.That(flow.EvaluateFinal(item.WorkpieceId), Is.Null,
                "No end barcode is not a completed final.");
            Assert.That(flow.ObserveEndBarcode(item.WorkpieceId, item.Barcode), Is.True);
            var candidate = flow.EvaluateFinal(item.WorkpieceId);
            Assert.That(candidate, Is.Not.Null);
            Assert.That(candidate!.Disposition, Is.EqualTo(FinalDisposition.Good));
            Assert.That(candidate.IsSimulation, Is.True);
            // First the existing Master Core WAL stores the final, then the flow records
            // persistence. Virtual PLC token ACK is a distinct final event.
            master.StoreFinal(item, candidate);
            Assert.That(flow.FinalPersisted(item.WorkpieceId, candidate), Is.True);
            Assert.That(flow.EjectionConfirmed(item.WorkpieceId), Is.True);
            Assert.That(master.LatestFinal(item.WorkpieceId)?.Disposition,
                Is.EqualTo(FinalDisposition.Good));
        }

        var snapshots = flow.GetAll();
        Assert.That(snapshots.All(x => x.Phase == "EJECTED" && x.Ejected &&
            x.CurrentStationId == "OUTFEED" && x.CapturedChannelCount == 11), Is.True);
        Assert.That(master.ReadProductionOverview().Total, Is.EqualTo(5));
        Assert.That(assetLedger.Stats().ChannelResults, Is.EqualTo(55));

        var reopened = new ProductionFlowCoordinator(JournalPath);
        Assert.That(reopened.GetAll().Select(x => x.Workpiece.WorkpieceId),
            Is.EquivalentTo(items.Select(x => x.WorkpieceId)));
        Assert.That(reopened.GetAll().All(x => x.Ejected && x.CapturedChannelCount == 11), Is.True);
        Console.WriteLine("AGENT01_FIVE_WORKPIECE_PROGRESS_JSON=" + reopened.ExportJson());
        Console.WriteLine("AGENT01_FLOW_DEMO_PASS items=5 unique_ids=5 stations=7 channels=55 " +
            "opencv_assets=65 persisted_finals=5 ejected=5 sqlite_replay=5");
    }

    [Test]
    public void OutOfOrderRemoteCaptureBelongsToItsOwnWorkpieceNeverTheSameBarcodePeer()
    {
        var flow = new ProductionFlowCoordinator(JournalPath);
        var a = Item();
        var b = Item();
        flow.Register(a);
        flow.Register(b);
        var bSt03 = Result(b, "CH02");
        Assert.That(flow.RecordChannel(bSt03), Is.True); // arrives before ST03 sensor
        Assert.That(flow.Get(b.WorkpieceId).CapturedChannels, Does.Contain("CH02"));
        Assert.That(flow.Get(a.WorkpieceId).CapturedChannelCount, Is.Zero);
        Assert.That(flow.RecordChannel(bSt03), Is.False); // same PeerLink retransmission
        Assert.Throws<InvalidOperationException>(() =>
            flow.RecordChannel(bSt03 with { WorkpieceId = a.WorkpieceId }));
        Assert.Throws<InvalidOperationException>(() =>
            flow.EnterStation(a.WorkpieceId, "ST04")); // cannot skip LOADING
        Assert.That(flow.EnterStation(a.WorkpieceId, "LOADING"), Is.True);
        Assert.Throws<InvalidOperationException>(() =>
            flow.CompleteStation(a.WorkpieceId, "LOADING")); // positioning missing
        flow.RecordChannel(Result(a, "CH01"));
        Assert.That(flow.CompleteStation(a.WorkpieceId, "LOADING"), Is.True);
        Assert.That(flow.Get(b.WorkpieceId).CapturedChannelCount, Is.EqualTo(1));
        Assert.That(flow.Get(a.WorkpieceId).CapturedChannelCount, Is.EqualTo(1));
    }

    [Test]
    public void WrongEndBarcodeHasElevenCapturedChannelsButCannotSubmitFinalOrEject()
    {
        var flow = new ProductionFlowCoordinator(JournalPath);
        var item = Item();
        flow.Register(item);
        var captured = ChannelCatalog.All.Select(x => Result(item, x.ChannelId)).ToArray();
        foreach (var station in ProductionFlowCoordinator.StationOrder)
            FeedStation(flow, item, station, captured);
        Assert.That(flow.ObserveEndBarcode(item.WorkpieceId, "OTHER_BARCODE"), Is.True);
        var progress = flow.Get(item.WorkpieceId);
        Assert.That(progress.CapturedChannelCount, Is.EqualTo(11));
        Assert.That(progress.CompletedStationCount, Is.EqualTo(7));
        Assert.That(progress.Phase, Is.EqualTo("END_BARCODE_MISMATCH"));
        Assert.That(progress.EndBarcodeMatches, Is.False);
        Assert.That(flow.EvaluateFinal(item.WorkpieceId), Is.Null);
        Assert.Throws<InvalidOperationException>(() =>
            flow.FinalPersisted(item.WorkpieceId, new FinalDecision(
                FinalDisposition.Good, true, [], [], "COMPLETE_OK:SIM_ONLY")));
        Assert.Throws<InvalidOperationException>(() =>
            flow.EjectionConfirmed(item.WorkpieceId));
        Console.WriteLine("AGENT01_FLOW_NEGATIVE_PASS wrong_end_barcode=1 final=0 eject=0");
    }

    [Test]
    public void ReplayingHistoricProgressIsStableAcrossReopenAndRepeatedStationEvents()
    {
        var flow = new ProductionFlowCoordinator(JournalPath);
        var item = Item();
        Assert.That(flow.Register(item), Is.True);
        Assert.That(flow.Register(item), Is.False);
        Assert.That(flow.EnterStation(item.WorkpieceId, "LOADING"), Is.True);
        Assert.That(flow.EnterStation(item.WorkpieceId, "LOADING"), Is.False);
        var capture = Result(item, "CH01");
        flow.RecordChannel(capture);
        Assert.That(flow.RecordChannel(capture), Is.False);
        long beforeCompleted = flow.Get(item.WorkpieceId).LastSequence;
        Assert.That(flow.CompleteStation(item.WorkpieceId, "LOADING"), Is.True);
        Assert.That(flow.CompleteStation(item.WorkpieceId, "LOADING"), Is.False);

        var historic = flow.Replay(item.WorkpieceId, beforeCompleted);
        Assert.That(historic.CapturedChannelCount, Is.EqualTo(1));
        Assert.That(historic.CompletedStationCount, Is.Zero);
        Assert.That(historic.CurrentStationId, Is.EqualTo("LOADING"));
        var restored = new ProductionFlowCoordinator(JournalPath);
        Assert.That(restored.Get(item.WorkpieceId).CompletedStationCount, Is.EqualTo(1));
        Assert.That(restored.Replay(item.WorkpieceId, beforeCompleted).CapturedChannels, Is.EquivalentTo(historic.CapturedChannels));
        Assert.That(restored.Replay(item.WorkpieceId, beforeCompleted).CompletedStationCount, Is.EqualTo(historic.CompletedStationCount));
        Assert.That(restored.EventsFor(item.WorkpieceId), Has.Count.EqualTo(4));
        Assert.Throws<InvalidDataException>(() =>
            restored.Register(item with { ReleaseId = "WRONG_RELEASE" }));
    }
}
