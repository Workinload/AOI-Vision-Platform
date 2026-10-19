using AOI.Domain;

namespace AOI.Solutions;

/// <summary>Verified source-frame pixel size for one logical channel, decoded from a real PNG.</summary>
public sealed record RoiFrameSize(int Width, int Height)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new InvalidDataException("A verified source frame must have positive dimensions.");
    }
}

/// <summary>One fail-closed offline validation finding. Codes are stable for tests and operator text.</summary>
public sealed record OfflineRecipeIssue(string Code, string Message);

/// <summary>
/// Extra fail-closed validation for the OFFLINE operator product editor. It validates
/// against the SIM_ONLY capture adapter and inspection plugins that are actually
/// installed in this repository (sim.png.frame-source.v1, legacy.sim.brightness.v1,
/// opencv.contour) plus real source-frame dimensions for every ROI.
///
/// This deliberately does NOT replace SolutionRepository.Publish's own
/// manifest/ROI/digest validation. OfflineProductManager runs it BEFORE calling
/// Publish so an unusable plugin, device, parameter or ROI cannot create a release
/// row at all. Unknown plugin/device/adapter and unverified ROI image sizes fail closed.
/// </summary>
public static class OfflineRecipeValidator
{
    public const string InstalledCaptureAdapter = "sim.png.frame-source.v1";
    public const string LegacyBrightnessAlgorithm = "legacy.sim.brightness.v1";
    public const string OpenCvContourAlgorithm = "opencv.contour";
    private const double MaximumContourAreaPixels = 1_000_000_000;

    public static IReadOnlyList<string> InstalledInspectionAlgorithms { get; } =
        Array.AsReadOnly([LegacyBrightnessAlgorithm, OpenCvContourAlgorithm]);

    private static readonly HashSet<string> BrightnessKeys = new(StringComparer.Ordinal)
    {
        "brightThreshold", "minimumBrightFraction"
    };

    private static readonly HashSet<string> ContourKeys = new(StringComparer.Ordinal)
    {
        "binaryThreshold", "brightThreshold", "minimumContourAreaPixels",
        "minimumAreaPixels", "findBrightDefects", "maximumDetections"
    };

    /// <summary>
    /// Validates one station recipe. Structural failures short-circuit further checks.
    /// When previousRecipe is supplied (draft editing), an unchanged existing ROI keeps its
    /// earlier real-image association; a new/changed ROI still requires a verified frame.
    /// Publishing passes no previousRecipe, so every ROI must have a verified frame.
    /// </summary>
    public static IReadOnlyList<OfflineRecipeIssue> Inspect(StationRecipe recipe,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null,
        StationRecipe? previousRecipe = null)
    {
        var issues = new List<OfflineRecipeIssue>();
        if (recipe is null)
        {
            issues.Add(new("RECIPE_MISSING", "缺少工位配方。"));
            return issues;
        }
        try { recipe.Validate(); }
        catch (Exception ex) when (IsValidationError(ex))
        {
            issues.Add(new("RECIPE_INVALID", ex.Message));
            return issues;
        }
        try { _ = RoiPixelPreview.ReadRule(recipe); }
        catch (Exception ex) when (IsValidationError(ex))
        {
            issues.Add(new("BRIGHTNESS_RULE_INVALID", ex.Message));
        }
        InspectCaptureTargets(recipe, issues);
        InspectInspectionTargets(recipe, issues);
        InspectRois(recipe, verifiedFrames, previousRecipe, issues);
        return issues;
    }

    /// <summary>Throws a single aggregated InvalidDataException when any check fails.</summary>
    public static void RequireValid(StationRecipe recipe,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null,
        StationRecipe? previousRecipe = null)
    {
        var issues = Inspect(recipe, verifiedFrames, previousRecipe);
        if (issues.Count != 0) throw new InvalidDataException(Describe(issues));
    }

    /// <summary>
    /// Validates every mapped station draft of one product before any immutable release
    /// write. Missing drafts, unknown products and unverified ROI frames fail closed.
    /// </summary>
    public static IReadOnlyList<OfflineRecipeIssue> InspectSolution(SolutionRepository repository,
        string solutionId, IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var issues = new List<OfflineRecipeIssue>();
        if (string.IsNullOrWhiteSpace(solutionId))
        {
            issues.Add(new("SOLUTION_MISSING", "未指定产品方案。"));
            return issues;
        }
        if (!repository.ListSolutions().Any(x => string.Equals(x.SolutionId, solutionId, StringComparison.Ordinal)))
        {
            issues.Add(new("SOLUTION_UNKNOWN", "A6 数据库中不存在产品方案：" + solutionId));
            return issues;
        }
        foreach (string stationId in ChannelCatalog.All.Select(c => c.StationId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            StationDraft? draft;
            try { draft = repository.GetDraft(solutionId, stationId); }
            catch (Exception ex) when (IsValidationError(ex))
            {
                issues.Add(new("DRAFT_UNREADABLE", stationId + ": " + ex.Message));
                continue;
            }
            if (draft is null)
            {
                issues.Add(new("STATION_DRAFT_MISSING", "缺少工位草稿：" + stationId));
                continue;
            }
            foreach (var issue in Inspect(draft.Recipe, verifiedFrames))
                issues.Add(issue with { Message = stationId + ": " + issue.Message });
        }
        return issues;
    }

    public static string Describe(IEnumerable<OfflineRecipeIssue> issues) =>
        string.Join(" | ", issues.Select(x => x.Code + ": " + x.Message));

    private static void InspectCaptureTargets(StationRecipe recipe, List<OfflineRecipeIssue> issues)
    {
        if (recipe.CaptureTargets is not { } captures) return;
        var physical = ChannelCatalog.All.Where(c => c.StationId == recipe.StationId)
            .Select(c => c.PhysicalDeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            if (capture is null)
            {
                issues.Add(new("CAPTURE_TARGET_MISSING", "采集目标为空。"));
                continue;
            }
            if (!physical.Contains(capture.PhysicalDeviceId))
                issues.Add(new("CAPTURE_DEVICE_UNKNOWN", "采集设备不属于该工位：" + capture.PhysicalDeviceId));
            if (!string.Equals(capture.AdapterId, InstalledCaptureAdapter, StringComparison.Ordinal))
                issues.Add(new("CAPTURE_ADAPTER_NOT_INSTALLED",
                    "未安装的采集适配器：" + capture.AdapterId + "（仅允许 " + InstalledCaptureAdapter + "）"));
        }
    }

    private static void InspectInspectionTargets(StationRecipe recipe, List<OfflineRecipeIssue> issues)
    {
        if (recipe.InspectionTargets is not { } targets) return;
        foreach (var target in targets)
        {
            if (target is null)
            {
                issues.Add(new("INSPECTION_TARGET_MISSING", "检测插件目标为空。"));
                continue;
            }
            string stationId;
            try { stationId = ChannelCatalog.Require(target.ChannelId).StationId; }
            catch (ArgumentException ex)
            {
                issues.Add(new("INSPECTION_CHANNEL_UNKNOWN", ex.Message));
                continue;
            }
            if (!string.Equals(stationId, recipe.StationId, StringComparison.Ordinal))
            {
                issues.Add(new("INSPECTION_CROSS_STATION", "检测插件跨工位/通道绑定：" + target.ChannelId));
                continue;
            }
            bool legacy = string.Equals(target.AlgorithmId, LegacyBrightnessAlgorithm, StringComparison.Ordinal);
            bool contour = string.Equals(target.AlgorithmId, OpenCvContourAlgorithm, StringComparison.Ordinal);
            if (!legacy && !contour)
            {
                issues.Add(new("INSPECTION_PLUGIN_NOT_INSTALLED",
                    "未安装的检测插件：" + target.AlgorithmId + "（已安装：" +
                    string.Join(" / ", InstalledInspectionAlgorithms) + "）"));
                continue;
            }
            var allowed = legacy ? BrightnessKeys : ContourKeys;
            foreach (string key in target.NumericParameters.Keys.Order(StringComparer.Ordinal))
                if (!allowed.Contains(key))
                    issues.Add(new("INSPECTION_PARAMETER_UNKNOWN",
                        target.AlgorithmId + " 不支持参数 " + key));
            if (target.TextParameters.Count != 0)
                issues.Add(new("INSPECTION_TEXT_PARAMETER_UNSUPPORTED",
                    target.AlgorithmId + " 不接受文本参数。"));
            if (legacy) ValidateBrightnessNumbers(target.NumericParameters, target.ChannelId, issues);
            else ValidateContourNumbers(target.NumericParameters, target.ChannelId, issues);
        }
    }

    private static void ValidateBrightnessNumbers(IReadOnlyDictionary<string, double> numbers,
        string channelId, List<OfflineRecipeIssue> issues)
    {
        if (numbers.TryGetValue("brightThreshold", out double threshold) && !IsThreshold(threshold))
            issues.Add(new("BRIGHTNESS_THRESHOLD_RANGE",
                channelId + " brightThreshold 必须是 1..254 的整数。"));
        if (numbers.TryGetValue("minimumBrightFraction", out double fraction) &&
            (!double.IsFinite(fraction) || fraction is < 0 or > 1))
            issues.Add(new("BRIGHTNESS_FRACTION_RANGE",
                channelId + " minimumBrightFraction 必须在 0..1 之间。"));
    }

    private static void ValidateContourNumbers(IReadOnlyDictionary<string, double> numbers,
        string channelId, List<OfflineRecipeIssue> issues)
    {
        if (Consistent(numbers, "binaryThreshold", "brightThreshold", "CONTOUR_THRESHOLD_CONFLICT",
                channelId, issues) is { } threshold && !IsThreshold(threshold))
            issues.Add(new("CONTOUR_THRESHOLD_RANGE", channelId + " binaryThreshold 必须是 1..254 的整数。"));
        if (Consistent(numbers, "minimumContourAreaPixels", "minimumAreaPixels", "CONTOUR_AREA_CONFLICT",
                channelId, issues) is { } area &&
            (!double.IsFinite(area) || area <= 0 || area > MaximumContourAreaPixels))
            issues.Add(new("CONTOUR_AREA_RANGE", channelId + " 最小轮廓面积必须位于 (0, 1e9]。"));
        if (numbers.TryGetValue("findBrightDefects", out double polarity) &&
            (!double.IsFinite(polarity) || polarity is not (0 or 1)))
            issues.Add(new("CONTOUR_POLARITY_RANGE", channelId + " findBrightDefects 只能是 0 或 1。"));
        if (numbers.TryGetValue("maximumDetections", out double detections) &&
            (!double.IsFinite(detections) || detections != Math.Truncate(detections) ||
             detections is < 1 or > 2048))
            issues.Add(new("CONTOUR_MAXIMUM_DETECTIONS_RANGE", channelId + " maximumDetections 必须是 1..2048 的整数。"));
    }

    private static double? Consistent(IReadOnlyDictionary<string, double> numbers,
        string primary, string alternate, string conflictCode, string channelId,
        List<OfflineRecipeIssue> issues)
    {
        double? value = null;
        if (numbers.TryGetValue(primary, out double first)) value = first;
        if (numbers.TryGetValue(alternate, out double second))
        {
            if (value is { } existing && existing != second)
                issues.Add(new(conflictCode, channelId + " " + primary + " 与 " + alternate + " 相互冲突。"));
            value ??= second;
        }
        return value;
    }

    private static void InspectRois(StationRecipe recipe,
        IReadOnlyDictionary<string, RoiFrameSize>? verifiedFrames, StationRecipe? previousRecipe,
        List<OfflineRecipeIssue> issues)
    {
        try { RoiReleaseWorkflow.ValidateStationRois(recipe); }
        catch (Exception ex) when (IsValidationError(ex))
        {
            issues.Add(new("ROI_INVALID", ex.Message));
            return;
        }
        foreach (string channelId in recipe.ChannelIds)
        {
            PublishedChannelRoi? roi;
            try { roi = RoiReleaseWorkflow.ReadStationRoi(recipe, channelId); }
            catch (Exception ex) when (IsValidationError(ex))
            {
                issues.Add(new("ROI_INVALID", channelId + ": " + ex.Message));
                continue;
            }
            if (roi is null) continue;
            bool unchanged = RoiUnchanged(previousRecipe, recipe, channelId);
            if (verifiedFrames is null || !verifiedFrames.TryGetValue(channelId, out RoiFrameSize? frame) ||
                frame is null)
            {
                if (!unchanged)
                    issues.Add(new("ROI_IMAGE_SIZE_UNVERIFIED",
                        "ROI " + channelId + " 没有关联已验证的实际图像尺寸；拒绝保存/发布。"));
                continue;
            }
            try
            {
                frame.Validate();
                _ = roi.Bounds.ToPixels(frame.Width, frame.Height);
            }
            catch (Exception ex) when (IsValidationError(ex))
            {
                issues.Add(new("ROI_OUT_OF_FRAME", channelId + ": " + ex.Message));
            }
        }
    }

    private static bool RoiUnchanged(StationRecipe? previousRecipe, StationRecipe current, string channelId)
    {
        if (previousRecipe is null) return false;
        try
        {
            var before = RoiReleaseWorkflow.ReadStationRoi(previousRecipe, channelId);
            var after = RoiReleaseWorkflow.ReadStationRoi(current, channelId);
            return before is not null && after is not null && before.Bounds == after.Bounds;
        }
        catch (Exception ex) when (IsValidationError(ex))
        {
            return false; // an unreadable previous draft cannot vouch for a frame association
        }
    }

    private static bool IsThreshold(double value) =>
        double.IsFinite(value) && value == Math.Truncate(value) && value is >= 1 and <= 254;

    private static bool IsValidationError(Exception ex) =>
        ex is InvalidDataException or ArgumentException or InvalidOperationException
            or NotSupportedException or KeyNotFoundException or OverflowException;
}
