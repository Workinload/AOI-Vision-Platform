using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;
using System.Collections.Immutable;

namespace AOI.Tests;

/// <summary>
/// Real localhost UDP datagrams are compared byte-for-byte with the vPLC's
/// independently captured WireHistory. No expected reply is manufactured
/// and no private PLC memory mirror is used as evidence of ACK delivery.
/// </summary>
[TestFixture]
public sealed class Agent02RawFinsObservationTests
{
    private sealed class Live : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _port;
        public LegacyPlcShadow Shadow { get; }
        public LegacyFinsLoopbackClient Client { get; }

        public Live(LegacyExitScenario scenario)
        {
            var fifo = new SimFullVirtualPlc(5, 3,
                flipEndBarcodeToken: scenario == LegacyExitScenario.BarcodeMismatch ? 1 : null);
            Shadow = new LegacyPlcShadow(fifo, LegacyFinsNodes.Demo,
                unreadableEndScanTokens: scenario == LegacyExitScenario.BarcodeFailure
                    ? new ushort[] { 1 } : Array.Empty<ushort>());
            var udp = new LegacyPlcShadowUdp(Shadow, port: 0);
            _server = udp.RunAsync(_stop.Token);
            _port = udp.Listening.GetAwaiter().GetResult();
            Client = Connect();
        }

        public LegacyFinsLoopbackClient Connect() =>
            new(_port, LegacyFinsNodes.Demo, TimeSpan.FromMilliseconds(160));

        public async Task OfferAsync(LegacyExitScenario scenario)
        {
            await Client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [1]);
            await Client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                [1, (ushort)scenario]);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _server; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }

    private static void AssertRealWireMatch(
        IReadOnlyList<LegacyFinsDatagramObservation> timeline,
        LegacyWireTransaction wire, bool expectsReply)
    {
        Assert.That(timeline, Is.Not.Empty);
        Assert.That(timeline.Select(x => x.Sid), Is.All.EqualTo(wire.Sid));
        Assert.That(timeline.Select(x => x.OperationId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(timeline.Select(x => x.RequestBytes.Length), Is.All.EqualTo(wire.RequestHex.Length / 2));
        Assert.That(timeline.All(x =>
            Convert.ToHexString(x.RequestBytes.AsSpan()) == wire.RequestHex), Is.True,
            "Every phase must retain the identical exact UDP request buffer.");
        Assert.That(timeline.Select(x => x.MonotonicTimestamp), Is.Ordered);
        Assert.That(timeline.All(x => x.ResponseBytes.IsDefaultOrEmpty ||
            Convert.ToHexString(x.ResponseBytes.AsSpan()) == wire.ResponseHex), Is.True);
        var prepared = timeline.Single(x => x.Stage == LegacyFinsObservationStage.Prepared);
        var sent = timeline.Single(x => x.Stage == LegacyFinsObservationStage.WireSent);
        Assert.That(prepared.ResponseBytes.IsEmpty, Is.True);
        Assert.That(sent.ResponseBytes.IsEmpty, Is.True);
        Assert.That(prepared.OccurredUtc <= sent.OccurredUtc, Is.True);
        if (expectsReply)
        {
            var received = timeline.Single(x =>
                x.Stage == LegacyFinsObservationStage.ReplyReceived);
            Assert.That(Convert.ToHexString(received.ResponseBytes.AsSpan()),
                Is.EqualTo(wire.ResponseHex));
            Assert.That(received.HasActualReply, Is.True);
        }
        else
        {
            Assert.That(timeline.Any(x => x.Stage == LegacyFinsObservationStage.ReplyReceived),
                Is.False);
            Assert.That(timeline.Any(x => x.HasActualReply), Is.False,
                "A lost ACK must never be reconstructed from expected FINS values.");
        }
    }

    [TestCase(LegacyExitScenario.BarcodeFailure, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, (ushort)4)]
    public async Task ActualScannerRequestThenTwoRealWritesAndIndependentReadback(
        LegacyExitScenario scenario, ushort firstValue)
    {
        await using var live = new Live(scenario);
        await live.OfferAsync(scenario);
        var scanner = new List<LegacyFinsDatagramObservation>();
        Guid scanId = Guid.NewGuid();
        Assert.That((await live.Client.ReadRawObservedAsync(2099, 1, scanId, scanner.Add)),
            Is.EqualTo(new ushort[] { 1 }));
        Assert.That(scanner.Select(x => x.Stage), Is.EqualTo(new[] {
            LegacyFinsObservationStage.Prepared,
            LegacyFinsObservationStage.WireSent,
            LegacyFinsObservationStage.ReplyReceived,
            LegacyFinsObservationStage.AckAccepted,
            LegacyFinsObservationStage.ReadbackVerified }));
        Assert.That(scanner.All(x => x.OperationId == scanId && !x.IsWrite), Is.True);
        AssertRealWireMatch(scanner, live.Shadow.WireHistory.Single(x =>
            x.Address == 2099 && !x.IsWrite), expectsReply: true);

        ushort[] addresses = [2099, 1016];
        ushort[] values = [firstValue, 2];
        var ids = new HashSet<Guid> { scanId };
        for (int i = 0; i < addresses.Length; i++)
        {
            ushort address = addresses[i];
            ushort value = values[i];
            Guid writeId = Guid.NewGuid();
            Assert.That(ids.Add(writeId), Is.True);
            var writeEvents = new List<LegacyFinsDatagramObservation>();
            var proof = await live.Client.WriteRawObservedAsync(address, value,
                writeId, writeEvents.Add);
            Assert.That(proof.OperationId, Is.EqualTo(writeId));
            Assert.That(proof.ReplyVerified, Is.True);
            Assert.That(proof.AcknowledgedUtc, Is.Not.Null);
            Assert.That(writeEvents.Select(x => x.Stage), Is.EqualTo(new[] {
                LegacyFinsObservationStage.Prepared,
                LegacyFinsObservationStage.WireSent,
                LegacyFinsObservationStage.ReplyReceived,
                LegacyFinsObservationStage.AckAccepted }));
            var wire = live.Shadow.WireHistory.Single(x =>
                x.Address == address && x.IsWrite);
            AssertRealWireMatch(writeEvents, wire, expectsReply: true);
            Assert.That(writeEvents[0].RequestBytes.Length, Is.EqualTo(20));
            Assert.That(writeEvents[^1].ResponseBytes.Length, Is.EqualTo(14));
            Assert.That(proof.Sid, Is.EqualTo(wire.Sid));

            // A DIFFERENT UDP read with a DIFFERENT SID; never infer readback
            // from the previously-accepted write or from the PLC's final mirror.
            Guid readId = Guid.NewGuid();
            Assert.That(ids.Add(readId), Is.True);
            var readEvents = new List<LegacyFinsDatagramObservation>();
            ushort[] actual = await live.Client.ReadRawObservedAsync(address, 1,
                readId, readEvents.Add);
            Assert.That(actual, Is.EqualTo(new[] { value }));
            Assert.That(readEvents[^1].Stage,
                Is.EqualTo(LegacyFinsObservationStage.ReadbackVerified));
            Assert.That(readEvents[0].RequestBytes.Length, Is.EqualTo(18));
            Assert.That(readEvents[^1].ResponseBytes.Length, Is.EqualTo(16));
            Assert.That(readEvents.All(x => x.OperationId == readId), Is.True);
            Assert.That(readEvents.All(x => x.Sid != proof.Sid), Is.True);
            AssertRealWireMatch(readEvents, live.Shadow.WireHistory.Last(x =>
                x.Address == address && !x.IsWrite), expectsReply: true);

            // Copies remain stable even when the caller modifies its own copy.
            byte firstByte = writeEvents[0].RequestBytes[0];
            byte[] callerCopy = writeEvents[0].RequestBytes.ToArray();
            callerCopy[0] ^= 0xFF;
            Assert.That(writeEvents[0].RequestBytes[0], Is.EqualTo(firstByte));
            Assert.That(writeEvents[0].RequestBytes,
                Is.InstanceOf<ImmutableArray<byte>>());
            TestContext.Progress.WriteLine($"A2_RAW_FINS_ACK step={i + 1} " +
                $"operation={writeId:D} SID={proof.Sid} request={wire.RequestHex} " +
                $"response={wire.ResponseHex} readback={actual[0]}");
        }
        Assert.That(live.Shadow.EndSequenceComplete, Is.True);
    }

    [Test]
    public async Task WriteAppliedWithLostAck_EmitsRealSendButNoReceivedReply()
    {
        await using var live = new Live(LegacyExitScenario.BarcodeMismatch);
        await live.OfferAsync(LegacyExitScenario.BarcodeMismatch);
        live.Shadow.LoseNextTerminalWriteAck(1, 2099);

        Guid operationId = Guid.NewGuid();
        var events = new List<LegacyFinsDatagramObservation>();
        var socket = live.Connect(); // short budget for negative test
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await socket.WriteRawObservedAsync(2099, 4, operationId, events.Add));
        Assert.That(events.Select(x => x.Stage), Is.EqualTo(new[] {
            LegacyFinsObservationStage.Prepared,
            LegacyFinsObservationStage.WireSent,
            LegacyFinsObservationStage.TransportFailure }));
        Assert.That(events.All(x => x.OperationId == operationId), Is.True);
        var wire = live.Shadow.WireHistory.Single(x => x.Address == 2099 && x.IsWrite);
        Assert.That(wire.Outcome, Is.EqualTo("INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY"));
        Assert.That(wire.ResponseHex, Is.Empty);
        AssertRealWireMatch(events, wire, expectsReply: false);
        var fresh = live.Connect();
        var read = new List<LegacyFinsDatagramObservation>();
        Assert.That(await fresh.ReadRawObservedAsync(2099, 1, Guid.NewGuid(), read.Add),
            Is.EqualTo(new ushort[] { 4 }),
            "Readback sees effect despite original UDP ACK being lost.");
        Assert.That(read.Single(x => x.Stage == LegacyFinsObservationStage.ReadbackVerified)
            .ResponseBytes.Length, Is.EqualTo(16));
        Assert.That(live.Shadow.WireHistory.Count(x => x.Address == 2099 && x.IsWrite),
            Is.EqualTo(1), "Never repeat an uncertain final output.");
        Assert.That(live.Shadow.WireHistory.Any(x => x.Address == 1016 && x.IsWrite),
            Is.False);
        TestContext.Progress.WriteLine("A2_RAW_FINS_ACK_UNKNOWN " +
            $"operation={operationId:D} SID={wire.Sid} raw_sent={wire.RequestHex} " +
            "raw_reply=MISSING independent_readback=4 no_duplicate=1");
    }

    [Test]
    public async Task NegativeFinsEndCode_RecordsReceivedPacketButNeverAcceptsIt()
    {
        await using var live = new Live(LegacyExitScenario.BarcodeFailure);
        await live.OfferAsync(LegacyExitScenario.BarcodeFailure);
        live.Shadow.RejectNextTerminalWrite(1, 2099);
        var events = new List<LegacyFinsDatagramObservation>();
        var id = Guid.NewGuid();
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await live.Client.WriteRawObservedAsync(2099, 3, id, events.Add));
        Assert.That(events.Select(x => x.Stage), Is.EqualTo(new[] {
            LegacyFinsObservationStage.Prepared,
            LegacyFinsObservationStage.WireSent,
            LegacyFinsObservationStage.ReplyReceived,
            LegacyFinsObservationStage.AckRejected }));
        var wire = live.Shadow.WireHistory.Single(x => x.Address == 2099 && x.IsWrite);
        Assert.That(wire.Outcome, Is.EqualTo("INJECTED_TERMINAL_WRITE_REJECTED"));
        AssertRealWireMatch(events, wire, expectsReply: true);
        Assert.That(Convert.ToHexString(events[^1].ResponseBytes.AsSpan()),
            Does.EndWith("0005"));
        Assert.That(events[^1].Detail, Does.Contain("endcode=0x0005"));
        Assert.That(events.Any(x => x.Stage == LegacyFinsObservationStage.AckAccepted),
            Is.False);
        Assert.That((await live.Client.ReadAsync(2099, 1))[0], Is.EqualTo(1));
        Assert.That((await live.Client.ReadAsync(1016, 1))[0], Is.Zero);
        TestContext.Progress.WriteLine("A2_RAW_FINS_REJECTED " +
            $"operation={id:D} SID={wire.Sid} response={wire.ResponseHex} accepted=0");
    }

    [Test]
    public async Task DurablePrepareCallbackThrowsBeforeSend_AndOriginalObservedApiStillWorks()
    {
        await using var live = new Live(LegacyExitScenario.BarcodeMismatch);
        await live.OfferAsync(LegacyExitScenario.BarcodeMismatch);
        var id = Guid.NewGuid();
        var events = new List<LegacyFinsDatagramObservation>();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await live.Client.WriteRawObservedAsync(2099, 4, id, evt =>
            {
                events.Add(evt);
                if (evt.Stage == LegacyFinsObservationStage.Prepared)
                    throw new InvalidOperationException("simulated journal fsync failed");
            }));
        Assert.That(events.Select(x => x.Stage),
            Is.EqualTo(new[] { LegacyFinsObservationStage.Prepared }));
        Assert.That(live.Shadow.WireHistory.Any(x =>
            x.Address == 2099 && x.IsWrite), Is.False);
        Assert.That((await live.Client.ReadAsync(2099, 1))[0], Is.EqualTo(1));
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await live.Client.WriteRawObservedAsync(2099, 4, Guid.Empty, _ => { }));

        LegacyFinsWriteProof? preSend = null;
        var proof = await live.Client.WriteObservedAsync(2099, 4,
            evt => preSend = evt);
        Assert.That(preSend, Is.Not.Null);
        Assert.That(preSend!.ReplyVerified, Is.False);
        Assert.That(preSend.AcknowledgedUtc, Is.Null);
        Assert.That(proof.OperationId, Is.EqualTo(preSend.OperationId));
        Assert.That(proof.Sid, Is.EqualTo(preSend.Sid));
        Assert.That(proof.ReplyVerified, Is.True);
        Assert.That(proof.AcknowledgedUtc, Is.Not.Null);
        Assert.That(live.Shadow.WireHistory.Count(x => x.IsWrite && x.Address == 2099),
            Is.EqualTo(1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            A2WritePolicy.RequireLoopback("192.168.0.50", FinsDm.SimPort));
    }
}
