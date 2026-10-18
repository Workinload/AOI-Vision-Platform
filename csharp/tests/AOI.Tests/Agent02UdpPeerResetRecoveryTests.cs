using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;

namespace AOI.Tests;

/// <summary>
/// A closed UDP peer can deliver ICMP Port Unreachable to the Windows vPLC
/// receiver (WSAECONNRESET/10054) on its next ReceiveAsync. A network-only
/// transient must not stop the sole PLC simulator or masquerade as a FINS ACK.
/// </summary>
[TestFixture]
public sealed class Agent02UdpPeerResetRecoveryTests
{
    [Test]
    public async Task ClosedEphemeralUdpPorts_DoNotTerminateVirtualPlcOrForgeFinsReply()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var shadow = new LegacyPlcShadow(new SimFullVirtualPlc(5, 3),
            LegacyFinsNodes.Demo);
        var udp = new LegacyPlcShadowUdp(shadow, port: 0);
        Task listener = udp.RunAsync(stop.Token);
        int port = await udp.Listening.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // These are actual loopback UDP FINS read datagrams from temporary
            // client ports. Dispose each socket before the response returns.
            // Windows may subsequently report their ICMP Port Unreachable on
            // vPLC's ReceiveAsync; Linux normally does not.
            var destination = new IPEndPoint(IPAddress.Loopback, port);
            const int ephemeralRequests = 24;
            for (int i = 0; i < ephemeralRequests; i++)
            {
                byte[] packet = LegacyFinsWire.MakeRequest(
                    write: false, address: 4007, values: new ushort[2],
                    sid: (byte)(i + 1), nodes: LegacyFinsNodes.Demo);
                using (var closedPeer = new UdpClient(AddressFamily.InterNetwork))
                    await closedPeer.SendAsync(packet, destination, stop.Token);
                await Task.Delay(8, stop.Token);
                Assert.That(listener.IsCompleted, Is.False,
                    "One closed UDP client must never terminate the persistent vPLC.");
            }

            // A genuinely new FINS client must still read a valid response
            // from the SAME server, with actual route, SID and raw bytes.
            await Task.Delay(70, stop.Token);
            Assert.That(listener.IsCompleted, Is.False,
                "The vPLC died after receiving an orphan ICMP/10054.");
            var recovered = new LegacyFinsLoopbackClient(port,
                LegacyFinsNodes.Demo, TimeSpan.FromSeconds(3));
            var events = new List<LegacyFinsDatagramObservation>();
            Guid operation = Guid.NewGuid();
            ushort[] readback = await recovered.ReadRawObservedAsync(4007, 2,
                operation, events.Add, stop.Token);
            Assert.That(readback, Has.Length.EqualTo(2));
            Assert.That(readback[0] != 0 || readback[1] != 0, Is.True,
                "Recovered client must see an actual nonzero PLC boot nonce.");
            Assert.That(events.Select(e => e.Stage), Is.EqualTo(new[] {
                LegacyFinsObservationStage.Prepared,
                LegacyFinsObservationStage.WireSent,
                LegacyFinsObservationStage.ReplyReceived,
                LegacyFinsObservationStage.AckAccepted,
                LegacyFinsObservationStage.ReadbackVerified }));
            Assert.That(events.All(e => e.OperationId == operation), Is.True);
            Assert.That(events[0].RequestBytes.Length, Is.EqualTo(18));
            Assert.That(events[^1].ResponseBytes.Length, Is.EqualTo(18));
            Assert.That(shadow.WireHistory.Count(x => !x.IsWrite && x.Address == 4007),
                Is.GreaterThanOrEqualTo(ephemeralRequests + 1));
            Assert.That(shadow.WireHistory.Any(x => x.IsWrite &&
                x.Address is 1016 or 2099), Is.False,
                "Orphan UDP messages must not generate a terminal output write.");
            Assert.That(listener.IsCompleted, Is.False);
            TestContext.Progress.WriteLine(
                "A2_UDP_CLOSED_PEER_RECOVERY_PASS " +
                "real_localhost_fins=1 ephemeral_peers=24 " +
                "vplc_alive_after_icmp=1 fresh_sid_reply_verified=1 " +
                "no_final_plc_write=1 no_synthetic_ack=1");
        }
        finally
        {
            stop.Cancel();
            await listener;
        }
    }
}
