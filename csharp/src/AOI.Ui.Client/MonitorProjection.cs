using AOI.Contracts;
using AOI.Domain;

namespace AOI.Ui.Client;

/// <summary>Read-only, fail-closed projection of the v0.1 Core snapshot contract.</summary>
public sealed record ChannelMonitor(
    ChannelDefinition Definition, string Status, string Detail, DateTimeOffset? UpdatedUtc,
    bool IsSimulated, bool IsCurrentWorkpiece);

public sealed record MonitorProjection(
    string NodeId, MachineRole Role, string MachineState, string SimulationMode,
    bool IsOnline, string SafetyNotice, WorkpieceIdentity? Workpiece, FinalDecision? Final,
    DateTimeOffset UpdatedUtc, IReadOnlyList<ChannelMonitor> Channels)
{
    public ProductionOverview? Production { get; init; }
    public PlcEngineeringSnapshot? PlcEngineering { get; init; }
    public WorkpieceHoldOverview? Holds { get; init; }
    public SimActiveProductSnapshot? ActiveProduct { get; init; }
    public IReadOnlyList<RoiDraftState>? RoiDrafts { get; init; }
    public SimConveyorOverview? Conveyor { get; init; }

    public static MonitorProjection Offline(MachineRole role) => new(
        role == MachineRole.Master ? "master" : "slave", role, "IPC_OFFLINE", "SIM_ONLY",
        false, "IPC disconnected · real hardware writes prohibited", null, null,
        DateTimeOffset.MinValue, ChannelCatalog.All.Select(c =>
            new ChannelMonitor(c, "NO_DATA", "No live Core snapshot", null, false, false)).ToArray());

    public static MonitorProjection FromSnapshot(MachineSnapshot snapshot, MachineRole expectedRole,
        DateTimeOffset now, TimeSpan? freshness = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.RealHardwareWritesEnabled || snapshot.Role != expectedRole ||
            !string.Equals(snapshot.NodeId, expectedRole == MachineRole.Master ? "master" : "slave",
                StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(snapshot.SimulationMode) ||
             !snapshot.SimulationMode.StartsWith("SIM_", StringComparison.Ordinal)) ||
            snapshot.UpdatedUtc > now.AddSeconds(5) ||
            now - snapshot.UpdatedUtc > (freshness ?? TimeSpan.FromSeconds(5)))
            throw new InvalidDataException("Unsafe, stale, inconsistent or non-simulation Core snapshot.");
        if (snapshot.LatestFinal is { IsSimulation: false })
            throw new InvalidDataException("Production decision must not appear in SIM_ONLY UI.");
        if (snapshot.LatestWorkpiece is not null)
            snapshot.LatestWorkpiece.Validate();
        if (snapshot.Channels is null)
            throw new InvalidDataException("Missing channel list.");
        snapshot.Production?.Validate();
        snapshot.PlcEngineering?.Validate();
        snapshot.Holds?.Validate();
        snapshot.ActiveProduct?.Validate();
        snapshot.Conveyor?.Validate();
        if (expectedRole == MachineRole.Slave && snapshot.Conveyor is not null)
            throw new InvalidDataException("Remote Slave cannot claim Master mechanical movement.");
        if (snapshot.RoiDrafts is { } drafts)
        {
            if (drafts.Count > 11 ||
                drafts.Select(d => d.ChannelId).Distinct(StringComparer.Ordinal).Count() != drafts.Count)
                throw new InvalidDataException("Invalid or duplicate ROI drafts in Core IPC.");
            foreach (var draft in drafts)
            {
                draft.Validate();
                if (ChannelCatalog.Require(draft.ChannelId).OwnerRole != expectedRole)
                    throw new InvalidDataException("Remote node cannot inject editable ROI into this UI.");
            }
        }
        var byId = new Dictionary<string, ChannelSnapshot>(StringComparer.Ordinal);
        foreach (var c in snapshot.Channels)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.ChannelId) || !byId.TryAdd(c.ChannelId, c))
                throw new InvalidDataException("Duplicate or invalid channel snapshot.");
            var def = ChannelCatalog.Require(c.ChannelId);
            if (def.StationId != c.StationId || def.PhysicalDeviceId != c.PhysicalDeviceId ||
                c.LastUpdatedUtc > now.AddSeconds(5) ||
                (c.WorkpieceId is not null && c.WorkpieceId != snapshot.LatestWorkpiece?.WorkpieceId))
                throw new InvalidDataException("Channel topology, time or workpiece identity mismatch.");
        }
        return new MonitorProjection(snapshot.NodeId, snapshot.Role, snapshot.MachineState,
            snapshot.SimulationMode, true, "SIM_ONLY · REAL PLC/camera commands unavailable",
            snapshot.LatestWorkpiece, snapshot.LatestFinal, snapshot.UpdatedUtc,
            ChannelCatalog.All.Select(def =>
            {
                if (!byId.TryGetValue(def.ChannelId, out var info))
                    return new ChannelMonitor(def, "NO_DATA", "Channel omitted by Core", null, false, false);
                var local = def.OwnerRole == expectedRole;
                var current = info.WorkpieceId is not null &&
                    info.WorkpieceId == snapshot.LatestWorkpiece?.WorkpieceId;
                return new ChannelMonitor(def, info.Status,
                    current ? "Current workpiece" : local ? "No current captured frame" : "Remote channel",
                    info.LastUpdatedUtc, info.IsSimulated, current);
            }).ToArray())
        {
            Production = snapshot.Production,
            PlcEngineering = snapshot.PlcEngineering,
            Holds = snapshot.Holds,
            ActiveProduct = snapshot.ActiveProduct,
            RoiDrafts = snapshot.RoiDrafts,
            Conveyor = snapshot.Conveyor
        };
    }
}

/// <summary>Local-only ROI draft. Cannot issue IPC or hardware writes.</summary>
public sealed record RoiDraft(string ChannelId, double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        _ = ChannelCatalog.Require(ChannelId);
        if (new[] { X, Y, Width, Height }.Any(v => !double.IsFinite(v)) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            X + Width > 1 || Y + Height > 1)
            throw new ArgumentOutOfRangeException(nameof(Width), "ROI must fit normalized [0,1] image space.");
    }
}
