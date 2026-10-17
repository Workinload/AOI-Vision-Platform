using AOI.Domain;
using AOI.Runtime.Plc.Mechanics;
using NUnit.Framework;

namespace AOI.Tests;

/// <summary>Reproducible real mechanics state transitions; no mocks and no industrial hardware.</summary>
[TestFixture]
public sealed class Agent02MechanicalLineTests
{
    private static WorkpieceIdentity Item(int number) =>
        WorkpieceIdentity.New($"SIM-MECH-{number:0000}", 2, "SIM_MECHANICS_RELEASE");

    private static IReadOnlyList<SimMechanicalEvent> RunUntilEmpty(
        SimMechanicalLine line, int maxTicks = 1000)
    {
        var trace = new List<SimMechanicalEvent>();
        for (int tick = 0; tick < maxTicks && line.ActiveCount != 0; tick++)
        {
            var end = line.Snapshot().Active.SingleOrDefault(x => x.ReadyForExit);
            if (end is not null)
                Assert.That(line.TryEject(end.Token, FinalDisposition.Good), Is.True);
            trace.AddRange(line.DrainEvents());
            line.AdvanceTicks();
        }
        trace.AddRange(line.DrainEvents());
        Assert.That(line.ActiveCount, Is.Zero, "A non-stuck five-piece line must eventually exit.");
        return trace;
    }

    [Test]
    public void FiveDistinctWorkpiecesOccupyDifferentStationsAndEveryPhysicalCameraTriggersOnce()
    {
        var line = new SimMechanicalLine(startUtc: DateTimeOffset.UnixEpoch);
        var identities = Enumerable.Range(1, 5).Select(Item).ToArray();
        foreach (var item in identities.Select((identity, index) =>
                     (identity, token: (ushort)(index + 1))))
        {
            for (int wait = 0; !line.CanLoad && wait < 100; wait++)
                line.AdvanceTicks();
            Assert.That(line.TryLoad(item.token, item.identity), Is.True);
            Assert.That(line.FindPosition(item.identity.WorkpieceId)?.StationId, Is.EqualTo("LOADING"));
        }
        Assert.That(line.ActiveCount, Is.EqualTo(5));

        line.AdvanceTicks(100);
        var view = line.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(view.Active, Has.Count.EqualTo(5));
            Assert.That(view.Active.Select(x => x.StationId).Distinct().Count(), Is.EqualTo(5));
            Assert.That(view.Active.Select(x => x.WorkpieceId).Distinct().Count(), Is.EqualTo(5));
            Assert.That(view.Active.All(x => x.ProgressFraction is >= 0 and <= 1), Is.True);
            Assert.That(view.ExitedCount, Is.Zero, "Virtual exit is an explicit Core action.");
        });

        var trace = RunUntilEmpty(line);
        Assert.That(line.Snapshot().ExitedCount, Is.EqualTo(5));
        Assert.That(line.Snapshot().LoadedCount, Is.EqualTo(5));
        Assert.That(trace.Where(e => e.Kind == SimMechanicalEventKind.EndScanRequested),
            Has.Count.EqualTo(5));
        Assert.That(trace.Where(e => e.Kind == SimMechanicalEventKind.Ejected),
            Has.Count.EqualTo(5));

        foreach (var identity in identities)
        {
            var captures = trace.Where(x => x.WorkpieceId == identity.WorkpieceId &&
                                           x.Kind == SimMechanicalEventKind.CaptureRequested).ToArray();
            Assert.That(captures, Has.Length.EqualTo(9), "7 area cameras + 2 shared line-scan parents.");
            Assert.That(captures.SelectMany(e => e.ChannelIds).Order(StringComparer.Ordinal),
                Is.EqualTo(Enumerable.Range(1, 11).Select(n => $"CH{n:00}")
                    .Order(StringComparer.Ordinal)));
            var rearLine = captures.Single(x => x.PhysicalDeviceId == "LINE-S-8");
            var frontLine = captures.Single(x => x.PhysicalDeviceId == "LINE-M-2");
            Assert.That(rearLine.ChannelIds, Is.EqualTo(new[] { "CH08", "CH09" }));
            Assert.That(frontLine.ChannelIds, Is.EqualTo(new[] { "CH10", "CH11" }));
            Assert.That(trace.Count(x => x.WorkpieceId == identity.WorkpieceId &&
                                        x.Kind == SimMechanicalEventKind.SensorTriggered),
                Is.EqualTo(8), "Each of 8 mapped stations must fire once.");
            Assert.That(line.FindPosition(identity.WorkpieceId), Is.Null);
        }
        Assert.That(trace.Select(x => x.Sequence), Is.Ordered.And.Unique);
        Assert.That(trace.Select(x => x.VirtualUtc), Is.Ordered);
    }

    [Test]
    public void St05StuckSensorStopsMovementAndCaptureUntilOperatorRestoresIt()
    {
        var line = new SimMechanicalLine(startUtc: DateTimeOffset.UnixEpoch);
        var workpiece = Item(10);
        line.SetSensorStuck("ST05", true);
        Assert.That(line.TryLoad(10, workpiece), Is.True);

        line.AdvanceTicks(150);
        var atSt05 = line.FindPosition(workpiece.WorkpieceId);
        Assert.That(atSt05?.StationId, Is.EqualTo("ST05"));
        Assert.That(atSt05?.SensorStuck, Is.True);
        Assert.That(atSt05?.SensorTriggered, Is.False);
        Assert.That(line.TryEject(10, FinalDisposition.Good), Is.False);
        var before = line.DrainEvents();
        Assert.That(before.Any(x => x.Kind == SimMechanicalEventKind.CaptureRequested &&
                                     x.ChannelIds.Contains("CH06")), Is.False);
        Assert.That(before.Any(x => x.Kind == SimMechanicalEventKind.EndScanRequested), Is.False);

        line.AdvanceTicks(150);
        Assert.That(line.FindPosition(workpiece.WorkpieceId)?.StationId, Is.EqualTo("ST05"));
        Assert.That(line.Snapshot().ExitedCount, Is.Zero);

        line.SetSensorStuck("ST05", false);
        line.AdvanceTicks(120);
        Assert.That(line.FindPosition(workpiece.WorkpieceId)?.ReadyForExit, Is.True);
        var after = line.DrainEvents();
        Assert.That(after.Any(x => x.Kind == SimMechanicalEventKind.SensorRestored &&
                                    x.StationId == "ST05"), Is.True);
        Assert.That(after.Count(x => x.Kind == SimMechanicalEventKind.CaptureRequested &&
                                     x.ChannelIds.Contains("CH06")), Is.EqualTo(1));
        Assert.That(after.Any(x => x.Kind == SimMechanicalEventKind.EndScanRequested), Is.True);
        Assert.That(line.TryEject(10, FinalDisposition.Good), Is.True);
    }

    [Test]
    public void StationDwellTimingOneStepPerTickAndExplicitEndScannerAreObservable()
    {
        var line = new SimMechanicalLine(
            new Dictionary<string, int> { ["LOADING"] = 5, ["ST03"] = 3 },
            TimeSpan.FromMilliseconds(50), DateTimeOffset.UnixEpoch);
        var workpiece = Item(25);
        Assert.That(line.TryLoad(25, workpiece), Is.True);
        line.AdvanceTicks(4);
        Assert.That(line.FindPosition(workpiece.WorkpieceId)?.StationId, Is.EqualTo("LOADING"));
        Assert.That(line.DrainEvents().Any(x =>
            x.Kind == SimMechanicalEventKind.CaptureRequested), Is.False);

        line.AdvanceTicks();
        Assert.That(line.DrainEvents().Where(x =>
            x.Kind == SimMechanicalEventKind.CaptureRequested)
            .Single().ChannelIds, Is.EqualTo(new[] { "CH01" }));
        line.AdvanceTicks();
        Assert.That(line.FindPosition(workpiece.WorkpieceId)?.StationId, Is.EqualTo("ST03"));
        line.AdvanceTicks(2);
        Assert.That(line.DrainEvents().Any(x =>
            x.Kind == SimMechanicalEventKind.CaptureRequested), Is.False);
        line.AdvanceTicks();
        var events = line.DrainEvents().Where(x =>
            x.Kind == SimMechanicalEventKind.CaptureRequested).ToArray();
        Assert.That(events, Has.Length.EqualTo(2));
        Assert.That(events.SelectMany(x => x.ChannelIds).Order(StringComparer.Ordinal),
            Is.EqualTo(new[] { "CH02", "CH03" }));
        Assert.That(line.TryEject(25, FinalDisposition.Good), Is.False);
        Assert.Throws<ArgumentException>(() => line.TryEject(25, FinalDisposition.Hold));
    }

    [Test]
    public void PhysicalTokensCannotBeDuplicatedOrSilentlyRemappedAfterExit()
    {
        var line = new SimMechanicalLine();
        var first = Item(1);
        var second = Item(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => line.TryLoad(0, first));
        Assert.That(line.TryLoad(1, first), Is.True);
        Assert.That(line.TryLoad(2, second), Is.False, "LOADING occupied: backpressure, no skip.");
        Assert.Throws<InvalidOperationException>(() => line.TryLoad(1, second));
        Assert.Throws<InvalidOperationException>(() => line.TryLoad(3, first));
        Assert.Throws<ArgumentException>(() => line.SetSensorStuck("ST99", true));

        line.AdvanceTicks(200);
        Assert.That(line.TryEject(1, FinalDisposition.Reject), Is.True);
        Assert.Throws<InvalidOperationException>(() => line.TryLoad(1, second));
        Assert.Throws<InvalidOperationException>(() => line.TryLoad(2, first));
        Assert.That(line.TryLoad(2, second), Is.True);
        Assert.That(line.Snapshot().LoadedCount, Is.EqualTo(2));
    }
}
