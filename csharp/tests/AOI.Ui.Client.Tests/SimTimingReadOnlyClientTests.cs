using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class SimTimingReadOnlyClientTests
{
    private static SimTimingMilestone Milestone(Guid workpiece, Guid capture,
        string stage, Guid? operation = null, byte? sid = null,
        ushort? value = null, int attempt = 1, string device = "HIK-M-2",
        string? channel = "CH06", string origin = "DIRECT_OBSERVATION",
        string address = "D1011", Guid? frame = null) =>
        new(1, "master", origin, stage, DateTimeOffset.UtcNow,
            workpiece, Guid.NewGuid(), 1, "R1", Guid.NewGuid(), 5102L,
            capture, device, channel, attempt, frame, operation, null,
            address, value, sid, "ACTUAL_FINS_UDP_ACK");

    private static SimTimingReply Reply(Guid id,
        params SimTimingMilestone[] milestones) =>
        new(true, "SIM_ONLY_READ_ONLY", id, true, false, milestones, []);

    private static CaptureDebugRow St05(SimTimingReply reply) =>
        OperatorTimingProjection.Apply(
            CaptureDebugProjection.ForWorkpiece(
                MonitorProjection.Offline(MachineRole.Master), reply.WorkpieceId), reply)
            .Single(x => x.ChannelId == "CH06");

    [Test]
    public async Task RealLoopbackTcpQueriesExactWorkpieceAndConsumesA0CompatibleJson()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var item = Guid.NewGuid();
        var op = Guid.NewGuid();
        var capture = Guid.NewGuid();
        var reply = Reply(item,
            Milestone(item, capture, "CamDoneAcknowledged", op, 49, 2));
        var request = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(accepted.GetStream(), Encoding.ASCII,
                leaveOpen: true);
            string? line = await reader.ReadLineAsync();
            byte[] bytes = Encoding.UTF8.GetBytes(WireFormat.Serialize(reply) + "\n");
            await accepted.GetStream().WriteAsync(bytes);
            return line;
        });
        var result = await new SimTimingReadOnlyClient(port).ReadAsync(item);
        Assert.That(await request, Is.EqualTo("TIMING|" + item.ToString("D")));
        Assert.That(result.Events.Single().FinsSid, Is.EqualTo(49));
        Assert.That(result.Events.Single().OperationId, Is.EqualTo(op));
        Assert.That(St05(result).CamDoneEvidence, Does.Contain("ACK已观测"));
        Assert.That(St05(result).CamDoneEvidence, Does.Contain("SID=49"));
    }

    [Test]
    public void SentPacketAndInspectionOkCannotInventAnAcknowledgedCamDone()
    {
        var item = Guid.NewGuid();
        var capture = Guid.NewGuid();
        var op = Guid.NewGuid();
        var sent = Milestone(item, capture, "CamDoneSent", op, 11, 2);
        var completed = Milestone(item, capture, "InspectionCompleted",
            frame: Guid.NewGuid()) with { Status = "OK" };
        var rows = St05(Reply(item, sent, completed));
        Assert.That(rows.CamDoneEvidence, Does.Contain("已发送但未确认 ACK"));
        Assert.That(rows.CamDoneEvidence, Does.Not.Contain("ACK已观测"));
        Assert.That(rows.InspectionResult, Does.Contain("InspectionCompleted OK"));
    }

    [Test]
    public void RequestFrameAndCamDoneUseTheLatestCaptureAttemptOnly()
    {
        var id = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var ackPrevious = Milestone(id, first, "CamDoneAcknowledged", Guid.NewGuid(), 7, 2);
        var newRequest = Milestone(id, second, "PlcCaptureRequestObserved",
            value: 1, attempt: 2);
        var arrived = Milestone(id, second, "FrameArrived",
            attempt: 2, frame: Guid.NewGuid());
        var rows = St05(Reply(id, ackPrevious, newRequest, arrived));
        Assert.That(rows.RequestEvidence, Does.Contain("已观测"));
        Assert.That(rows.FrameReadyEvidence, Does.Contain("尚无证据"));
        Assert.That(rows.CamDoneEvidence, Does.Not.Contain("ACK已观测"));
    }

    [Test]
    public void LineScanParentFrameMapsToTwoChannelsButNotFictitiousChildInspection()
    {
        var id = Guid.NewGuid();
        var capture = Guid.NewGuid();
        var frame = Guid.NewGuid();
        var ready = Milestone(id, capture, "FrameReady", device: "LINE-M-2",
            channel: null, address: "D1014", frame: frame);
        var inspected = Milestone(id, capture, "InspectionCompleted", device: "LINE-M-2",
            channel: "CH10", address: "D1014", frame: frame) with { Status = "NG" };
        var result = OperatorTimingProjection.Apply(
            CaptureDebugProjection.ForWorkpiece(
                MonitorProjection.Offline(MachineRole.Master), id),
            Reply(id, ready, inspected));
        var ch10 = result.Single(x => x.ChannelId == "CH10");
        var ch11 = result.Single(x => x.ChannelId == "CH11");
        Assert.That(ch10.FrameReadyEvidence, Does.Contain(frame.ToString("N")[..8]));
        Assert.That(ch11.FrameReadyEvidence, Does.Contain(frame.ToString("N")[..8]));
        Assert.That(ch10.InspectionResult, Does.Contain("NG"));
        Assert.That(ch11.InspectionResult, Does.Not.Contain("NG"));
    }

    [Test]
    public void WrongWorkpieceLegacyAndEmptyTimelineNeverBecomePhysicalAck()
    {
        var id = Guid.NewGuid();
        var capture = Guid.NewGuid();
        var wrong = Reply(id, Milestone(Guid.NewGuid(), capture,
            "CamDoneAcknowledged", Guid.NewGuid(), 3, 2));
        Assert.Throws<InvalidDataException>(() => wrong.Validate());
        var legacy = Reply(id, Milestone(id, capture,
            "CamDoneAcknowledged", Guid.NewGuid(), 3, 2, origin: "CORE_WAL_LEGACY"));
        Assert.That(St05(legacy).CamDoneEvidence, Does.Not.Contain("ACK已观测"));
        var empty = Reply(id);
        Assert.That(OperatorTimingProjection.Display(empty).Summary, Does.Contain("尚无持久化"));
        Assert.That(St05(empty).CamDoneEvidence, Does.Contain("Core无"));
    }

    [Test]
    public void RejectsOversizedAndNonSimulatedRepliesAndPreservesA7Findings()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() =>
            (Reply(id) with { IsSimulation = false }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            Reply(id, Enumerable.Repeat(Milestone(id, Guid.NewGuid(), "FrameArrived"),
                121).ToArray()).Validate());
        var withFault = Reply(id) with
        {
            Findings = [new SimTimingFinding(
                "FRAME_REUSED_ACROSS_WORKPIECES", "ERROR", "Frame reused")]
        };
        var projected = OperatorTimingProjection.Display(withFault);
        Assert.That(projected.Findings.Single().Severity, Is.EqualTo("ERROR"));
        Assert.That(projected.Findings.Single().Code, Does.Contain("REUSED"));
    }
}
