using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>
/// These tests transmit actual FINS/UDP datagrams to a live 127.0.0.1 listener.
/// Memory-only assertions are insufficient: each 1016/2099 write is checked against
/// the wire trace, including SID, response, order, UTC and monotonic timestamps.
/// </summary>
[TestFixture]
public sealed class Agent02LegacyFinsTimingTests
{
    private static readonly LegacyFinsNodes MasterNodes =
        LegacyFinsNodes.HistoricalCandidateMaster;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } =
            new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan value) => Now += value;
    }

    private sealed class LiveShadow : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public LegacyPlcShadow Shadow { get; }
        public LegacyFinsLoopbackClient Client { get; }

        private LiveShadow(SimFullVirtualPlc fifo, LegacyFinsNodes nodes,
            LegacyPlcTiming? timing, TimeProvider? clock)
        {
            Shadow = new LegacyPlcShadow(fifo, nodes, timing, clock);
            var service = new LegacyPlcShadowUdp(Shadow, port: 0);
            _server = service.RunAsync(_stop.Token);
            Client = new LegacyFinsLoopbackClient(service.Listening.GetAwaiter().GetResult(),
                nodes, timing?.UdpRequestTimeout ?? TimeSpan.FromMilliseconds(700));
        }

        public static LiveShadow Start(SimFullVirtualPlc? fifo = null,
            LegacyFinsNodes? nodes = null, LegacyPlcTiming? timing = null,
            TimeProvider? clock = null) =>
            new(fifo ?? new SimFullVirtualPlc(5, 3),
                nodes ?? MasterNodes, timing, clock);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _server; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }

    [Test]
    public async Task St05LegacyRequestAndCamDoneTravelThroughRealUdpWithDifferentNodes()
    {
        await using var service = LiveShadow.Start();
        var client = service.Client;
        var initialBoot = await client.ReadAsync(4007, 2);
        Assert.That(initialBoot.Any(v => v != 0), Is.True,
            "Existing D4000 FIFO is still routed by the composed simulator.");

        // D4300 is a private SIM injection only. It makes the EXTERNAL PLC
        // present D1011=1; it is NOT a software camera trigger.
        await client.WriteAsync(LegacyPlcShadow.SimOfferCaptureAddress, [1, 1011]);
        var offer = service.Shadow.CameraAt(1011);
        Assert.That(offer?.PhysicalDeviceId, Is.EqualTo("HIK-M-2"));
        Assert.That(offer?.StationId, Is.EqualTo("ST05"));
        Assert.That(offer?.ChannelIds, Is.EqualTo(new[] { "CH06" }));
        Assert.That((await client.ReadAsync(1011, 1))[0], Is.EqualTo(1));

        // A1/A3 must supply actual FrameReady and identity proof before this call.
        await client.WriteAsync(1011, [2]);
        Assert.That((await client.ReadAsync(1011, 1))[0], Is.EqualTo(2));

        var trace = service.Shadow.WireHistory;
        Assert.That(trace.Where(t => t.Address == 1011).Select(t => (t.IsWrite, t.Values.FirstOrDefault())),
            Is.EqualTo(new[] { (false, (ushort)0), (true, (ushort)2), (false, (ushort)0) }));
        Assert.That(trace, Has.All.Matches<LegacyWireTransaction>(t =>
            t.ResponseHex.Length >= 28 && t.RequestHex.Length >= 36 &&
            t.ResponsePreparedUtc >= t.ReceivedUtc));
        Assert.That(trace.Select(t => t.MonotonicTimestamp), Is.Ordered);
        Assert.That(trace.Select(t => t.Sequence), Is.Ordered.And.Unique);
        Assert.That(trace.Single(t => t.Address == 1011 && t.IsWrite).Token, Is.EqualTo(1));
        Assert.That(trace.Single(t => t.Address == 1011 && t.IsWrite).Outcome, Is.EqualTo("OK"));

        TestContext.Progress.WriteLine("A2_ST05_REAL_UDP_CAM_DONE " +
            string.Join(" -> ", trace.Where(x => x.Address == 1011)
                .Select(x => $"{(x.IsWrite ? "WRITE" : "READ")} D{x.Address} " +
                    $"{(x.IsWrite ? x.Values[0] : 1)} sid={x.Sid} " +
                    $"utc={x.ReceivedUtc:O}")));
    }

    [Test]
    public async Task OnePhysicalLineScanCaptureAcknowledgesBothLogicalChannelsExactlyOnce()
    {
        await using var service = LiveShadow.Start();
        await service.Client.WriteAsync(LegacyPlcShadow.SimOfferCaptureAddress, [3, 1013]);
        var offer = service.Shadow.CameraAt(1013);
        Assert.That(offer?.FieldUnverified, Is.True, "D1013 clear alias is FIELD_UNVERIFIED.");
        Assert.That(offer?.ChannelIds, Is.EqualTo(new[] { "CH08", "CH09" }));
        Assert.That((await service.Client.ReadAsync(1013, 1))[0], Is.EqualTo(1));
        await service.Client.WriteAsync(1013, [2]);
        Assert.That((await service.Client.ReadAsync(1013, 1))[0], Is.EqualTo(2));
        Assert.That(service.Shadow.WireHistory.Count(x => x.Address == 1013 && x.IsWrite),
            Is.EqualTo(1), "One physical line camera has one CamDone, NOT two child ACKs.");
        Assert.Throws<UnauthorizedAccessException>(() =>
            new A2WritePolicy().AuthorizeVirtualWrite(1013, 1),
            "Default/general virtual policy still blocks field-conflicted D1013.");
    }

    [Test]
    public async Task LoadingScannerUsesActualFinsD1999AndTenWordFixture()
    {
        await using var service = LiveShadow.Start();
        await service.Client.WriteAsync(LegacyPlcShadow.SimOfferLoadAddress, [1]);
        Assert.That((await service.Client.ReadAsync(1999, 1))[0], Is.EqualTo(1));
        var raw = await service.Client.ReadAsync(2000, 10);
        Assert.That(A2FinsProtocol.DecodeBaAscii(raw), Is.EqualTo("SIM-0001"));
        await service.Client.WriteAsync(1999, [2]);
        Assert.That((await service.Client.ReadAsync(1999, 1))[0], Is.EqualTo(2));
        Assert.That(service.Shadow.WireHistory.Single(x => x.Address == 1999 &&
            x.IsWrite).Values, Is.EqualTo(new ushort[] { 2 }));
    }

    [TestCase(LegacyExitScenario.Normal, (ushort)1, (ushort)2)]
    [TestCase(LegacyExitScenario.Normal, (ushort)2, (ushort)2)]
    [TestCase(LegacyExitScenario.BarcodeFailure, (ushort)2, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, (ushort)2, (ushort)4)]
    public async Task EndResultBranchesAreTwoRealOrderedFinsWritesAndReadback(
        LegacyExitScenario scenario, ushort quality, ushort scanAck)
    {
        await using var service = LiveShadow.Start();
        // Register a virtual FIFO token so its separate D2020 end-scanner fixture exists.
        await service.Client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [1]);
        await service.Client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
            [1, (ushort)scenario]);
        Assert.That((await service.Client.ReadAsync(2099, 1))[0], Is.EqualTo(1));
        Assert.That(A2FinsProtocol.DecodeBaAscii(await service.Client.ReadAsync(2100, 10)),
            Is.EqualTo("SIM-0001"));

        if (scenario == LegacyExitScenario.Normal)
        {
            // Wrong order MUST fail without mutating D2099.
            var wrong = Assert.ThrowsAsync<InvalidDataException>(async () =>
                await service.Client.WriteAsync(2099, [2]));
            Assert.That(wrong!.Message, Does.Contain("REJECTED"));
            Assert.That((await service.Client.ReadAsync(2099, 1))[0], Is.EqualTo(1));
            await service.Client.WriteAsync(1016, [quality]);
            await service.Client.WriteAsync(2099, [2]);
        }
        else
        {
            // A scan error cannot be followed by GOOD even when all camera pixels look fine.
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await service.Client.WriteAsync(1016, [1]));
            await service.Client.WriteAsync(2099, [scanAck]);
            await service.Client.WriteAsync(1016, [2]);
        }

        Assert.That(service.Shadow.EndSequenceComplete, Is.True);
        Assert.That((await service.Client.ReadAsync(1016, 1))[0], Is.EqualTo(quality));
        Assert.That((await service.Client.ReadAsync(2099, 1))[0], Is.EqualTo(scanAck));

        var acceptedWrites = service.Shadow.WireHistory
            .Where(x => x.IsWrite && x.Outcome == "OK" &&
                        x.Address is 1016 or 2099).ToArray();
        var expectedOrder = scenario == LegacyExitScenario.Normal
            ? new ushort[] { 1016, 2099 } : new ushort[] { 2099, 1016 };
        Assert.That(acceptedWrites.Select(x => x.Address), Is.EqualTo(expectedOrder));
        Assert.That(acceptedWrites.Select(x => x.Values.Single()),
            Is.EqualTo(scenario == LegacyExitScenario.Normal
                ? new ushort[] { quality, 2 }
                : new ushort[] { scanAck, 2 }));
        Assert.That(acceptedWrites.Select(x => x.RequestHex),
            Has.All.Matches<string>(s => s.StartsWith("800002", StringComparison.Ordinal)));
        Assert.That(acceptedWrites.All(x => x.ResponseHex.StartsWith("C00002",
            StringComparison.Ordinal)), Is.True);
        Assert.That(acceptedWrites.Select(x => x.ReceivedUtc), Is.Ordered);
        Assert.That(acceptedWrites.Select(x => x.MonotonicTimestamp), Is.Ordered);
        Assert.That(acceptedWrites.All(x => x.Token == 1), Is.True);
        TestContext.Progress.WriteLine($"A2_END_WIRE_{scenario} " +
            string.Join(" -> ", acceptedWrites.Select(x =>
                $"FINS WRITE D{x.Address}={x.Values[0]} SID={x.Sid} at={x.ReceivedUtc:O}")));
    }

    [Test]
    public async Task CaptureTimeoutAndMissingUdpPacketRejectLateOrInventedCamDone()
    {
        var clock = new ManualClock();
        var tiny = new LegacyPlcTiming(TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(70));
        await using var service = LiveShadow.Start(timing: tiny, clock: clock);
        var client = service.Client;

        // No PLC request: ack must be explicitly rejected.
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await client.WriteAsync(1011, [2]));

        await client.WriteAsync(LegacyPlcShadow.SimOfferCaptureAddress, [1, 1011]);
        service.Shadow.SuppressNextPacket(1011);
        Exception? missing = null;
        try { await client.ReadAsync(1011, 1); }
        catch (Exception ex) { missing = ex; }
        Assert.That(missing, Is.InstanceOf<OperationCanceledException>(),
            "One dropped REAL localhost UDP request must reach the transport timeout.");
        Assert.That(service.Shadow.WireHistory.Last(x => x.Address == 1011).Outcome,
            Is.EqualTo("INJECTED_UDP_TIMEOUT"));
        clock.Advance(TimeSpan.FromMilliseconds(41));
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await client.WriteAsync(1011, [2]));
        Assert.That((await client.ReadAsync(1011, 1))[0], Is.EqualTo(1),
            "PLC still requests a result; the Core must report a missing-frame fault.");
        Assert.That(service.Shadow.WireHistory.Where(x => x.Address == 1011 && x.IsWrite)
            .All(x => x.Outcome.StartsWith("REJECTED_", StringComparison.Ordinal)), Is.True);
    }

    [TestCase(LegacyExitScenario.Normal, FinalDisposition.Good, (ushort)1)]
    [TestCase(LegacyExitScenario.Normal, FinalDisposition.Reject, (ushort)2)]
    [TestCase(LegacyExitScenario.BarcodeFailure, FinalDisposition.Reject, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, FinalDisposition.Reject, (ushort)4)]
    public async Task HostCallableFinalWriterUsesTwoActualFinsWritesAndVerifiesBothReadbacks(
        LegacyExitScenario scenario, FinalDisposition disposition, ushort expectedScan)
    {
        await using var service = LiveShadow.Start();
        await service.Client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [1]);
        await service.Client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
            [1, (ushort)scenario]);

        var receipt = await LegacyFinsOutputWriter.WriteFinalAsync(
            service.Client, 1, scenario, disposition, TimeSpan.FromSeconds(1));
        Assert.That(receipt.IsSimulation, Is.True);
        Assert.That(receipt.ConfirmedSteps, Has.Count.EqualTo(2));
        Assert.That(receipt.ConfirmedSteps.Select(x => x.DmAddress),
            Is.EqualTo(scenario == LegacyExitScenario.Normal
                ? new ushort[] { 1016, 2099 } : new ushort[] { 2099, 1016 }));
        Assert.That(receipt.ConfirmedSteps.All(x => x.Value == x.ConfirmedValue &&
            x.RequestUtc <= x.AckUtc && x.AckUtc <= x.ReadbackUtc), Is.True);
        Assert.That((await service.Client.ReadAsync(2099, 1))[0], Is.EqualTo(expectedScan));
        Assert.That(service.Shadow.EndSequenceComplete, Is.True);

        // Evidence comes from the server's raw decoded FINS packets, not mirror words.
        var actualWrites = service.Shadow.WireHistory
            .Where(x => x.IsWrite && x.Address is 1016 or 2099).ToArray();
        Assert.That(actualWrites.Select(x => x.Address),
            Is.EqualTo(receipt.ConfirmedSteps.Select(x => x.DmAddress)));
        Assert.That(actualWrites.All(x => x.RequestHex.Length == 40 &&
            x.ResponseHex.Length == 28 && x.Outcome == "OK"), Is.True);
        TestContext.Progress.WriteLine("A2_HOST_FINAL_WRITER " + scenario + " " +
            string.Join(" -> ", receipt.ConfirmedSteps.Select(x =>
                $"D{x.DmAddress}={x.Value} readback={x.ConfirmedValue}")));
    }

    [Test]
    public async Task FinalWriterRejectsMissingRequestAndNeverWritesGoodOnBarcodeMismatch()
    {
        await using var service = LiveShadow.Start();
        var beforeOffer = Assert.ThrowsAsync<LegacyOutputIncompleteException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(service.Client, 1,
                LegacyExitScenario.Normal, FinalDisposition.Good, TimeSpan.FromSeconds(1)));
        Assert.That(beforeOffer?.ConfirmedSteps, Is.Empty);
        Assert.That(service.Shadow.WireHistory.Any(x => x.IsWrite &&
            x.Address is 1016 or 2099), Is.False);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await LegacyFinsOutputWriter.WriteFinalAsync(service.Client, 1,
                LegacyExitScenario.BarcodeMismatch, FinalDisposition.Good,
                TimeSpan.FromSeconds(1)));
        Assert.That(service.Shadow.EndSequenceComplete, Is.False);
    }

    [Test]
    public void FinsNodeRouteAndSidAreValidatedBeforeAnyShadowMutation()
    {
        var nodes = new LegacyFinsNodes(50, 130, 1, 2, 3, 4);
        var packet = LegacyFinsWire.MakeRequest(true, 1011, new ushort[] { 2 },
            sid: 0x45, nodes);
        Assert.That(LegacyFinsWire.TryParseRequest(packet, nodes, out var op), Is.True);
        Assert.That(op.Sid, Is.EqualTo(0x45));
        Assert.That(op.Address, Is.EqualTo(1011));
        Assert.That(LegacyFinsWire.TryParseRequest(packet, LegacyFinsNodes.Demo, out _), Is.False);
        var reply = LegacyFinsWire.MakeResponse(packet, 0, []);
        Assert.That(reply[3], Is.EqualTo(2));
        Assert.That(reply[4], Is.EqualTo(130));
        Assert.That(reply[6], Is.EqualTo(1));
        Assert.That(reply[7], Is.EqualTo(50));
        Assert.That(LegacyFinsWire.DecodeResponse(packet, reply, nodes), Is.Empty);

        foreach (int offset in new[] { 3, 4, 5, 6, 7, 8, 9 })
        {
            var bad = (byte[])reply.Clone();
            bad[offset] ^= 1;
            Assert.Throws<InvalidDataException>(() =>
                LegacyFinsWire.DecodeResponse(packet, bad, nodes));
        }
        Assert.Throws<UnauthorizedAccessException>(() =>
            A2WritePolicy.RequireLoopback("192.168.1.50", 9600));
    }
}
