using AOI.Domain;

namespace AOI.Solutions;

public enum OfflinePublishOutcome
{
    Published,
    Rejected
}

public sealed record OfflinePublishResult(
    OfflinePublishOutcome Outcome, PublishedRelease? Release, string Message);

/// <summary>
/// Operator-facing OFFLINE product management built directly on the existing A6 repository.
/// No Core IPC, no second recipe database and no hardware path: every operation goes through
/// SolutionRepository, SolutionParameterEditor and RoiReleaseWorkflow.
///
/// Activation is deliberately absent. It requires the stopped-Core gate (A0 PR #149) and must
/// not be emulated while Cores are running.
/// </summary>
public sealed class OfflineProductManager
{
    private readonly SolutionRepository repository;
    private readonly string actor;

    public OfflineProductManager(SolutionRepository repository, string actor)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Offline product management requires a named operator.", nameof(actor));
        this.repository = repository;
        this.actor = actor;
    }

    public static OfflineProductManager Open(string solutionsDbPath, string actor) =>
        new(new SolutionRepository(solutionsDbPath), actor);

    public string Actor => actor;

    public SolutionRepository Repository => repository;

    /// <summary>Registers the SIM baseline machine profile. Safe to call once per database.</summary>
    public void EnsureSimProfile() =>
        repository.RegisterProfile(MachineProfile.SimBaseline(), actor);

    /// <summary>
    /// Creates a SIM_ONLY product solution seeded with every station at the legacy default
    /// threshold. Mirrors the existing A6 bootstrap so behaviour is identical to the CLI.
    /// </summary>
    public ProductSolution CreateSimSolution(string solutionId, string displayName,
        double brightThreshold = 127, double minimumBrightFraction = 0.1)
    {
        if (string.IsNullOrWhiteSpace(solutionId) || string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Solution id and display name are required.");
        var solution = new ProductSolution(solutionId, "SIM_MACHINE_01", displayName);
        repository.CreateSolution(solution, actor);
        foreach (var station in ChannelCatalog.All.GroupBy(x => x.StationId))
        {
            repository.SaveDraft(solutionId,
                new StationRecipe(station.Key,
                    station.Select(c => c.ChannelId).ToArray(),
                    new Dictionary<string, double>
                    {
                        ["brightThreshold"] = brightThreshold,
                        ["minimumBrightFraction"] = minimumBrightFraction
                    }),
                expectedRevision: 0, actor);
        }
        return solution;
    }

    /// <summary>Reads the operator-visible draft view for one logical channel.</summary>
    public DraftChannelParameterView ReadChannel(string solutionId, string channelId) =>
        SolutionParameterEditor.ReadDraft(repository, solutionId, channelId);

    /// <summary>
    /// Validates and saves one station draft with optimistic concurrency. Validation runs
    /// before the write so an unusable ROI/parameter set never reaches the database.
    /// </summary>
    public StationDraft SaveStation(string solutionId, StationRecipe recipe, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate();
        RoiReleaseWorkflow.ValidateStationRois(recipe);
        return repository.SaveDraft(solutionId, recipe, expectedRevision, actor);
    }

    /// <summary>
    /// Publishes an immutable Release from the current drafts and re-verifies the persisted
    /// digest. There is no UI-only success: the returned release is the stored A6 record.
    /// </summary>
    public OfflinePublishResult Publish(string solutionId)
    {
        try
        {
            PublishedRelease release = repository.Publish(solutionId, actor);
            release.Verify();
            foreach (StationRecipe station in release.Manifest.Stations)
                RoiReleaseWorkflow.ValidateStationRois(station);
            return new OfflinePublishResult(OfflinePublishOutcome.Published, release,
                "SIM_PRODUCT_PUBLISHED release=" + release.ReleaseId +
                " sha256=" + release.ContentSha256 +
                " stations=" + release.Manifest.Stations.Count);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
            or ArgumentException or KeyNotFoundException)
        {
            return new OfflinePublishResult(OfflinePublishOutcome.Rejected, null, ex.Message);
        }
    }

    /// <summary>Reads back a stored release; proves the publish really persisted.</summary>
    public PublishedRelease RequirePublished(string releaseId) => repository.RequireRelease(releaseId);
}
