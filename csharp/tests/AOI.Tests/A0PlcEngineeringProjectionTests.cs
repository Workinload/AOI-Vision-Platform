using AOI.Contracts;
using AOI.Runtime;
using AOI.Runtime.Plc;
using NUnit.Framework;

namespace AOI.Tests;

[TestFixture]
public sealed class A0PlcEngineeringProjectionTests
{
    [Test]
    public void All45SignalsAnd14FieldGatesSurviveA2ProjectionWithoutEnablingWrites()
    {
        var data = PlcEngineeringProjection.Snapshot;
        data.Validate();
        Assert.That(data.Signals, Has.Count.EqualTo(45));
        Assert.That(data.Gates, Has.Count.EqualTo(14));
        Assert.That(data.Signals.All(s => !s.ProductionWriteEnabled), Is.True);
        Assert.That(data.Gates.All(g => !g.ProductionWriteAllowed), Is.True);
        Assert.That(data.IsFieldLive, Is.False);
        Assert.That(data.Gates.Count(g => g.Status == "BLOCKED"), Is.EqualTo(7));
        Assert.That(data.Gates.Count(g => g.Status == "OPEN"), Is.EqualTo(7));
        Assert.That(data.Signals.Single(x => x.SignalId == "SIG_FINAL_QUALITY").DmAddress, Is.EqualTo("D1016"));
        Assert.That(data.Signals.Single(x => x.SignalId == "SIG_BARCODE_WORDS").DmAddress, Is.EqualTo("D2000..D2019"));
        Assert.That(data.Signals.Single(x => x.SignalId == "SIG_CAM_REAR_LINE").ValidationState, Is.EqualTo("BLOCKED_CONFLICT"));
        Assert.That(data.Gates.Single(x => x.GateId == "FG-05").Status, Is.EqualTo("BLOCKED"));
        Assert.That(data.Signals.Select(s => s.SignalId), Is.EquivalentTo(A2SignalRegistry.Signals.Select(s => s.Id)));
        Assert.That(data.Gates.Select(s => s.GateId), Is.EquivalentTo(A2SignalRegistry.FieldGates.Select(s => s.Id)));
        var roundtrip = WireFormat.Parse<PlcEngineeringSnapshot>(WireFormat.Serialize(data));
        roundtrip.Validate();
        Assert.That(roundtrip.Signals.Count, Is.EqualTo(45));
    }

    [Test]
    public void ForgedLiveFieldStateOrWriteAuthorizationFailsClosed()
    {
        var original = PlcEngineeringProjection.Snapshot;
        Assert.Throws<InvalidDataException>(() =>
            (original with { IsFieldLive = true }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (original with { Signals = original.Signals.Skip(1).ToArray() }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (original with { Signals = original.Signals.Select((s, index) =>
                index == 0 ? s with { ProductionWriteEnabled = true } : s).ToArray() }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (original with { Gates = original.Gates.Select((g, index) =>
                index == 0 ? g with { ProductionWriteAllowed = true } : g).ToArray() }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (original with { Gates = original.Gates.Select((g, index) =>
                index == 0 ? g with { Status = "APPROVED" } : g).ToArray() }).Validate());
    }
}
