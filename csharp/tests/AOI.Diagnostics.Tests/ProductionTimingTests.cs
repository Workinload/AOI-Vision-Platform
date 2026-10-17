using AOI.Contracts;
using AOI.Diagnostics;
using AOI.Domain;
using AOI.Runtime;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;

namespace AOI.Diagnostics.Tests;

[TestFixture]
public sealed class ProductionTimingTests
{
    private string _root = "";
    private const string Device = "HIK-M-2";
    private const string Channel = "CH06";
    private const string Address = "D1011";

    [SetUp] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "a7-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown] public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ProductionTimingJournal Journal(string node = "master") =>
        new(Path.Combine(_root, node), node);

    private static WorkpieceIdentity Item(string barcode = "REPEAT") =>
        WorkpieceIdentity.New(barcode, 9, "SIM_RELEASE_P0");

    private static TimingObservation Capture(WorkpieceIdentity item, TimingStage stage,
        Guid capture, Guid? frame = null, Guid? operation = null,
        ushort? value = null, byte? sid = null) =>
        new(item, stage, capture, Device, Channel, 1, frame, operation,
            PlcDmAddress: stage is TimingStage.PlcCaptureRequestObserved or
                TimingStage.CamDoneSent or TimingStage.CamDoneAcknowledged ? Address : null,
            PlcValue: stage == TimingStage.PlcCaptureRequestObserved ? (ushort)1 : value,
            FinsSid: sid);

    private static TimingObservation Final(WorkpieceIdentity item, TimingStage stage,
        Guid operation, string address, ushort value, byte sid) =>
        new(item, stage, OperationId: operation, PlcDmAddress: address, PlcValue: value, FinsSid: sid);

    [TestCase(false)]
    [TestCase(true)]
    public void PlcRequestBeforeFrameOrFrameBeforeRequest_ProducesActualPerCaptureCausalTimeline(bool frameFirst)
    {
        var item = Item();
        var journal = Journal();
        Guid cap = Guid.NewGuid(), frame = Guid.NewGuid(), op = Guid.NewGuid();
        var request = Capture(item, TimingStage.PlcCaptureRequestObserved, cap);
        var arrived = Capture(item, TimingStage.FrameArrived, cap, frame);
        if (frameFirst) journal.Append(arrived);
        journal.Append(request);
        if (!frameFirst) journal.Append(arrived);
        journal.Append(Capture(item, TimingStage.FrameReady, cap, frame));
        journal.Append(Capture(item, TimingStage.CamDoneSent, cap, operation: op, value: 2, sid: 7));
        journal.Append(Capture(item, TimingStage.CamDoneAcknowledged, cap, operation: op, value: 2, sid: 7));
        var result = new ChannelResult(Guid.NewGuid(), item.WorkpieceId, item.Epoch, item.ReleaseId,
            item.TraceId, Channel, "ST05", Device, cap, frame, null, 1,
            new string('a', 64), ChannelOutcome.Ok, true, DateTimeOffset.UtcNow);
        journal.Append(TimingObservation.FromResult(item, result));

        var queried = new ProductionTimelineReader(_root).Query(
            new TimingFilter(item.WorkpieceId, CaptureId: cap));
        Assert.That(queried.Events, Has.Length.EqualTo(6));
        Assert.That(queried.Events.All(x => x.ProcessRunId == journal.ProcessRunId &&
            x.CaptureId == cap && x.PhysicalDeviceId == Device &&
            x.MonotonicTick is > 0 && x.TickFrequency is > 0), Is.True);
        var expected = new[] {
            new TimingExpectation(frameFirst ? TimingStage.FrameArrived : TimingStage.PlcCaptureRequestObserved),
            new TimingExpectation(frameFirst ? TimingStage.PlcCaptureRequestObserved : TimingStage.FrameArrived),
            new TimingExpectation(TimingStage.FrameReady),
            new TimingExpectation(TimingStage.CamDoneSent, PlcDmAddress: Address, PlcValue: 2),
            new TimingExpectation(TimingStage.CamDoneAcknowledged, PlcDmAddress: Address,
                PlcValue: 2, MaxSincePrevious: TimeSpan.FromSeconds(2)),
            new TimingExpectation(TimingStage.InspectionCompleted)
        };
        var comparison = ProductionTimingAnalyzer.Compare(item.WorkpieceId, expected, queried.Events);
        Assert.That(comparison.IsMatch, Is.True, DiagnosticsFormat.Serialize(comparison));
        Assert.That(queried.DiagnosticDifferences, Is.Empty);
        var csv = Path.Combine(_root, "timing", "audit.csv");
        var json = Path.Combine(_root, "timing", "audit.json");
        ProductionTimelineExports.SaveCsv(queried, csv);
        ProductionTimelineExports.SaveJson(queried, json);
        Assert.That(File.ReadAllText(csv), Does.Contain("CamDoneAcknowledged"));
        Assert.That(File.ReadAllText(json), Does.Contain(item.WorkpieceId.ToString("D")));
    }

    [Test]
    public void MissingFrame_WithWrongCamDoneSuccess_IsDiagnosedNotShownAsCaptured()
    {
        var item = Item();
        var journal = Journal();
        Guid cap = Guid.NewGuid(), op = Guid.NewGuid();
        journal.Append(Capture(item, TimingStage.PlcCaptureRequestObserved, cap));
        journal.Append(Capture(item, TimingStage.CamDoneSent, cap, operation: op, value: 2, sid: 4));
        journal.Append(Capture(item, TimingStage.CamDoneAcknowledged, cap, operation: op, value: 2, sid: 4));
        var timeline = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        Assert.That(timeline.DiagnosticDifferences.Select(d => d.Code),
            Does.Contain("CAMDONE_OK_WITHOUT_FRAME_READY"));
        var expected = new[] {
            new TimingExpectation(TimingStage.PlcCaptureRequestObserved),
            new TimingExpectation(TimingStage.FrameReady),
            new TimingExpectation(TimingStage.CamDoneAcknowledged, PlcDmAddress: Address, PlcValue: 2)
        };
        var comparison = ProductionTimingAnalyzer.Compare(item.WorkpieceId, expected, timeline.Events);
        Assert.That(comparison.IsMatch, Is.False);
        Assert.That(comparison.Differences.Select(d => d.Code),
            Does.Contain("EXPECTED_EVENT_NOT_OBSERVED"));
    }

    [Test]
    public void FrameReusedForTwoCaptures_IsExplicitlyReported()
    {
        var item = Item();
        var journal = Journal();
        Guid frame = Guid.NewGuid();
        journal.Append(Capture(item, TimingStage.FrameArrived, Guid.NewGuid(), frame));
        journal.Append(Capture(item, TimingStage.FrameArrived, Guid.NewGuid(), frame));
        var timeline = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        Assert.That(timeline.DiagnosticDifferences.Select(x => x.Code),
            Does.Contain("FRAME_REUSED_ACROSS_CAPTURES"));
    }

    [Test]
    public void RealMasterProductionWalLegacyEvents_AreQueryableButDoNotFakeCamDone()
    {
        var item = Item();
        var store = new ProductionStore(Path.Combine(_root, "master", "production.db"));
        store.CreateAndQueueRegistration(item, PeerEnvelope.Create(
            "master", "slave", 1, item, "workpiece_registered", item));
        var timeline = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        Assert.That(timeline.Events.Any(e => e.Origin == "CORE_WAL_LEGACY" &&
            e.Status == "WORKPIECE_REGISTERED_LOCAL"), Is.True);
        Assert.That(timeline.Events.All(e => e.MonotonicTick is null), Is.True);
        var expected = new[] { new TimingExpectation(TimingStage.CamDoneAcknowledged) };
        Assert.That(ProductionTimingAnalyzer.Compare(item.WorkpieceId, expected, timeline.Events).IsMatch, Is.False);
    }

    [Test]
    public void ChannelOwnerAndIdentifierMismatch_CannotBeWritten()
    {
        var item = Item();
        var journal = Journal();
        var wrong = new TimingObservation(item, TimingStage.FrameReady, Guid.NewGuid(),
            "HIK-S-2", "CH06", 1, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => journal.Append(wrong));
        var timeline = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        Assert.That(timeline.Events, Is.Empty);
    }

    [Test]
    public void WrongActualPlcValueAndMonotonicDeadline_AreObservableFailures()
    {
        var item = Item();
        var journal = Journal();
        Guid op = Guid.NewGuid();
        var first = journal.Append(Final(item, TimingStage.FinalPlcWriteSent, op, "D1016", 2, 10));
        Thread.Sleep(25);
        var ack = journal.Append(Final(item, TimingStage.FinalPlcWriteAcknowledged, op, "D1016", 2, 10));
        Assert.That(ProductionTimingAnalyzer.Elapsed(first, ack), Is.GreaterThan(TimeSpan.FromMilliseconds(1)));
        var comparison = ProductionTimingAnalyzer.Compare(item.WorkpieceId, [
            new TimingExpectation(TimingStage.FinalPlcWriteSent, PlcDmAddress: "D1016", PlcValue: 2),
            new TimingExpectation(TimingStage.FinalPlcWriteAcknowledged,
                PlcDmAddress: "D1016", PlcValue: 1, MaxSincePrevious: TimeSpan.FromMilliseconds(1))
        ], new[] { first, ack });
        Assert.That(comparison.Differences.Select(x => x.Code), Does.Contain("PLC_VALUE_MISMATCH"));
        Assert.That(comparison.IsMatch, Is.False);
        var timeout = ProductionTimingAnalyzer.Compare(item.WorkpieceId, [
            new TimingExpectation(TimingStage.FinalPlcWriteSent),
            new TimingExpectation(TimingStage.FinalPlcWriteAcknowledged,
                MaxSincePrevious: TimeSpan.FromMilliseconds(1))
        ], [first, ack]);
        Assert.That(timeout.Differences.Select(x => x.Code), Does.Contain("DEADLINE_EXCEEDED"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FinalPlcAcknowledgedWrites_FollowLegacyGoodOrBarcodeFailureBranches(bool barcodeError)
    {
        var item = Item();
        var journal = Journal();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        void Write(Guid op, byte sid, string address, ushort value)
        {
            journal.Append(Final(item, TimingStage.FinalPlcWriteSent, op, address, value, sid));
            journal.Append(Final(item, TimingStage.FinalPlcWriteAcknowledged, op, address, value, sid));
        }
        if (barcodeError)
        {
            Write(a, 4, "D2099", 4);
            Write(b, 5, "D1016", 2);
        }
        else
        {
            Write(a, 4, "D1016", 1);
            Write(b, 5, "D2099", 2);
        }
        var timeline = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        Assert.That(timeline.DiagnosticDifferences, Is.Empty);
    }

    [Test]
    public void LegacyFinalWritesReversed_AreIdentifiedFromAckOrderNotMemoryMirror()
    {
        var item = Item();
        var journal = Journal();
        foreach (var (address, value, sid) in new[] {
            ("D2099",(ushort)2,(byte)5), ("D1016",(ushort)1,(byte)6) })
        {
            Guid op = Guid.NewGuid();
            journal.Append(Final(item, TimingStage.FinalPlcWriteSent, op, address, value, sid));
            journal.Append(Final(item, TimingStage.FinalPlcWriteAcknowledged, op, address, value, sid));
        }
        Assert.That(new ProductionTimelineReader(_root)
            .Query(new TimingFilter(item.WorkpieceId)).DiagnosticDifferences.Select(x => x.Code),
            Does.Contain("LEGACY_FINAL_FINS_ORDER_MISMATCH"));
    }

    [Test]
    public void SeparateProcessRunsHaveIncomparableStopwatches()
    {
        var item = Item();
        var one = Journal();
        var early = one.Append(Final(item, TimingStage.FinalPlcWriteSent,
            Guid.NewGuid(), "D1016", 2, 1));
        var two = Journal();
        var later = two.Append(Final(item, TimingStage.FinalPlcWriteSent,
            Guid.NewGuid(), "D2099", 2, 2));
        Assert.That(ProductionTimingAnalyzer.Elapsed(early, later), Is.Null);
        var comparison = ProductionTimingAnalyzer.Compare(item.WorkpieceId, [
            new TimingExpectation(TimingStage.FinalPlcWriteSent, PlcDmAddress: "D1016"),
            new TimingExpectation(TimingStage.FinalPlcWriteSent, PlcDmAddress: "D2099")
        ], [early, later]);
        Assert.That(comparison.Differences.Select(x => x.Code),
            Does.Contain("CROSS_PROCESS_CLOCK_UNVERIFIED"));
    }

    [Test]
    public async Task ActualUdpFinsRequestAndResponse_CanBeCorrelatedToOnePhysicalCapture()
    {
        // Actual loopback UDP wire exchange through the existing A2 FINS codec,
        // NOT an internal virtual PLC memory-only assertion or REAL PLC connection.
        var item = Item();
        var journal = Journal();
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        var plc = new VirtualPlc();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task<byte[]> Exchange(byte[] packet)
        {
            await client.SendAsync(packet, destination, deadline.Token);
            var received = await server.ReceiveAsync(deadline.Token);
            var response = plc.Handle(received.Buffer);
            Assert.That(response, Is.Not.Empty);
            await server.SendAsync(response, received.RemoteEndPoint, deadline.Token);
            return (await client.ReceiveAsync(deadline.Token)).Buffer;
        }

        // Set the simulated PLC D1011 externally through real UDP write, not memory mutation.
        byte[] setRequest = FinsDm.Request(true, 1011, new ushort[] { 1 }, 30);
        _ = FinsDm.DecodeResponse(setRequest, await Exchange(setRequest));

        Guid cap = Guid.NewGuid(), op = Guid.NewGuid();
        byte[] readRequest = FinsDm.Request(false, 1011, new ushort[] { 0 }, 31);
        journal.Append(new TimingObservation(item, TimingStage.FinsReadSent,
            cap, Device, Channel, 1, OperationId: op,
            PlcDmAddress: Address, FinsSid: 31));
        byte[] readResponse = await Exchange(readRequest);
        var words = FinsDm.DecodeResponse(readRequest, readResponse);
        Assert.That(words, Is.EqualTo(new ushort[] { 1 }));
        journal.Append(new TimingObservation(item, TimingStage.FinsReadCompleted,
            cap, Device, Channel, 1, OperationId: op,
            PlcDmAddress: Address, PlcValue: words[0], FinsSid: 31));
        journal.Append(Capture(item, TimingStage.PlcCaptureRequestObserved, cap));
        var events = new ProductionTimelineReader(_root)
            .Query(new TimingFilter(item.WorkpieceId)).Events;
        Assert.That(events.Select(e => e.Stage),
            Does.Contain(TimingStage.PlcCaptureRequestObserved));
        Assert.That(events.Single(e => e.Stage == TimingStage.FinsReadCompleted).PlcValue,
            Is.EqualTo((ushort)1));
        Assert.That(events.Where(e => e.OperationId == op).Select(e => e.FinsSid)
            .Distinct(), Is.EquivalentTo(new byte?[] { 31 }));
    }

    [Test]
    public void SameFrameIdForDifferentPhysicalWorkpieces_IsFoundEvenWhenQueryFiltersOneItem()
    {
        var journal = Journal();
        var first = Item("BARCODE_REUSED");
        var second = Item("BARCODE_REUSED");
        Guid duplicatedFrame = Guid.NewGuid();
        journal.Append(Capture(first, TimingStage.FrameArrived, Guid.NewGuid(), duplicatedFrame));
        journal.Append(Capture(second, TimingStage.FrameArrived, Guid.NewGuid(), duplicatedFrame));
        var firstOnly = new ProductionTimelineReader(_root)
            .Query(new TimingFilter(first.WorkpieceId));
        Assert.That(firstOnly.Events.All(e => e.WorkpieceId == first.WorkpieceId), Is.True);
        Assert.That(firstOnly.DiagnosticDifferences.Select(d => d.Code),
            Does.Contain("FRAME_REUSED_ACROSS_WORKPIECES"));
        Assert.That(firstOnly.DiagnosticDifferences.Single(d =>
            d.Code == "FRAME_REUSED_ACROSS_WORKPIECES").Detail,
            Does.Contain(second.WorkpieceId.ToString("D")));
    }

    [Test]
    public void MissingFrameAndMissingCamDoneAckAreDiagnosedAfterExplicitWindows()
    {
        var item = Item();
        var journal = Journal();
        Guid capture = Guid.NewGuid();
        var request = journal.Append(Capture(item, TimingStage.PlcCaptureRequestObserved, capture));
        var lateWindow = ProductionTimingAnalyzer.Diagnose([request],
            evaluatedUtc: request.Utc.AddSeconds(11), frameDeadline: TimeSpan.FromSeconds(10));
        Assert.That(lateWindow.Select(d => d.Code),
            Does.Contain("FRAME_NOT_ARRIVED_BEFORE_DEADLINE"));

        var frame = Guid.NewGuid();
        journal.Append(Capture(item, TimingStage.FrameArrived, capture, frame));
        journal.Append(Capture(item, TimingStage.FrameReady, capture, frame));
        Guid op = Guid.NewGuid();
        var sent = journal.Append(Capture(item, TimingStage.CamDoneSent, capture,
            operation: op, value: 2, sid: 41));
        var queried = new ProductionTimelineReader(_root).Query(new TimingFilter(item.WorkpieceId));
        var overdue = ProductionTimingAnalyzer.Diagnose(queried.Events,
            evaluatedUtc: sent.Utc.AddSeconds(3),
            camDoneDeadline: TimeSpan.FromSeconds(2));
        Assert.That(overdue.Select(d => d.Code), Does.Contain("CAMDONE_ACK_TIMEOUT"));
        Assert.That(overdue.Select(d => d.Code), Does.Not.Contain("FRAME_NOT_ARRIVED_BEFORE_DEADLINE"));
    }

}
