using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AOI.Tests;

/// <summary>
/// Agent05 independent negative acceptance against real IPv4-loopback UDP sockets.
/// Unlike a memory-only simulator test, the relay can let the legacy PLC APPLY a
/// write and then discard ONLY its UDP response. The actual sender cannot know
/// whether the write took effect. This fixture does not impersonate AOI.Host
/// restart / A1 persistent output intents; that E2E remains separately required.
/// No endpoint other than 127.0.0.1 is expressible here.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Agent05FinalFinsAckLossTests
{
    private static readonly LegacyFinsNodes Nodes = LegacyFinsNodes.HistoricalCandidateMaster;

    private sealed record DatagramEvidence(
        byte[] Request, byte[] ShadowReply, byte[]? ActuallyTransmittedReply,
        DateTimeOffset ReceivedUtc, long MonotonicTick)
    {
        public bool IsWrite => Request.Length >= 18 && Request[11] == 2;
        public ushort Address => BinaryPrimitives.ReadUInt16BigEndian(Request.AsSpan(13, 2));
        public ushort Value => IsWrite
            ? BinaryPrimitives.ReadUInt16BigEndian(Request.AsSpan(18, 2)) : (ushort)0;
        public byte Sid => Request[9];
    }

    /// <summary>
    /// A QA-only UDP response interceptor. The same A2 LegacyPlcShadow.Handle
    /// processes every real FINS packet BEFORE we decide to drop/alter a reply.
    /// This models ambiguous applied-writes, which the shadow's pre-handle
    /// SuppressNextPacket cannot model.
    /// </summary>
    private sealed class AppliedWriteFaultRelay : IAsyncDisposable
    {
        private readonly UdpClient _socket =
            new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly object _gate = new();
        private readonly List<DatagramEvidence> _exchanges = [];
        private readonly ushort? _lostAckAt;
        private readonly ushort _lostAckValue;
        private readonly bool _staleFirstEndReadback;
        private bool _lostAck;
        private bool _staleRead;
        private bool _firstFinalApplied;

        public LegacyPlcShadow Shadow { get; }
        public int Port { get; }
        public IReadOnlyList<DatagramEvidence> Exchanges
        {
            get { lock (_gate) return _exchanges.ToArray(); }
        }

        public AppliedWriteFaultRelay(ushort? lostAckAt = null, ushort lostAckValue = 0,
            bool staleFirstEndReadback = false)
        {
            Shadow = new LegacyPlcShadow(new SimFullVirtualPlc(3, 3), Nodes);
            Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
            _lostAckAt = lostAckAt;
            _lostAckValue = lostAckValue;
            _staleFirstEndReadback = staleFirstEndReadback;
            _server = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult incoming;
                try { incoming = await _socket.ReceiveAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                if (!IPAddress.IsLoopback(incoming.RemoteEndPoint.Address))
                    continue;

                var utc = DateTimeOffset.UtcNow;
                var tick = Stopwatch.GetTimestamp();
                var packet = incoming.Buffer;
                // A2 handles a REAL received datagram, mutating PLC state and
                // recording its SID/end-code, even if our reply is subsequently lost.
                var generated = Shadow.Handle(packet);
                byte[]? transmitted = generated.Length == 0 ? null : generated;
                if (LegacyFinsWire.TryParseRequest(packet, Nodes, out var op))
                {
                    bool acceptedWrite = op.IsWrite && op.Count == 1 &&
                        (op.Address is 1016 or 2099) &&
                        generated.Length == 14 && generated[12] == 0 &&
                        generated[13] == 0;

                    if (acceptedWrite)
                        _firstFinalApplied = true;

                    if (acceptedWrite && !_lostAck && _lostAckAt == op.Address &&
                        op.Values[0] == _lostAckValue)
                    {
                        _lostAck = true;
                        transmitted = null; // AFTER real PLC mutation: ambiguous ACK
                    }
                    else if (_staleFirstEndReadback && !_staleRead &&
                        _firstFinalApplied && !op.IsWrite &&
                        op.Address == 2099 && op.Count == 1)
                    {
                        _staleRead = true;
                        // Physically return D2099=1 even when PLC applied D2099=3/4.
                        transmitted = LegacyFinsWire.MakeResponse(packet, 0, [1]);
                    }
                }

                if (transmitted is not null)
                {
                    try { await _socket.SendAsync(transmitted, incoming.RemoteEndPoint, _stop.Token); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                }
                lock (_gate)
                    _exchanges.Add(new DatagramEvidence(packet, generated,
                        transmitted, utc, tick));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _socket.Dispose();
            try { await _server; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }

        public LegacyFinsLoopbackClient NewClient() =>
            new(Port, Nodes, TimeSpan.FromMilliseconds(300));

        public async Task<LegacyFinsLoopbackClient> BeginAsync(LegacyExitScenario scenario)
        {
            var client = NewClient();
            await client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [1]);
            await client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                [1, (ushort)scenario]);
            Assert.That((await client.ReadAsync(2099, 1)).Single(), Is.EqualTo(1));
            return client;
        }
    }

    private static DatagramEvidence[] FinalWrites(AppliedWriteFaultRelay relay) =>
        relay.Exchanges.Where(x => x.IsWrite && x.Address is 1016 or 2099).ToArray();

    /// <summary>Decode the request/response from raw UDP bytes, not the final DM mirror.</summary>
    private static void RequireRealWire(DatagramEvidence packet, bool expectDelivered,
        bool expectSuccess)
    {
        var request = packet.Request;
        Assert.That(request[0], Is.EqualTo(0x80));
        Assert.That(request[2], Is.EqualTo(2));
        Assert.That(request[4], Is.EqualTo(Nodes.DestinationNode));
        Assert.That(request[7], Is.EqualTo(Nodes.SourceNode));
        Assert.That(request[10], Is.EqualTo(1));
        Assert.That(request[11], Is.EqualTo(2));
        Assert.That(request[12], Is.EqualTo(0x82));
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16, 2)),
            Is.EqualTo(1));
        Assert.That(request.Length, Is.EqualTo(20));
        Assert.That(packet.MonotonicTick, Is.GreaterThan(0));
        Assert.That(packet.ReceivedUtc, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
        byte[] reply = packet.ShadowReply;
        Assert.That(reply.Length, Is.EqualTo(14));
        Assert.That(reply[0], Is.EqualTo(0xC0));
        Assert.That(reply[4], Is.EqualTo(request[7]));
        Assert.That(reply[7], Is.EqualTo(request[4]));
        Assert.That(reply[9], Is.EqualTo(packet.Sid));
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12, 2)),
            Is.EqualTo(expectSuccess ? 0 : 5));
        if (expectDelivered)
        {
            Assert.That(packet.ActuallyTransmittedReply, Is.Not.Null);
            Assert.That(packet.ActuallyTransmittedReply, Is.EquivalentTo(reply));
        }
        else Assert.That(packet.ActuallyTransmittedReply, Is.Null);
    }

    [TestCase(LegacyExitScenario.BarcodeFailure, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, (ushort)4)]
    public async Task FirstReverseWriteAppliedButAckLost_MustStopBeforeD1016_AndReconnectReadOnly(
        LegacyExitScenario scenario, ushort expectedStatus)
    {
        await using var relay = new AppliedWriteFaultRelay(2099, expectedStatus);
        var client = await relay.BeginAsync(scenario);
        var uncertain = Assert.ThrowsAsync<LegacyOutputIncompleteException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(client, 1, scenario,
                FinalDisposition.Reject, TimeSpan.FromSeconds(2)));

        Assert.That(uncertain?.Token, Is.EqualTo(1));
        Assert.That(uncertain?.ConfirmedSteps, Is.Empty,
            "Even an applied PLC write is unconfirmed when the UDP reply is lost.");
        var writes = FinalWrites(relay);
        Assert.That(writes.Select(x => (x.Address, x.Value)),
            Is.EqualTo(new[] { ((ushort)2099, expectedStatus) }));
        RequireRealWire(writes.Single(), expectDelivered: false, expectSuccess: true);
        Assert.That(relay.Shadow.WireHistory.Single(x => x.Address == 2099 &&
            x.IsWrite).Outcome, Is.EqualTo("OK"),
            "PLC actually applied D2099 despite LOST transport ACK.");

        // A reconnect is a NEW FINS socket, not permission to resubmit a write.
        var reconnected = relay.NewClient();
        Assert.That((await reconnected.ReadAsync(2099, 1)).Single(),
            Is.EqualTo(expectedStatus));
        Assert.That((await reconnected.ReadAsync(1016, 1)).Single(), Is.EqualTo(0));
        Assert.That(FinalWrites(relay), Has.Length.EqualTo(1));
        Assert.That(relay.Shadow.EndSequenceComplete, Is.False);
        TestContext.Progress.WriteLine($"A5_REAL_UDP_ACK_LOST_AFTER_APPLY token=1 scenario={scenario} " +
            $"SID={writes[0].Sid} write=D2099:{expectedStatus} generated=SUCCESS transmitted=NONE " +
            "D1016_Writes=0 reconnect_write_replays=0 UNKNOWN_ACK");
    }

    [Test]
    public async Task SecondReverseWriteAppliedButAckLost_PreservesOnlyFirstConfirmedStep()
    {
        await using var relay = new AppliedWriteFaultRelay(1016, 2);
        var client = await relay.BeginAsync(LegacyExitScenario.BarcodeMismatch);
        var uncertain = Assert.ThrowsAsync<LegacyOutputIncompleteException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(client, 1,
                LegacyExitScenario.BarcodeMismatch, FinalDisposition.Reject,
                TimeSpan.FromSeconds(2)));

        Assert.That(uncertain?.ConfirmedSteps, Has.Count.EqualTo(1));
        Assert.That(uncertain!.ConfirmedSteps[0].DmAddress, Is.EqualTo(2099));
        Assert.That(uncertain.ConfirmedSteps[0].Value, Is.EqualTo(4));
        Assert.That(uncertain.ConfirmedSteps[0].ConfirmedValue, Is.EqualTo(4));
        var writes = FinalWrites(relay);
        Assert.That(writes.Select(x => (x.Address, x.Value)),
            Is.EqualTo(new[] { ((ushort)2099, (ushort)4), ((ushort)1016, (ushort)2) }));
        RequireRealWire(writes[0], expectDelivered: true, expectSuccess: true);
        RequireRealWire(writes[1], expectDelivered: false, expectSuccess: true);

        // Although the shadow has COMPLETED the sequence, the caller MUST keep
        // UNKNOWN_ACK: a write timeout cannot certify a completed two-step effect.
        Assert.That(relay.Shadow.EndSequenceComplete, Is.True);
        var newClient = relay.NewClient();
        Assert.That((await newClient.ReadAsync(2099, 1)).Single(), Is.EqualTo(4));
        Assert.That((await newClient.ReadAsync(1016, 1)).Single(), Is.EqualTo(2));
        Assert.That(FinalWrites(relay), Has.Length.EqualTo(2),
            "Client recreation must NOT implicitly replay either write.");
        TestContext.Progress.WriteLine($"A5_SECOND_WRITE_ACK_LOSS SID={writes[1].Sid} " +
            "known_steps=1 unresolved_steps=1 no_automatic_retry=1");
    }

    [Test]
    public async Task RefusedFirstWriteHasNonzeroFinsEndCode_NoSecondWriteOrGood()
    {
        await using var relay = new AppliedWriteFaultRelay();
        var client = await relay.BeginAsync(LegacyExitScenario.BarcodeFailure);
        // Deliberately wrong business scenario. Transport still uses two real
        // loopback sockets but PLC must reject the FIRST quality-first step.
        var refused = Assert.ThrowsAsync<LegacyOutputIncompleteException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(client, 1,
                LegacyExitScenario.Normal, FinalDisposition.Good,
                TimeSpan.FromSeconds(2)));
        Assert.That(refused?.ConfirmedSteps, Is.Empty);
        var writes = FinalWrites(relay);
        Assert.That(writes, Has.Length.EqualTo(1));
        Assert.That((writes[0].Address, writes[0].Value),
            Is.EqualTo(((ushort)1016, (ushort)1)));
        RequireRealWire(writes[0], expectDelivered: true, expectSuccess: false);
        Assert.That(relay.Shadow.WireHistory.Single(x => x.Address == 1016 &&
            x.IsWrite).Outcome, Does.StartWith("REJECTED_"));
        var again = relay.NewClient();
        Assert.That((await again.ReadAsync(1016, 1)).Single(), Is.EqualTo(0));
        Assert.That((await again.ReadAsync(2099, 1)).Single(), Is.EqualTo(1));
        Assert.That(relay.Shadow.EndSequenceComplete, Is.False);
        TestContext.Progress.WriteLine("A5_FINS_REJECTED_GOOD reply_end_code=0x0005 " +
            $"SID={writes[0].Sid} D1016_good_applied=0 later_D2099_write=0");
    }

    [Test]
    public async Task StaleFirstReadbackAfterAppliedFailure_HaltsBeforeFinalQualityWrite()
    {
        await using var relay = new AppliedWriteFaultRelay(staleFirstEndReadback: true);
        var client = await relay.BeginAsync(LegacyExitScenario.BarcodeFailure);
        var fault = Assert.ThrowsAsync<LegacyOutputIncompleteException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(client, 1,
                LegacyExitScenario.BarcodeFailure, FinalDisposition.Reject,
                TimeSpan.FromSeconds(2)));
        Assert.That(fault?.ConfirmedSteps, Is.Empty,
            "First written word was ACKed but failed independent readback.");
        var writes = FinalWrites(relay);
        Assert.That(writes.Select(x => (x.Address, x.Value)),
            Is.EqualTo(new[] { ((ushort)2099, (ushort)3) }));
        RequireRealWire(writes.Single(), expectDelivered: true, expectSuccess: true);
        var stale = relay.Exchanges.First(x => !x.IsWrite && x.Address == 2099 &&
            x.ActuallyTransmittedReply is { Length: 16 } response &&
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(14, 2)) == 1 &&
            relay.Exchanges.Any(w => w.IsWrite && w.Address == 2099 &&
                w.MonotonicTick < x.MonotonicTick));
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(
            stale.ShadowReply.AsSpan(14, 2)), Is.EqualTo(3));
        var newClient = relay.NewClient();
        Assert.That((await newClient.ReadAsync(2099, 1)).Single(), Is.EqualTo(3));
        Assert.That((await newClient.ReadAsync(1016, 1)).Single(), Is.EqualTo(0));
        Assert.That(FinalWrites(relay), Has.Length.EqualTo(1));
        TestContext.Progress.WriteLine("A5_STALE_READBACK_FAIL_CLOSED SID=" + stale.Sid +
            " actual_D2099=3 response_D2099=1 next_D1016_write=0");
    }
}
