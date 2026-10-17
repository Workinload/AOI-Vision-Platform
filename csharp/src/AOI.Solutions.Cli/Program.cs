using AOI.Contracts;
using AOI.Domain;
using AOI.Solutions;
using System.Globalization;

if (args.Length < 2 || args[0] is not
    ("bootstrap" or "tune" or "publish" or "activate" or "status"))
{
    Console.Error.WriteLine("""
        AOI.Solutions.Cli - SIM_ONLY local offline product editor
          bootstrap <root> <solutionId> <displayName> <actor>
          tune <root> <solutionId> <stationId> <expectedRevision> <threshold(1..254)> <minFraction(0..1)> <actor>
          publish <root> <solutionId> <actor>
          activate <root> <releaseId> <actor>
          status <root>
        DANGER: No command touches physical PLC or clears A6 changeover SafeHold.
        """);
    return 2;
}
try
{
    string mode = args[0];
    string root = Path.GetFullPath(args[1]);
    var repository = new SolutionRepository(Path.Combine(root, "solutions.sqlite"));
    if (mode == "bootstrap")
    {
        if (args.Length != 5) throw new ArgumentException("bootstrap requires root, ID, name, actor");
        string id = args[2], name = args[3], actor = args[4];
        repository.RegisterProfile(MachineProfile.SimBaseline(), actor);
        repository.CreateSolution(new ProductSolution(id, "SIM_MACHINE_01", name), actor);
        foreach (var station in ChannelCatalog.All.GroupBy(x => x.StationId))
            repository.SaveDraft(id,
                new StationRecipe(station.Key,
                    station.Select(c => c.ChannelId).ToArray(),
                    new Dictionary<string, double>
                    {
                        ["brightThreshold"] = 127,
                        ["minimumBrightFraction"] = 0.1
                    }),
                expectedRevision: 0, actor);
        Console.WriteLine($"SIM_PRODUCT_BOOTSTRAP_OK solution={id} stations={ChannelCatalog.All.Select(x => x.StationId).Distinct().Count()} channels=11");
    }
    else if (mode == "tune")
    {
        if (args.Length != 8) throw new ArgumentException("tune requires solutionId stationId revision threshold fraction actor");
        string solutionId = args[2], stationId = args[3], actor = args[7];
        long revision = long.Parse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture);
        double threshold = double.Parse(args[5], CultureInfo.InvariantCulture);
        double fraction = double.Parse(args[6], CultureInfo.InvariantCulture);
        if (!double.IsFinite(threshold) || threshold != Math.Floor(threshold) ||
            threshold is < 1 or > 254 ||
            !double.IsFinite(fraction) || fraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(args), "Unsafe SIM_ONLY pixel thresholds.");
        var draft = repository.GetDraft(solutionId, stationId)
            ?? throw new InvalidOperationException("No station draft; run bootstrap first.");
        var tunables = new Dictionary<string, double>(draft.Recipe.Tunables, StringComparer.Ordinal)
        {
            ["brightThreshold"] = threshold,
            ["minimumBrightFraction"] = fraction
        };
        var saved = repository.SaveDraft(solutionId,
            new StationRecipe(draft.Recipe.StationId, draft.Recipe.ChannelIds, tunables),
            revision, actor);
        Console.WriteLine($"SIM_PRODUCT_TUNE_OK station={saved.StationId} revision={saved.Revision} threshold={threshold} fraction={fraction}");
    }
    else if (mode == "publish")
    {
        if (args.Length != 4) throw new ArgumentException("publish requires root, solutionId, actor");
        var release = repository.Publish(args[2], args[3]);
        // Force validation of every station's actual pixel rule, not just digest.
        foreach (var station in release.Manifest.Stations)
            RequireSafeRule(station);
        Console.WriteLine($"SIM_PRODUCT_PUBLISHED release={release.ReleaseId} sha256={release.ContentSha256} stations={release.Manifest.Stations.Count}");
    }
    else if (mode == "activate")
    {
        if (args.Length != 4) throw new ArgumentException("activate requires root, releaseId, actor");
        var release = repository.RequireRelease(args[2]);
        foreach (var station in release.Manifest.Stations) RequireSafeRule(station);
        var forbidden = new[]
        {
            Path.Combine(root, "master", "production.db"),
            Path.Combine(root, "slave", "production.db"),
            Path.Combine(root, "master", "sim-full-cycle.db"),
            Path.Combine(root, "slave", "sim-full-cycle.db")
        };
        if (forbidden.Any(File.Exists))
            throw new InvalidOperationException(
                "ACTIVATION_BLOCKED: production WAL or virtual conveyor evidence exists; do not reconfigure an active/unknown line.");
        var master = new SimChangeoverNode(Path.Combine(root, "master", "sim-product-node.sqlite"),
            "master", MachineRole.Master);
        var slave = new SimChangeoverNode(Path.Combine(root, "slave", "sim-product-node.sqlite"),
            "slave", MachineRole.Slave);
        var beforeMaster = master.Snapshot();
        var beforeSlave = slave.Snapshot();
        if (beforeMaster.ActiveEpoch != beforeSlave.ActiveEpoch ||
            beforeMaster.ActiveReleaseId != beforeSlave.ActiveReleaseId ||
            !beforeMaster.SafeHold || !beforeSlave.SafeHold ||
            beforeMaster.Phase != NodeChangeoverPhase.Idle ||
            beforeSlave.Phase != NodeChangeoverPhase.Idle)
            throw new InvalidOperationException("ACTIVATION_BLOCKED: nodes not jointly idle in safe hold.");
        var coordinator = new ChangeoverCoordinator(Path.Combine(root, "sim-product-coordinator.sqlite"));
        var request = new ChangeoverRequest(Guid.NewGuid(), release,
            beforeMaster.ActiveEpoch, beforeMaster.ActiveReleaseId,
            checked(beforeMaster.ActiveEpoch + 1), args[3]);
        // This evidence is for purely offline SQLite node emulators ONLY.
        // Neither hardware output nor remote PLC connectivity exists here.
        var status = coordinator.Execute(request, master, slave, SimInterlocks.AllSafeForOfflineTest);
        if (status.Phase != ChangeoverPhase.Committed)
            throw new InvalidOperationException("ACTIVATION_HOLD: " + status.Phase + " " + status.Detail);
        Console.WriteLine($"SIM_PRODUCT_DUAL_COMMITTED_HOLD release={release.ReleaseId} epoch={status.NextEpoch} nodes=2 real_writes=0");
    }
    else if (mode == "status")
    {
        if (args.Length != 2) throw new ArgumentException("status requires only root");
        var files = new[] {
            Path.Combine(root, "master", "sim-product-node.sqlite"),
            Path.Combine(root, "slave", "sim-product-node.sqlite")
        };
        if (!files.All(File.Exists))
        {
            Console.WriteLine("SIM_PRODUCT_NOT_ACTIVATED");
        }
        else
        {
            var master = new SimChangeoverNode(files[0], "master", MachineRole.Master).Snapshot();
            var slave = new SimChangeoverNode(files[1], "slave", MachineRole.Slave).Snapshot();
            Console.WriteLine($"SIM_PRODUCT_STATUS master={master.ActiveReleaseId} m_epoch={master.ActiveEpoch} m_phase={master.Phase} " +
                $"slave={slave.ActiveReleaseId} s_epoch={slave.ActiveEpoch} s_phase={slave.Phase} real_writes=0");
        }
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("SIM_PRODUCT_COMMAND_FAILED " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}

static void RequireSafeRule(StationRecipe station)
{
    station.Validate();
    double threshold = station.Tunables.TryGetValue("brightThreshold", out var v) ? v : 127;
    double fraction = station.Tunables.TryGetValue("minimumBrightFraction", out var f) ? f : 0.1;
    if (!double.IsFinite(threshold) || threshold != Math.Floor(threshold) ||
        threshold is < 1 or > 254 || !double.IsFinite(fraction) || fraction is < 0 or > 1)
        throw new InvalidDataException("Unsafe station rule for offline publication: " + station.StationId);
}
