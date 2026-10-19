using AOI.Contracts;
using AOI.Domain;

namespace AOI.Solutions;

/// <summary>
/// Outcome of one real dual-node offline changeover. `Committed` is only true when the
/// durable coordinator reached Committed AND both persisted nodes read back the same
/// ReleaseId/Epoch in COMMITTED_HOLD. A HOLD result is never reported as success.
/// </summary>
public sealed record OfflineDualActivationResult(
    string ReleaseId, string ContentSha256, long PreviousEpoch, long NextEpoch,
    NodeSnapshot Master, NodeSnapshot Slave, ChangeoverPhase Phase, string? Detail)
{
    public bool Committed =>
        Phase == ChangeoverPhase.Committed &&
        Master.Phase == NodeChangeoverPhase.CommittedHold &&
        Slave.Phase == NodeChangeoverPhase.CommittedHold &&
        string.Equals(Master.ActiveReleaseId, ReleaseId, StringComparison.Ordinal) &&
        string.Equals(Slave.ActiveReleaseId, ReleaseId, StringComparison.Ordinal) &&
        Master.ActiveEpoch == NextEpoch && Slave.ActiveEpoch == NextEpoch;
}

/// <summary>
/// The ONE implementation of the two-node offline activation used by both the operator GUI
/// and the Recipes CLI. It never touches FINS, cameras, PLC outputs or a running Core:
/// activation is valid only while the caller holds BOTH Core OS leases and the run root is a
/// clean activation root. Callers that already hold the lease use
/// <see cref="ActivateUnderLease"/>; <see cref="Activate"/> acquires it for CLI-style use.
/// </summary>
public static class OfflineDualActivation
{
    public const string SolutionDatabaseFileName = "solutions.sqlite";

    /// <summary>CLI-style entry: acquires both Core leases, requires a clean root, activates.</summary>
    public static OfflineDualActivationResult Activate(string runRoot, string releaseId, string actor)
    {
        string root = Path.GetFullPath(runRoot);
        using OfflineCoreOperationGate.OfflineLease lease =
            OfflineCoreOperationGate.AcquireStoppedCores(root);
        lease.RequireCleanActivationRoot();
        return ActivateUnderLease(root, releaseId, actor, lease);
    }

    /// <summary>
    /// Activates under an ALREADY HELD lease (the GUI writer session). The lease must be the one
    /// that owns this run root; callers must keep it held until this method returns.
    /// </summary>
    public static OfflineDualActivationResult ActivateUnderLease(string runRoot,
        string releaseId, string actor, OfflineCoreOperationGate.OfflineLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        SolutionSafety.RequireActor(actor);
        string root = Path.GetFullPath(runRoot);

        // The lease is the ONLY proof that both Cores are stopped for this exact root.
        lease.RequireCleanActivationRoot();

        var repository = new SolutionRepository(
            Path.Combine(root, SolutionDatabaseFileName), readOnly: true);
        PublishedRelease release;
        try
        {
            release = repository.RequireRelease(releaseId);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException("UNKNOWN_RELEASE_ID: " + releaseId, ex);
        }
        release.Verify();
        RequireActivatableRules(release);
        RequireNoProductionEvidence(root);

        var master = new SimChangeoverNode(
            Path.Combine(root, "master", "sim-product-node.sqlite"), "master", MachineRole.Master);
        var slave = new SimChangeoverNode(
            Path.Combine(root, "slave", "sim-product-node.sqlite"), "slave", MachineRole.Slave);
        NodeSnapshot beforeMaster = master.Snapshot();
        NodeSnapshot beforeSlave = slave.Snapshot();
        if (beforeMaster.ActiveEpoch != beforeSlave.ActiveEpoch ||
            !string.Equals(beforeMaster.ActiveReleaseId, beforeSlave.ActiveReleaseId,
                StringComparison.Ordinal) ||
            !beforeMaster.SafeHold || !beforeSlave.SafeHold ||
            beforeMaster.Phase is not (NodeChangeoverPhase.Idle or NodeChangeoverPhase.CommittedHold) ||
            beforeSlave.Phase is not (NodeChangeoverPhase.Idle or NodeChangeoverPhase.CommittedHold))
            throw new InvalidOperationException(
                "ACTIVATION_BLOCKED: nodes are not jointly idle in safe hold with the same ReleaseId/Epoch.");

        long nextEpoch = checked(beforeMaster.ActiveEpoch + 1);
        var coordinator = new ChangeoverCoordinator(
            Path.Combine(root, "sim-product-coordinator.sqlite"));
        var request = new ChangeoverRequest(Guid.NewGuid(), release,
            beforeMaster.ActiveEpoch, beforeMaster.ActiveReleaseId, nextEpoch, actor);
        // Evidence for purely offline SQLite node emulators ONLY: no hardware output and no
        // remote PLC connectivity exists in this process, so real physical writes stay zero.
        ChangeoverStatus status = coordinator.Execute(
            request, master, slave, SimInterlocks.AllSafeForOfflineTest);

        NodeSnapshot afterMaster = master.Snapshot();
        NodeSnapshot afterSlave = slave.Snapshot();
        var result = new OfflineDualActivationResult(release.ReleaseId, release.ContentSha256,
            beforeMaster.ActiveEpoch, nextEpoch, afterMaster, afterSlave, status.Phase, status.Detail);
        if (!result.Committed)
            throw new InvalidOperationException(
                "ACTIVATION_HOLD: " + status.Phase + " " + (status.Detail ?? "no detail") +
                "; both nodes stay in safe HOLD and no physical write is attempted.");
        return result;
    }

    /// <summary>Re-verifies every published station rule against the plugins installed here.</summary>
    private static void RequireActivatableRules(PublishedRelease release)
    {
        foreach (StationRecipe station in release.Manifest.Stations)
        {
            station.Validate();
            RoiReleaseWorkflow.ValidateStationRois(station);
            // RoiPixelPreview.ReadRule throws for an out-of-range threshold/fraction.
            _ = RoiPixelPreview.ReadRule(station);
            if (station.InspectionTargets is null) continue;
            foreach (InspectionAlgorithmTarget target in station.InspectionTargets)
            {
                target.Validate();
                if (!OfflineRecipeValidator.InstalledInspectionAlgorithms.Contains(
                        target.AlgorithmId, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        "INSPECTION_PROCESSOR_NOT_INSTALLED: " + target.AlgorithmId);
            }
        }
    }

    /// <summary>
    /// A production run, virtual conveyor or persisted timing evidence means the line has real
    /// history; activation must stop and let the operator archive it instead of reconfiguring.
    /// </summary>
    private static void RequireNoProductionEvidence(string root)
    {
        string[] names = ["production.db", "sim-full-cycle.db"];
        foreach (string role in new[] { "master", "slave" })
            foreach (string name in names)
            {
                string path = Path.Combine(root, role, name);
                if (File.Exists(path))
                    throw new InvalidOperationException(
                        "ACTIVATION_BLOCKED: existing production/conveyor evidence " + path +
                        "; archive or inspect it, never delete or replay automatically.");
            }
    }
}
