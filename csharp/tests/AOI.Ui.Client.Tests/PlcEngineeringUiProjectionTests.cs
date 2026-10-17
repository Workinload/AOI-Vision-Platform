using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class PlcEngineeringUiProjectionTests
{
    private static MachineSnapshot Sample(PlcEngineeringSnapshot engineering) => new(
        "master", MachineRole.Master, "SIM_READY",
        null, null, Array.Empty<ChannelSnapshot>(),
        DateTimeOffset.UtcNow, false, "SIM_FULL")
    {
        PlcEngineering = engineering
    };

    private static PlcEngineeringSnapshot Data()
    {
        var signals = Enumerable.Range(1, 45).Select(i => new PlcEngineeringSignal(
            $"S{i:D2}", $"D{i}", "MACHINE", "MASTER", "PLC_TO_PC", "WORD",
            $"Engineering signal {i}", "0=OFF", "DECLARATION_ONLY", "", false)).ToArray();
        var gates = Enumerable.Range(1, 14).Select(i => new PlcFieldGate(
            $"FG-{i:D2}", "Pending signoff", i <= 7 ? "BLOCKED" : "OPEN",
            "Evidence is still required", false)).ToArray();
        return new PlcEngineeringSnapshot(signals, gates, false, DateTimeOffset.UtcNow);
    }

    [Test]
    public void ValidCatalogIsVisibleAsStaticEvidenceOnly()
    {
        var model = MonitorProjection.FromSnapshot(Sample(Data()),
            MachineRole.Master, DateTimeOffset.UtcNow);
        Assert.That(model.PlcEngineering, Is.Not.Null);
        Assert.That(model.PlcEngineering!.IsFieldLive, Is.False);
        Assert.That(model.PlcEngineering.Signals, Has.Count.EqualTo(45));
        Assert.That(model.PlcEngineering.Gates.Count(x => x.Status == "BLOCKED"), Is.EqualTo(7));
    }

    [Test]
    public void RejectForgedFieldLiveAndAuthorizationState()
    {
        var registry = Data();
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            Sample(registry with { IsFieldLive = true }),
            MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            Sample(registry with { Signals = registry.Signals.Select((s, i) =>
                i == 1 ? s with { ProductionWriteEnabled = true } : s).ToArray() }),
            MachineRole.Master, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => MonitorProjection.FromSnapshot(
            Sample(registry with { Gates = registry.Gates.Skip(1).ToArray() }),
            MachineRole.Master, DateTimeOffset.UtcNow));
    }
}
