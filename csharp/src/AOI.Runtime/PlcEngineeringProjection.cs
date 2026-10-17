using AOI.Contracts;
using AOI.Runtime.Plc;

namespace AOI.Runtime;

/// <summary>A2 static evidence -> read-only IPC projection, NEVER field PLC values.</summary>
public static class PlcEngineeringProjection
{
    public static PlcEngineeringSnapshot Snapshot { get; } = Create();

    private static PlcEngineeringSnapshot Create()
    {
        var signals = A2SignalRegistry.Signals
            .OrderBy(s => s.StartWord)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new PlcEngineeringSignal(
                s.Id, s.Address,
                s.Fields["StationId"], s.Fields["HostRole"],
                s.Fields["Direction"], s.Fields["DataType"],
                s.Fields["PurposeCN"], s.Fields["ValueLegend"],
                s.ValidationState, s.ConflictGroup,
                s.ProductionWriteEnabled))
            .ToArray();
        var gates = A2SignalRegistry.FieldGates
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => new PlcFieldGate(g.Id, g.Fields["Topic"], g.Status,
                g.Fields["RequiredEvidence"], g.ProductionWriteAllowed))
            .ToArray();
        var snapshot = new PlcEngineeringSnapshot(signals, gates,
            IsFieldLive: false, CatalogCapturedUtc: DateTimeOffset.UtcNow);
        snapshot.Validate();
        return snapshot;
    }
}
