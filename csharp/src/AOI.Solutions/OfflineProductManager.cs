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

    public static OfflineProductManager Open(string solutionsDbPath, string actor,
        bool readOnly = false) =>
        new(new SolutionRepository(solutionsDbPath, readOnly), actor);

    public string Actor => actor;

    public SolutionRepository Repository => repository;

    /// <summary>Registers the SIM baseline machine profile. Safe to call once per database.</summary>
    public void EnsureSimProfile() =>
        repository.RegisterProfile(MachineProfile.SimBaseline(), actor);

    /// <summary>Fast pre-validation so a bad id/name never starts a product creation.</summary>
    public static void ValidateNewSolution(string solutionId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(solutionId) || string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Solution id and display name are required.");
        SolutionSafety.RequireId(solutionId, nameof(solutionId));
        if (displayName.Length > 128)
            throw new ArgumentException("Invalid product name.");
    }

    /// <summary>
    /// Creates a SIM_ONLY product solution seeded with every station at the legacy default
    /// threshold. The product row and ALL 7 station drafts commit in one A6 transaction:
    /// a failure leaves no empty/partial product. Mirrors the A6 CLI defaults.
    /// </summary>
    public ProductSolution CreateSimSolution(string solutionId, string displayName,
        double brightThreshold = 127, double minimumBrightFraction = 0.1)
    {
        ValidateNewSolution(solutionId, displayName);
        // StationRecipe.Validate only bounds these tunables; the SIM_ONLY station rule has
        // its own ranges. Reject them BEFORE any product row or station draft is written.
        if (!double.IsFinite(brightThreshold) || brightThreshold != Math.Truncate(brightThreshold) ||
            brightThreshold is < 1 or > 254)
            throw new ArgumentOutOfRangeException(nameof(brightThreshold),
                "SIM_ONLY brightThreshold must be a finite integer from 1 to 254.");
        if (!double.IsFinite(minimumBrightFraction) || minimumBrightFraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumBrightFraction),
                "SIM_ONLY minimumBrightFraction must be a finite value from 0 to 1.");
        var solution = new ProductSolution(solutionId, "SIM_MACHINE_01", displayName);
        var drafts = new Dictionary<string, StationRecipe>(StringComparer.Ordinal);
        foreach (var station in ChannelCatalog.All.GroupBy(x => x.StationId))
            drafts[station.Key] = new StationRecipe(station.Key,
                station.Select(c => c.ChannelId).ToArray(),
                new Dictionary<string, double>
                {
                    ["brightThreshold"] = brightThreshold,
                    ["minimumBrightFraction"] = minimumBrightFraction
                });
        repository.CreateSolutionWithDrafts(solution, drafts, actor);
        return solution;
    }

    /// <summary>Read-only list of products actually present in this A6 database.</summary>
    public IReadOnlyList<ProductSolution> ListSolutions() => repository.ListSolutions();

    /// <summary>Reads the operator-visible draft view for one logical channel.</summary>
    public DraftChannelParameterView ReadChannel(string solutionId, string channelId) =>
        SolutionParameterEditor.ReadDraft(repository, solutionId, channelId);

    /// <summary>
    /// Validates and saves one station draft with optimistic concurrency. Installed-plugin,
    /// parameter, ROI and real-image-size validation runs BEFORE the write so an unusable
    /// set never reaches the database. A ROI needs its verified source-frame size.
    /// </summary>
    public StationDraft SaveStation(string solutionId, StationRecipe recipe, long expectedRevision,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var previous = repository.GetDraft(solutionId, recipe.StationId)?.Recipe;
        OfflineRecipeValidator.RequireValid(recipe, verifiedFrames, previous);
        return repository.SaveDraft(solutionId, recipe, expectedRevision, actor);
    }

    /// <summary>Read-only whole-product validation; used by the offline editor before publish.</summary>
    public IReadOnlyList<OfflineRecipeIssue> ValidateSolution(string solutionId,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null) =>
        OfflineRecipeValidator.InspectSolution(repository, solutionId, verifiedFrames);

    /// <summary>
    /// Legacy/CLI-compatible publish: pre-validates installed plugins, ranges and ROI image
    /// sizes before calling the repository, then reads the persisted record back.
    /// </summary>
    public OfflinePublishResult Publish(string solutionId,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null)
    {
        try
        {
            var issues = OfflineRecipeValidator.InspectSolution(repository, solutionId, verifiedFrames);
            if (issues.Count != 0)
                return new OfflinePublishResult(OfflinePublishOutcome.Rejected, null,
                    "发布前校验失败，未写入任何 Release：" + OfflineRecipeValidator.Describe(issues));
            PublishedRelease release = repository.Publish(solutionId, actor);
            release.Verify();
            return new OfflinePublishResult(OfflinePublishOutcome.Published, release,
                "SIM_PRODUCT_PUBLISHED release=" + release.ReleaseId +
                " sha256=" + release.ContentSha256 +
                " stations=" + release.Manifest.Stations.Count);
        }
        catch (Exception ex) when (IsPublishFailure(ex))
        {
            return new OfflinePublishResult(OfflinePublishOutcome.Rejected, null, ex.Message);
        }
    }

    /// <summary>
    /// Operator-safe publish. The repository verifies the whole-product station revision
    /// snapshot (CAS) and runs the installed-plugin/ROI validation inside ONE immediate A6
    /// transaction before inserting the immutable release. A stale snapshot or any invalid
    /// draft rejects the publish and leaves no new Release row.
    /// </summary>
    public OfflinePublishResult Publish(string solutionId,
        IReadOnlyDictionary<string, long> expectedStationRevisions,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames)
    {
        ArgumentNullException.ThrowIfNull(expectedStationRevisions);
        try
        {
            var release = repository.Publish(solutionId, actor, expectedStationRevisions,
                manifest => ValidateManifestStations(manifest, verifiedFrames));
            release.Verify();
            return new OfflinePublishResult(OfflinePublishOutcome.Published, release,
                "SIM_PRODUCT_PUBLISHED release=" + release.ReleaseId +
                " sha256=" + release.ContentSha256 +
                " stations=" + release.Manifest.Stations.Count);
        }
        catch (Exception ex) when (IsPublishFailure(ex))
        {
            return new OfflinePublishResult(OfflinePublishOutcome.Rejected, null, ex.Message);
        }
    }

    /// <summary>Whole-product draft revisions the operator must explicitly confirm.</summary>
    public IReadOnlyDictionary<string, long> ListDraftRevisions(string solutionId) =>
        repository.ListDraftRevisions(solutionId);

    private static void ValidateManifestStations(ReleaseManifest manifest,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames)
    {
        foreach (var station in manifest.Stations)
        {
            var issues = OfflineRecipeValidator.Inspect(station, verifiedFrames);
            if (issues.Count != 0)
                throw new InvalidDataException("发布前校验失败，未写入任何 Release：" +
                    OfflineRecipeValidator.Describe(issues));
        }
    }

    private static bool IsPublishFailure(Exception ex) =>
        ex is InvalidDataException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or System.Data.Common.DbException;

    /// <summary>Read-only list of releases actually persisted in this A6 database.</summary>
    public IReadOnlyList<PublishedRelease> ListReleases() => repository.ListReleases();

    /// <summary>Reads back a stored release; proves the publish really persisted.</summary>
    public PublishedRelease RequirePublished(string releaseId) => repository.RequireRelease(releaseId);
}