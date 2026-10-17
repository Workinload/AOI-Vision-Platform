using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>
/// Terminal faults are injected on a real loopback FINS/UDP server. All asserts use
/// real request/response traces and fresh UDP reads, not the private DM mirror.
/// </summary>
[TestFixture]
public sealed class Agent02TerminalAckFaultTests
{
    private sealed class Live : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _port;
        public LegacyPlcShadow Shadow { get; }
        public LegacyFinsLoopbackClient Client => Connect();

        public Live(LegacyExitScenario scenario)
        {
            var fifo = new SimFullVirtualPlc(5, 3,
                flipEndBarcodeToken: scenario == LegacyExitScenario.BarcodeMismatch ? 1 : null);
            Shadow = new LegacyPlcShadow(fifo, LegacyFinsNodes.Demo,
                unreadableEndScanTokens: scenario == LegacyExitScenario.BarcodeFailure
                    ? new ushort[] { 1 } : Array.Empty<ushort>());
            var server = new LegacyPlcShadowUdp(Shadow, 0);
            _server = server.RunAsync(_stop.Token);
            _port = server.Listening.GetAwaiter().GetResult();
        }

        public LegacyFinsLoopbackClient Connect() =>
            new(_port, LegacyFinsNodes.Demo, TimeSpan.FromMilliseconds(350));

        public async Task OfferAsync(LegacyExitScenario scenario)
        {
            await Client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [1]);
            var scannerWords = await Client.ReadAsync(
                SimFullVirtualPlc.EndBarcodeAddress, 10);
            string observed = A2FinsProtocol.DecodeBaAscii(scannerWords);
            if (scenario == LegacyExitScenario.BarcodeFailure)
                Assert.That(observed, Is.Empty);
            else if (scenario == LegacyExitScenario.BarcodeMismatch)
                Assert.That(observed, Is.EqualTo("SIM-BAD-END"));

            await Client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                [1, (ushort)scenario]);
            Assert.That((await Client.ReadAsync(LegacyPlcShadow.EndRequestAddress, 1))[0],
                Is.EqualTo(1));
            Assert.That(A2FinsProtocol.DecodeBaAscii(
                await Client.ReadAsync(LegacyPlcShadow.EndBarcodeAddress, 10)),
                Is.EqualTo(observed));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _server;
            _stop.Dispose();
        }
    }

    private static Task<LegacyOutputReceipt> SendAsync(LegacyFinsLoopbackClient client,
        LegacyExitScenario scenario) =>
        LegacyFinsOutputWriter.WriteFinalAsync(client, 1, scenario,
            FinalDisposition.Reject, TimeSpan.FromSeconds(2));

    [TestCase(LegacyExitScenario.BarcodeFailure, (ushort)3)]
    [TestCase(LegacyExitScenario.BarcodeMismatch, (ushort)4)]
    public async Task FirstFinsWriteAppliedButAckLost_PreventsAutomaticSecondWriteAndDuplicate(
        LegacyExitScenario scenario, ushort expectedScanAck)
    {
        await using var live = new Live(scenario);
        await live.OfferAsync(scenario);
        live.Shadow.LoseNextTerminalWriteAck(1, LegacyPlcShadow.EndRequestAddress);
        var original = live.Client;
        var incomplete = Assert.ThrowsAsync<LegacyOutputIncompleteException>(
            async () => await SendAsync(original, scenario));
        Assert.That(incomplete, Is.Not.Null);
        Assert.That(incomplete!.ConfirmedSteps, Is.Empty,
            "The sent datagram may have applied but has no verified ACK.");

        var output = live.Shadow.WireHistory.Where(x => x.IsWrite &&
            x.Address is LegacyPlcShadow.EndRequestAddress or
                         LegacyPlcShadow.FinalQualityAddress).ToArray();
        Assert.That(output, Has.Length.EqualTo(1));
        Assert.That(output[0].Token, Is.EqualTo(1));
        Assert.That(output[0].Values, Is.EqualTo(new[] { expectedScanAck }));
        Assert.That(output[0].Outcome,
            Is.EqualTo("INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY"));
        Assert.That(output[0].ResponseHex, Is.Empty,
            "The vPLC must actually suppress the UDP response.");
        Assert.That(output[0].RequestHex, Has.Length.EqualTo(40));

        // Fresh client = reconnect. A readback can observe the value but cannot
        // prove that the interrupted Core received a response. No write is retried.
        var reconnected = live.Connect();
        Assert.That((await reconnected.ReadAsync(2099, 1))[0], Is.EqualTo(expectedScanAck));
        Assert.That((await reconnected.ReadAsync(1016, 1))[0], Is.Zero);
        Assert.That(live.Shadow.EndSequenceComplete, Is.False);
        Assert.ThrowsAsync<LegacyOutputIncompleteException>(
            async () => await SendAsync(reconnected, scenario));
        Assert.That(live.Shadow.WireHistory.Count(x => x.IsWrite &&
            x.Address is 2099 or 1016), Is.EqualTo(1),
            "The output writer MUST NOT repeat an uncertain write.");
        Assert.That((await reconnected.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1))[0],
            Is.EqualTo(1), "Never actuate FIFO exit on an unknown ACK.");

        TestContext.Progress.WriteLine($"A2_FINS_ACK_LOST_AFTER_APPLY scenario={scenario} " +
            $"D2099={expectedScanAck} ack_hex=EMPTY sent_sid={output[0].Sid} " +
            $"raw={output[0].RequestHex} duplicate_writes=0");
    }

    [Test]
    public async Task SecondWriteAckLost_PreservesOnlyFirstConfirmedStepAndDoesNotReplay()
    {
        await using var live = new Live(LegacyExitScenario.BarcodeMismatch);
        await live.OfferAsync(LegacyExitScenario.BarcodeMismatch);
        live.Shadow.LoseNextTerminalWriteAck(1, LegacyPlcShadow.FinalQualityAddress);
        var error = Assert.ThrowsAsync<LegacyOutputIncompleteException>(
            async () => await SendAsync(live.Client, LegacyExitScenario.BarcodeMismatch));
        Assert.That(error!.ConfirmedSteps, Has.Count.EqualTo(1));
        Assert.That(error.ConfirmedSteps[0].DmAddress, Is.EqualTo(2099));
        Assert.That(error.ConfirmedSteps[0].Value, Is.EqualTo(4));
        Assert.That(live.Shadow.EndSequenceComplete, Is.True,
            "PLC can complete both effects even though Master cannot confirm second ACK.");
        var writes = live.Shadow.WireHistory.Where(x => x.IsWrite &&
            x.Address is 2099 or 1016).ToArray();
        Assert.That(writes.Select(x => (x.Address, x.Values[0])),
            Is.EqualTo(new[] { ((ushort)2099, (ushort)4), ((ushort)1016, (ushort)2) }));
        Assert.That(writes[0].Outcome, Is.EqualTo("OK"));
        Assert.That(writes[1].Outcome, Is.EqualTo("INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY"));
        Assert.That(writes[0].ResponseHex, Is.Not.Empty);
        Assert.That(writes[1].ResponseHex, Is.Empty);
        var fresh = live.Connect();
        Assert.That((await fresh.ReadAsync(2099, 1))[0], Is.EqualTo(4));
        Assert.That((await fresh.ReadAsync(1016, 1))[0], Is.EqualTo(2));
        Assert.That((await fresh.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1))[0],
            Is.EqualTo(1));
        Assert.That(writes.Select(x => x.MonotonicTimestamp), Is.Ordered);
        TestContext.Progress.WriteLine("A2_FINS_SECOND_ACK_LOST D2099=4_ACKED D1016=2_APPLIED_ACK_LOST " +
            $"sid={writes[1].Sid} no_ejection=1");
    }

    [Test]
    public async Task InjectedWriteRejectionSendsFinsErrorAndNeverChangesOutputRegisters()
    {
        await using var live = new Live(LegacyExitScenario.BarcodeFailure);
        await live.OfferAsync(LegacyExitScenario.BarcodeFailure);
        live.Shadow.RejectNextTerminalWrite(1, LegacyPlcShadow.EndRequestAddress);
        var ex = Assert.ThrowsAsync<LegacyOutputIncompleteException>(
            async () => await SendAsync(live.Client, LegacyExitScenario.BarcodeFailure));
        Assert.That(ex!.ConfirmedSteps, Is.Empty);
        var first = live.Shadow.WireHistory.Single(x => x.IsWrite && x.Address == 2099);
        Assert.That(first.Token, Is.EqualTo(1));
        Assert.That(first.Outcome, Is.EqualTo("INJECTED_TERMINAL_WRITE_REJECTED"));
        Assert.That(first.ResponseHex, Does.EndWith("0005"),
            "Simulator negative FINS end-code must be observable on UDP.");
        Assert.That((await live.Client.ReadAsync(2099, 1))[0], Is.EqualTo(1));
        Assert.That((await live.Client.ReadAsync(1016, 1))[0], Is.Zero);
        Assert.That(live.Shadow.EndSequenceComplete, Is.False);
        Assert.That(live.Shadow.WireHistory.Count(x => x.IsWrite &&
            x.Address is 2099 or 1016), Is.EqualTo(1));
        TestContext.Progress.WriteLine("A2_FINS_WRITE_REJECTED token=1 D2099=3 " +
            $"sid={first.Sid} response={first.ResponseHex} no_register_change=1");
    }

    [Test]
    public void UnrelatedAddressesAndInvalidTokensCannotBecomeFaultTargets()
    {
        var shadow = new LegacyPlcShadow(new SimFullVirtualPlc(5, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            shadow.LoseNextTerminalWriteAck(0, 2099));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            shadow.RejectNextTerminalWrite(129, 1016));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            shadow.LoseNextTerminalWriteAck(1, 1013));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            shadow.RejectNextTerminalWrite(1, 4003));
        Assert.Throws<UnauthorizedAccessException>(() =>
            A2WritePolicy.RequireLoopback("8.8.8.8", 19600));
    }
}
