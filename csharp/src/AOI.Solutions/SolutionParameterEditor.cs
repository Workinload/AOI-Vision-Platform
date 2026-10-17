using AOI.Domain;

namespace AOI.Solutions;

/// <summary>
/// Operator-editable proposal, not a released recipe. Capture target is per
/// PHYSICAL camera while inspection target and ROI are per LOGICAL channel.
/// </summary>
public sealed record DraftChannelParameterView(
    string SolutionId, string StationId, long StationRevision,
    string PhysicalDeviceId, string ChannelId,
    CameraCaptureTarget? CaptureTarget, InspectionAlgorithmTarget? InspectionTarget,
    NormalizedInspectionRoi InspectionRoi,
    IReadOnlyDictionary<string, double> StationTunables);

public static class SolutionParameterEditor
{
    public static DraftChannelParameterView ReadDraft(
        SolutionRepository repository, string solutionId, string channelId)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var channel = ChannelCatalog.Require(channelId);
        var draft = repository.GetDraft(solutionId, channel.StationId)
            ?? throw new KeyNotFoundException("Missing product station draft.");
        var recipe = draft.Recipe;
        recipe.Validate();
        var capture = recipe.CaptureTargets?.SingleOrDefault(
            c => c.PhysicalDeviceId == channel.PhysicalDeviceId)?.Canonical();
        var inspector = recipe.InspectionTargets?.SingleOrDefault(
            i => i.ChannelId == channelId)?.Canonical();
        var roi = RoiReleaseWorkflow.ReadStationRoi(recipe, channelId)?.Bounds
            ?? NormalizedInspectionRoi.FullFrame;
        return new(solutionId, channel.StationId, draft.Revision,
            channel.PhysicalDeviceId, channelId, capture, inspector, roi,
            RecipeParameterRules.FreezeNumbers(recipe.Tunables));
    }

    /// <summary>
    /// One optimistic station edit updates ONE physical capture target and
    /// automatically applies to both logical children of a line-scan camera.
    /// No SDK, device apply or field write occurs.
    /// </summary>
    public static StationDraft SetCaptureTarget(
        SolutionRepository repository, string solutionId,
        CameraCaptureTarget target, long expectedStationRevision, string actor)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        var channels = ChannelCatalog.All
            .Where(c => c.PhysicalDeviceId == target.PhysicalDeviceId).ToArray();
        var stationId = channels[0].StationId;
        if (channels.Any(c => c.StationId != stationId))
            throw new InvalidDataException("One physical camera maps to multiple stations.");
        var draft = repository.GetDraft(solutionId, stationId)
            ?? throw new KeyNotFoundException("Product station draft missing.");
        if (draft.Revision != expectedStationRevision)
            throw new InvalidOperationException("DRAFT_REVISION_CONFLICT");
        var updated = (draft.Recipe.CaptureTargets ?? [])
            .Where(x => x.PhysicalDeviceId != target.PhysicalDeviceId)
            .Append(target.Canonical()).ToArray();
        return repository.SaveDraft(solutionId, draft.Recipe with
        {
            CaptureTargets = updated
        }, expectedStationRevision, actor);
    }

    /// <summary>Algorithm/parameters are keyed by logical ChannelId.</summary>
    public static StationDraft SetInspectionTarget(
        SolutionRepository repository, string solutionId,
        InspectionAlgorithmTarget target, long expectedStationRevision, string actor)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        var channel = ChannelCatalog.Require(target.ChannelId);
        var draft = repository.GetDraft(solutionId, channel.StationId)
            ?? throw new KeyNotFoundException("Product station draft missing.");
        if (draft.Revision != expectedStationRevision)
            throw new InvalidOperationException("DRAFT_REVISION_CONFLICT");
        var updated = (draft.Recipe.InspectionTargets ?? [])
            .Where(x => x.ChannelId != target.ChannelId)
            .Append(target.Canonical()).ToArray();
        return repository.SaveDraft(solutionId, draft.Recipe with
        {
            InspectionTargets = updated
        }, expectedStationRevision, actor);
    }
}
