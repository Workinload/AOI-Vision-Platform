using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class CaptureDebugProjectionTests
{
    private static MachineSnapshot Snapshot(WorkpieceIdentity workpiece, DateTimeOffset now)
    {
        return new MachineSnapshot("master", MachineRole.Master, "SIM_PROCESSING",
            workpiece, null,
            [new ChannelSnapshot("CH06", "ST05", "HIK-M-2", "SIM_OK",
                workpiece.WorkpieceId, true, now.AddMilliseconds(-50))],
            now, false, "SIM_FULL")
        {
            Conveyor = new SimConveyorOverview(21, 1, 0,
                [new SimConveyorItem(workpiece.WorkpieceId, 1, workpiece.Barcode,
                    "ST05", true, 1, 2, "AT_STATION", false)], [])
        };
    }

    [Test]
    public void St05ShowsActualResultAndSensorButNeverInventsPlcRequestFrameReadyOrAck()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = WorkpieceIdentity.New("BARCODE", 1, "SIM-REL");
        var projection = MonitorProjection.FromSnapshot(Snapshot(identity, now),
            MachineRole.Master, now);
        var rows = CaptureDebugProjection.ForWorkpiece(projection, identity.WorkpieceId);

        Assert.That(rows, Has.Count.EqualTo(11));
        var ch06 = rows.Single(r => r.ChannelId == "CH06");
        Assert.Multiple(() =>
        {
            Assert.That(ch06.PlcAddress, Is.EqualTo("D1011"));
            Assert.That(ch06.PhysicalDeviceId, Is.EqualTo("HIK-M-2"));
            Assert.That(ch06.InspectionResult, Is.EqualTo("SIM_OK"));
            Assert.That(ch06.ResultUtc, Does.Contain("UTC"));
            Assert.That(ch06.SensorEvidence, Does.Contain("触发"));
            Assert.That(ch06.RequestEvidence, Does.Contain(CaptureDebugProjection.NotReported));
            Assert.That(ch06.FrameReadyEvidence, Does.Contain(CaptureDebugProjection.NotReported));
            Assert.That(ch06.CamDoneEvidence, Does.Contain(CaptureDebugProjection.NotReported));
            Assert.That(ch06.PngEvidence, Does.Contain("尚未请求"));
            Assert.That(rows.Single(x => x.ChannelId == "CH07").InspectionResult,
                Is.EqualTo("本件结果未入库"));
        });
    }

    [Test]
    public void VerifiedPngMustBelongToSelectedWorkpieceAndDoesNotProveCamDone()
    {
        var now = DateTimeOffset.UtcNow;
        var actual = WorkpieceIdentity.New("REPEATED", 2, "SIM-REL");
        var different = WorkpieceIdentity.New("REPEATED", 2, "SIM-REL");
        var projection = MonitorProjection.FromSnapshot(Snapshot(actual, now),
            MachineRole.Master, now);
        var wrong = new Dictionary<string, CapturePngEvidence>
        {
            ["CH06"] = new(different.WorkpieceId, "CH06", true, 400, "ok", now)
        };
        Assert.That(CaptureDebugProjection.ForWorkpiece(projection, actual.WorkpieceId, wrong)
            .Single(x => x.ChannelId == "CH06").PngEvidence,
            Does.Contain("尚未请求"));
        wrong["CH06"] = new CapturePngEvidence(actual.WorkpieceId, "CH06", true, 400, "ok", now);
        var row = CaptureDebugProjection.ForWorkpiece(projection, actual.WorkpieceId, wrong)
            .Single(x => x.ChannelId == "CH06");
        Assert.That(row.PngEvidence, Does.Contain("400 bytes"));
        Assert.That(row.CamDoneEvidence, Does.Contain(CaptureDebugProjection.NotReported));

        // History is a different WorkpieceId even though the barcode is identical.
        var historic = CaptureDebugProjection.ForWorkpiece(projection, different.WorkpieceId, wrong)
            .Single(x => x.ChannelId == "CH06");
        Assert.That(historic.InspectionResult, Does.Contain("历史逐通道"));
        Assert.That(historic.ResultUtc, Is.EqualTo("—"));
        Assert.That(historic.PngEvidence, Does.Contain("尚未请求"));
    }

    [Test]
    public void SnapshotObserverDedupeAndWorkpieceIsolationAreReal()
    {
        var now = DateTimeOffset.UtcNow;
        var a = WorkpieceIdentity.New("DUP", 1, "R");
        var b = WorkpieceIdentity.New("DUP", 1, "R");
        var observer = new CaptureSnapshotObserver();
        var pa = MonitorProjection.FromSnapshot(Snapshot(a, now), MachineRole.Master, now);
        observer.Observe(pa, now);
        int first = observer.Recent(a.WorkpieceId).Count;
        Assert.That(first, Is.GreaterThanOrEqualTo(3));
        observer.Observe(pa, now.AddMilliseconds(200));
        Assert.That(observer.Recent(a.WorkpieceId), Has.Count.EqualTo(first),
            "Polling the same snapshot must never fabricate extra capture events.");

        var pb = MonitorProjection.FromSnapshot(Snapshot(b, now), MachineRole.Master, now);
        observer.Observe(pb, now.AddMilliseconds(500));
        Assert.That(observer.Recent(b.WorkpieceId), Is.Not.Empty);
        Assert.That(observer.Recent(a.WorkpieceId), Has.Count.EqualTo(first));
        Assert.That(observer.Recent(a.WorkpieceId).Any(e => e.Kind == "CHANNEL_RESULT_SNAPSHOT"),
            Is.True);
        Assert.That(observer.Recent(a.WorkpieceId).Any(e => e.Kind == "FINS_CAMDONE_ACK"),
            Is.False);
        observer.Clear();
        Assert.That(observer.Recent(), Is.Empty);
    }

    [Test]
    public void BoundedObserverDoesNotGrowIndefinitelyOrAcceptOfflineAsARealEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var observer = new CaptureSnapshotObserver(2);
        var a = WorkpieceIdentity.New("A", 1, "R");
        var good = MonitorProjection.FromSnapshot(Snapshot(a, now), MachineRole.Master, now);
        observer.Observe(good, now);
        Assert.That(observer.Recent().Count, Is.EqualTo(2));
        observer.Observe(MonitorProjection.Offline(MachineRole.Master), now);
        Assert.That(observer.Recent(), Has.Count.EqualTo(2));
    }
}
