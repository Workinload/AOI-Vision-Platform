using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Solutions;

namespace AOI.Host;

/// <summary>
/// Read-only preflight proof of independently staged A6 SIM_ONLY releases.
/// Does not clear A6 SafeHold and NEVER enables real PLC/camera writes.
/// </summary>
public sealed record SimProductRuntime(long Epoch, string ReleaseId,
    PublishedRelease? Published, SimActiveProductSnapshot? Overview)
{
    public static SimProductRuntime Load(string root)
    {
        var master = Path.Combine(root, "master", "sim-product-node.sqlite");
        var slave = Path.Combine(root, "slave", "sim-product-node.sqlite");
        bool hasMaster = File.Exists(master), hasSlave = File.Exists(slave);
        if (!hasMaster && !hasSlave)
            return new(1, "SIM_LIGHT_TEST_RELEASE", null, null);
        if (!hasMaster || !hasSlave || !File.Exists(Path.Combine(root, "solutions.sqlite")))
            throw new InvalidOperationException("SIM_PRODUCT_HOLD: both staged nodes and repository required.");
        var masterNode = new SimChangeoverNode(master, "master", MachineRole.Master).Snapshot();
        var slaveNode = new SimChangeoverNode(slave, "slave", MachineRole.Slave).Snapshot();
        if (masterNode.Phase != NodeChangeoverPhase.CommittedHold ||
            slaveNode.Phase != NodeChangeoverPhase.CommittedHold ||
            !masterNode.SafeHold || !slaveNode.SafeHold ||
            masterNode.RealHardwareWritesEnabled || slaveNode.RealHardwareWritesEnabled ||
            masterNode.ActiveEpoch < 1 || masterNode.ActiveEpoch != slaveNode.ActiveEpoch ||
            string.IsNullOrWhiteSpace(masterNode.ActiveReleaseId) ||
            masterNode.ActiveReleaseId != slaveNode.ActiveReleaseId)
            throw new InvalidDataException("SIM_PRODUCT_HOLD: dual-node release or epoch mismatch.");
        var release = new SolutionRepository(Path.Combine(root, "solutions.sqlite"))
            .RequireRelease(masterNode.ActiveReleaseId);
        release.Verify();
        var stations = release.Manifest.Stations.Select(s =>
        {
            var rule = StationRule(s);
            return new SimActiveStation(s.StationId,
                s.ChannelIds.Order(StringComparer.Ordinal).ToArray(),
                rule.BrightThreshold, rule.MinimumBrightFraction);
        }).OrderBy(x => x.StationId, StringComparer.Ordinal).ToArray();
        var view = new SimActiveProductSnapshot(release.Manifest.SolutionId,
            release.ReleaseId, release.ContentSha256, masterNode.ActiveEpoch,
            IsSimulation: true, Stations: stations);
        view.Validate();
        return new(masterNode.ActiveEpoch, release.ReleaseId, release, view);
    }

    public static SimPixelRule StationRule(StationRecipe station)
    {
        station.Validate();
        double threshold = station.Tunables.TryGetValue("brightThreshold", out var value)
            ? value : 127;
        double minimum = station.Tunables.TryGetValue("minimumBrightFraction", out var fraction)
            ? fraction : 0.1;
        if (!double.IsFinite(threshold) || threshold != Math.Floor(threshold) ||
            threshold is < 1 or > 254 || !double.IsFinite(minimum) ||
            minimum is < 0 or > 1)
            throw new InvalidDataException("Unsafe published station pixel threshold.");
        return new SimPixelRule((byte)threshold, minimum);
    }

    public SimPixelRule RuleFor(ChannelDefinition channel)
    {
        if (Published is null) return new SimPixelRule(127, 0.1);
        var station = Published.Manifest.Stations.SingleOrDefault(s =>
            string.Equals(s.StationId, channel.StationId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Missing recipe station in published release.");
        return StationRule(station);
    }

    public void VerifyWorkpiece(WorkpieceIdentity workpiece)
    {
        workpiece.Validate();
        if (workpiece.Epoch != Epoch || workpiece.ReleaseId != ReleaseId)
            throw new InvalidDataException("SIM_PRODUCT_HOLD: peer item has wrong product release or epoch.");
    }
}
