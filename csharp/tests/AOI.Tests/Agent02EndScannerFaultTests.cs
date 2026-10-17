using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>
/// Real 127.0.0.1 FINS/UDP requests, not a call to the shadow's memory image.
/// The test intentionally leaves the failed scan token in the output queue.
/// </summary>
[TestFixture]
public sealed class Agent02EndScannerFaultTests
{
    private static async Task AdvanceHeadToTokenThreeAsync(LegacyFinsLoopbackClient client)
    {
        for (ushort token = 1; token <= 3; token++)
            await client.WriteAsync(SimFullVirtualPlc.LoadAckAddress, [token]);
        for (ushort token = 1; token <= 2; token++)
        {
            Assert.That((await client.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1))[0],
                Is.EqualTo(token));
            Assert.That(A2FinsProtocol.DecodeBaAscii(
                await client.ReadAsync(SimFullVirtualPlc.EndBarcodeAddress, 10)),
                Is.EqualTo(SimFullVirtualPlc.Barcode(token)),
                "No scanner fault may contaminate an earlier workpiece.");
            await client.WriteAsync(SimFullVirtualPlc.ExitDecisionAddress, [token, 2]);
        }
        Assert.That((await client.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1))[0],
            Is.EqualTo(3));
    }

    [Test]
    public async Task TokenThreeUnreadableScannerIsObservedOverUdpAndProducesReverseFinsOrder()
    {
        var plc = new SimFullVirtualPlc(5, 3);
        var shadow = new LegacyPlcShadow(plc, LegacyFinsNodes.Demo,
            unreadableEndScanTokens: new ushort[] { 3 });
        using var stop = new CancellationTokenSource();
        var server = new LegacyPlcShadowUdp(shadow, 0);
        var task = server.RunAsync(stop.Token);
        try
        {
            var client = new LegacyFinsLoopbackClient(await server.Listening,
                LegacyFinsNodes.Demo, TimeSpan.FromMilliseconds(700));
            await AdvanceHeadToTokenThreeAsync(client);

            // The Master reads this EXISTING D2020 register BEFORE it offers D4303.
            var observed = await client.ReadAsync(SimFullVirtualPlc.EndBarcodeAddress, 10);
            Assert.That(observed, Is.All.Zero,
                "The external scanner must actually report an empty barcode.");
            Assert.That(A2FinsProtocol.DecodeBaAscii(observed), Is.Empty);
            var raw = shadow.WireHistory.Last(t =>
                t.Address == SimFullVirtualPlc.EndBarcodeAddress && !t.IsWrite);
            Assert.That(raw.Token, Is.EqualTo(3),
                "D2020 FINS evidence must identify the active workpiece token.");
            Assert.That(raw.Outcome, Is.EqualTo("INJECTED_END_SCAN_UNREADABLE"));
            Assert.That(raw.RequestHex, Does.StartWith("800002"));
            Assert.That(raw.ResponseHex, Does.StartWith("C00002"));

            // Existing A0 bridge will choose BarcodeFailure when it sees empty D2020.
            await client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                [3, (ushort)LegacyExitScenario.BarcodeFailure]);
            Assert.That((await client.ReadAsync(LegacyPlcShadow.EndRequestAddress, 1))[0],
                Is.EqualTo(1));
            Assert.That(await client.ReadAsync(LegacyPlcShadow.EndBarcodeAddress, 10),
                Is.All.Zero, "D2100 must be empty too, not a Host-forged failure.");

            var receipt = await LegacyFinsOutputWriter.WriteFinalAsync(
                client, 3, LegacyExitScenario.BarcodeFailure, FinalDisposition.Reject,
                TimeSpan.FromSeconds(2));
            Assert.That(receipt.ConfirmedSteps.Select(x => (x.DmAddress, x.Value)),
                Is.EqualTo(new[] { ((ushort)2099, (ushort)3), ((ushort)1016, (ushort)2) }));
            Assert.That(receipt.ConfirmedSteps.All(x => x.Value == x.ConfirmedValue),
                Is.True);

            var writes = shadow.WireHistory.Where(x => x.IsWrite && x.Outcome == "OK" &&
                (x.Address is 2099 or 1016)).ToArray();
            Assert.That(writes.Select(x => (x.Address, x.Values.Single())),
                Is.EqualTo(new[] { ((ushort)2099, (ushort)3), ((ushort)1016, (ushort)2) }));
            Assert.That(writes.Select(x => x.Sid), Is.Ordered);
            Assert.That(writes.Select(x => x.MonotonicTimestamp), Is.Ordered);
            Assert.That((await client.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1))[0],
                Is.EqualTo(3), "Abnormal terminal handshake must NEVER eject the item.");
            Assert.That((await client.ReadAsync(SimFullVirtualPlc.ExitedCountAddress, 1))[0],
                Is.EqualTo(2));

            TestContext.Progress.WriteLine("A2_SCANNER_EMPTY_UDP token=3 " +
                string.Join(" -> ", writes.Select(x =>
                    $"D{x.Address}={x.Values[0]} SID={x.Sid} req={x.RequestHex} ack={x.ResponseHex}")));
        }
        finally
        {
            stop.Cancel();
            await task;
        }
    }

    [Test]
    public async Task TokenSpecificScannerFailureCannotBeMisreportedAsMismatch()
    {
        var shadow = new LegacyPlcShadow(new SimFullVirtualPlc(5, 3),
            LegacyFinsNodes.Demo, unreadableEndScanTokens: new ushort[] { 3 });
        using var stop = new CancellationTokenSource();
        var server = new LegacyPlcShadowUdp(shadow, 0);
        var task = server.RunAsync(stop.Token);
        try
        {
            var client = new LegacyFinsLoopbackClient(await server.Listening,
                LegacyFinsNodes.Demo, TimeSpan.FromMilliseconds(700));
            await AdvanceHeadToTokenThreeAsync(client);
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await client.WriteAsync(LegacyPlcShadow.SimOfferExitAddress,
                    [3, (ushort)LegacyExitScenario.BarcodeMismatch]));
            Assert.That(shadow.EndSequenceComplete, Is.False);
            Assert.That((await client.ReadAsync(LegacyPlcShadow.EndRequestAddress, 1))[0],
                Is.Zero, "Wrong scenario must not open D2099.");
            Assert.That(shadow.WireHistory.Single(x =>
                x.Address == LegacyPlcShadow.SimOfferExitAddress &&
                x.IsWrite).Outcome,
                Does.StartWith("REJECTED_UNREADABLE_END_SCAN"));
            Assert.That(shadow.WireHistory.Any(x => x.IsWrite &&
                x.Address is 2099 or 1016), Is.False);
            TestContext.Progress.WriteLine("A2_SCANNER_WRONG_BRANCH_REJECTED token=3 no_output=1");
        }
        finally
        {
            stop.Cancel();
            await task;
        }
    }

    [Test]
    public void InvalidFailureTokenConfigDoesNotCreateHardwareAccess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LegacyPlcShadow(new SimFullVirtualPlc(5, 3),
                unreadableEndScanTokens: new ushort[] { 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LegacyPlcShadow(new SimFullVirtualPlc(5, 3),
                unreadableEndScanTokens: new ushort[] { 129 }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            A2WritePolicy.RequireLoopback("10.0.0.2", FinsDm.SimPort));
    }
}
