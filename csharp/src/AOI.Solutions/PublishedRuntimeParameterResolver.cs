using AOI.Domain;

namespace AOI.Solutions;

/// <summary>
/// The only built-in inspection implementations recognized by the current SIM_ONLY
/// pipeline. This is a typed recipe selection, never an inspection verdict.
/// </summary>
public enum PublishedInspectionKind
{
    LegacyBrightness,
    OpenCvContour
}

/// <summary>
/// One configuration for one real physical capture event. On a line-scan parent
/// ChannelIds contains both logical results, but there is still ONE camera timeout.
/// </summary>
public sealed record ResolvedPhysicalCaptureSettings(
    Guid WorkpieceId, string ReleaseId, long Epoch,
    string PhysicalDeviceId, string StationId, MachineRole OwnerRole,
    string PlcSignalAddress, IReadOnlyList<string> ChannelIds,
    string? AdapterId, CaptureTriggerMode TriggerMode, string? TriggerInput,
    TimeSpan FrameDeadline, CameraConfigurationView Camera);

/// <summary>
/// Immutable, WorkpieceId/Release/Epoch-bound parameters A0 supplies to an A3
/// inspection implementation AFTER frame validation and PLC CamDone confirmation.
/// Null algorithm-specific properties must not be interpreted as zero/GOOD.
/// </summary>
public sealed record ResolvedChannelInspectionSettings(
    Guid WorkpieceId, string ReleaseId, long Epoch,
    string PhysicalDeviceId, string ChannelId,
    PublishedInspectionKind Kind, string AlgorithmId,
    byte BinaryThreshold, double? MinimumBrightFraction,
    double? MinimumContourAreaPixels, bool? FindBrightDefects,
    int? MaximumDetections, NormalizedInspectionRoi Roi,
    IReadOnlyDictionary<string, string> TextParameters)
{
    public PixelInspectionRoi PixelRoi(int sourceWidth, int sourceHeight) =>
        Roi.ToPixels(sourceWidth, sourceHeight);
}

/// <summary>
/// A complete one-physical-trigger plan. Area devices have one inspection channel;
/// both line-scan parents have two independently resolved child-channel settings.
/// </summary>
public sealed record ResolvedPhysicalInspectionPlan(
    ResolvedPhysicalCaptureSettings Capture,
    IReadOnlyList<ResolvedChannelInspectionSettings> Inspections);

/// <summary>
/// Converts existing A6 immutable releases into the runtime values A0 Host/A3 need.
/// This library deliberately does not reference AOI.Runtime, camera SDKs, FINS or
/// an actual device. It never sends a software/hardware trigger.
/// </summary>
public static class PublishedRuntimeParameterResolver
{
    public const string LegacyBrightnessId = "legacy.sim.brightness.v1";
    public const string OpenCvContourId = "opencv.contour";

    // SIM_ONLY historical reference deadlines; NOT field-verified OEM settings.
    public static readonly TimeSpan DefaultAreaFrameDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultLineFrameDeadline = TimeSpan.FromSeconds(20);

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
    /// Core should construct PublishedParameterSnapshot ONCE after verifying the
    /// dual-node active Release, Bind(workpiece) when it is registered, then call
    /// Resolve with that saved binding at each actual physical trigger.
    /// </summary>
    public static ResolvedPhysicalInspectionPlan Resolve(
        WorkpieceParameterBinding bound, string physicalDeviceId,
        CameraConfigurationReadback? adapterReadback = null)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var physical = bound.ForPhysicalDevice(physicalDeviceId, adapterReadback);
        if (physical.ChannelIds.Count is < 1 or > 2)
            throw new InvalidDataException("Unsupported physical capture channel count.");
        if (physical.ChannelIds.Count == 2 &&
            physical.ChannelIds.Any(id => ChannelCatalog.Require(id).SharedFrameGroup != physicalDeviceId))
            throw new InvalidDataException("Line-scan pair must share its physical parent.");

        var target = physical.Camera.Target;
        // This specific resolver serves A0's EXTERNAL PLC-driven flow. A software
        // camera trigger cannot be inferred from a D-register request.
        if (target?.TriggerMode == CaptureTriggerMode.SoftwareCommand)
            throw new NotSupportedException("SOFTWARE_TRIGGER_NOT_SUPPORTED_IN_EXTERNAL_PLC_FLOW");

        TimeSpan deadline = target?.FrameTimeoutMs is { } timeoutMs
            ? TimeSpan.FromMilliseconds(timeoutMs)
            : physical.ChannelIds.Count == 2 ? DefaultLineFrameDeadline : DefaultAreaFrameDeadline;
        if (deadline <= TimeSpan.Zero)
            throw new InvalidDataException("No positive published frame deadline.");

        var workpiece = bound.Workpiece;
        var capture = new ResolvedPhysicalCaptureSettings(
            workpiece.WorkpieceId, workpiece.ReleaseId, workpiece.Epoch,
            physical.PhysicalDeviceId, physical.StationId, physical.OwnerRole,
            physical.PlcSignalAddress,
            Array.AsReadOnly(physical.ChannelIds.ToArray()),
            target?.AdapterId, target?.TriggerMode ?? CaptureTriggerMode.ExternalHardwareEdge,
            target?.TriggerInput, deadline, physical.Camera);

        var children = physical.ChannelIds.Select(channelId =>
            ResolveChannel(bound.ForChannel(channelId, physicalDeviceId, adapterReadback),
                workpiece.WorkpieceId)).ToArray();
        return new(capture, Array.AsReadOnly(children));
    }

    /// <summary>
    /// A0 can use this before resolving a physical batch when an individual
    /// capture/inspection result is needed; identity comes from the A6 binding.
    /// </summary>
    public static ResolvedChannelInspectionSettings ResolveChannel(
        WorkpieceParameterBinding bound, string channelId, string physicalDeviceId)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var settings = bound.ForChannel(channelId, physicalDeviceId);
        return ResolveChannel(settings, bound.Workpiece.WorkpieceId);
    }

    private static ResolvedChannelInspectionSettings ResolveChannel(
        ChannelExecutionParameters settings, Guid workpieceId)
    {
        var id = settings.Algorithm.AlgorithmId;
        var numbers = settings.Algorithm.NumericParameters;
        if (id == LegacyBrightnessId)
        {
            EnsureKnown(numbers, BrightnessKeys, id);
            byte threshold = ByteThreshold(numbers, "brightThreshold", settings.StationBrightThreshold);
            double fraction = Value(numbers, "minimumBrightFraction", settings.StationMinimumBrightFraction);
            if (!double.IsFinite(fraction) || fraction is < 0 or > 1)
                throw new InvalidDataException("INVALID_PUBLISHED_BRIGHT_FRACTION");
            return new(workpieceId, settings.ReleaseId, settings.Epoch,
                settings.PhysicalDeviceId, settings.ChannelId,
                PublishedInspectionKind.LegacyBrightness, id,
                threshold, fraction, null, null, null, settings.InspectionRoi,
                settings.Algorithm.TextParameters);
        }
        if (id == OpenCvContourId)
        {
            EnsureKnown(numbers, ContourKeys, id);
            if (numbers.TryGetValue("binaryThreshold", out double direct) &&
                numbers.TryGetValue("brightThreshold", out double alternate) &&
                direct != alternate)
                throw new InvalidDataException("CONFLICTING_PUBLISHED_CONTOUR_THRESHOLD");
            if (numbers.TryGetValue("minimumContourAreaPixels", out double area) &&
                numbers.TryGetValue("minimumAreaPixels", out double otherArea) &&
                area != otherArea)
                throw new InvalidDataException("CONFLICTING_PUBLISHED_CONTOUR_AREA");
            double threshold = numbers.TryGetValue("binaryThreshold", out direct)
                ? direct : Value(numbers, "brightThreshold", settings.StationBrightThreshold);
            byte binaryThreshold = ByteThreshold(threshold);
            double minArea = numbers.TryGetValue("minimumContourAreaPixels", out area)
                ? area : Value(numbers, "minimumAreaPixels", 8);
            if (!double.IsFinite(minArea) || minArea <= 0 || minArea > 1_000_000_000)
                throw new InvalidDataException("INVALID_PUBLISHED_CONTOUR_AREA");
            double bright = Value(numbers, "findBrightDefects", 1);
            if (bright is not (0 or 1))
                throw new InvalidDataException("INVALID_PUBLISHED_CONTOUR_POLARITY");
            double detections = Value(numbers, "maximumDetections", 128);
            if (!double.IsFinite(detections) || detections != Math.Truncate(detections) ||
                detections is < 1 or > 2048)
                throw new InvalidDataException("INVALID_PUBLISHED_MAXIMUM_DETECTIONS");
            return new(workpieceId, settings.ReleaseId, settings.Epoch,
                settings.PhysicalDeviceId, settings.ChannelId,
                PublishedInspectionKind.OpenCvContour, id,
                binaryThreshold, null, minArea, bright == 1, (int)detections,
                settings.InspectionRoi, settings.Algorithm.TextParameters);
        }
        throw new NotSupportedException("INSPECTION_PROCESSOR_NOT_READY: " + id);
    }

    private static double Value(IReadOnlyDictionary<string, double> source,
        string name, double fallback) =>
        source.TryGetValue(name, out double value) ? value : fallback;

    private static byte ByteThreshold(
        IReadOnlyDictionary<string, double> source, string name, int fallback) =>
        ByteThreshold(Value(source, name, fallback));

    private static byte ByteThreshold(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 1 or > 254)
            throw new InvalidDataException("INVALID_PUBLISHED_BINARY_THRESHOLD");
        return checked((byte)value);
    }

    private static void EnsureKnown(IReadOnlyDictionary<string, double> source,
        HashSet<string> allowed, string pluginId)
    {
        foreach (var (key, value) in source)
            if (!allowed.Contains(key) || !double.IsFinite(value))
                throw new InvalidDataException(
                    "UNSUPPORTED_PUBLISHED_ALGORITHM_PARAMETER: " + pluginId + "/" + key);
    }
}
