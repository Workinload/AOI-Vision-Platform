using AOI.Domain;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace AOI.Solutions;

/// <summary>Describes the intended capture source, NOT a live hardware connection or PLC command.</summary>
public enum CaptureTriggerMode { ExternalHardwareEdge, ExternalHardwareLevel, SoftwareCommand }

/// <summary>Immutable target for one physical device (line-scan twins share it).</summary>
public sealed record CameraCaptureTarget(
    string PhysicalDeviceId, string AdapterId, CaptureTriggerMode TriggerMode,
    string? TriggerInput, int? FrameTimeoutMs, double? ExposureMicroseconds,
    double? GainDb, IReadOnlyDictionary<string, double> DeviceParameters)
{
    public void Validate()
    {
        if (!ChannelCatalog.All.Any(c => c.PhysicalDeviceId == PhysicalDeviceId))
            throw new InvalidDataException("Unknown physical camera: " + PhysicalDeviceId);
        RecipeParameterRules.RequireKey(AdapterId, "capture adapter");
        if (!Enum.IsDefined(TriggerMode) ||
            (TriggerInput is not null &&
                (TriggerInput.Length == 0 || TriggerInput.Length > 64 ||
                 !Regex.IsMatch(TriggerInput, @"^[A-Za-z0-9_.:/-]+$"))) ||
            FrameTimeoutMs is < 1 or > 120000 ||
            (ExposureMicroseconds is { } exposure && (!double.IsFinite(exposure) || exposure <= 0)) ||
            (GainDb is { } gain && !double.IsFinite(gain)))
            throw new InvalidDataException("Invalid intended trigger or capture target.");
        RecipeParameterRules.ValidateNumbers(DeviceParameters, "camera target");
    }

    internal CameraCaptureTarget Canonical() => this with
    {
        DeviceParameters = RecipeParameterRules.SortedNumbers(DeviceParameters)
    };
}

/// <summary>Algorithm is a plugin selection, not a claim that the plugin is installed or accurate.</summary>
public sealed record InspectionAlgorithmTarget(
    string ChannelId, string AlgorithmId,
    IReadOnlyDictionary<string, double> NumericParameters,
    IReadOnlyDictionary<string, string> TextParameters)
{
    public void Validate()
    {
        _ = ChannelCatalog.Require(ChannelId);
        RecipeParameterRules.RequireKey(AlgorithmId, "algorithm plugin");
        RecipeParameterRules.ValidateNumbers(NumericParameters, "algorithm");
        if (TextParameters is null || TextParameters.Count > 64)
            throw new InvalidDataException("Invalid algorithm text parameter dictionary.");
        foreach (var (key, value) in TextParameters)
        {
            RecipeParameterRules.RequireKey(key, "algorithm text parameter");
            if (value is null || value.Length > 512 ||
                value.Any(c => char.IsControl(c) && c is not ('\t' or '\n')))
                throw new InvalidDataException("Invalid algorithm parameter value.");
        }
    }

    internal InspectionAlgorithmTarget Canonical() => this with
    {
        NumericParameters = RecipeParameterRules.SortedNumbers(NumericParameters),
        TextParameters = new SortedDictionary<string, string>(TextParameters, StringComparer.Ordinal)
    };
}

internal static class RecipeParameterRules
{
    internal static void RequireKey(string? key, string label)
    {
        if (key is null || !Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9_.-]{0,95}$"))
            throw new InvalidDataException("Invalid " + label + " identifier.");
    }

    internal static void ValidateNumbers(IReadOnlyDictionary<string, double>? source, string label)
    {
        if (source is null || source.Count > 64) throw new InvalidDataException("Invalid " + label + " parameters.");
        foreach (var (key, value) in source)
        {
            RequireKey(key, label + " parameter");
            if (!double.IsFinite(value))
                throw new InvalidDataException("Nonfinite " + label + " parameter.");
        }
    }

    internal static IReadOnlyDictionary<string, double> SortedNumbers(
        IReadOnlyDictionary<string, double> source) =>
        new SortedDictionary<string, double>(source, StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, double> FreezeNumbers(
        IReadOnlyDictionary<string, double> source) =>
        new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(source, StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, string> FreezeText(
        IReadOnlyDictionary<string, string> source) =>
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(source, StringComparer.Ordinal));

    internal static void ValidateStation(StationRecipe station)
    {
        if (station.CaptureTargets is { } captures)
        {
            var physical = ChannelCatalog.All
                .Where(x => x.StationId == station.StationId)
                .Select(x => x.PhysicalDeviceId).ToHashSet(StringComparer.Ordinal);
            if (captures.Count > physical.Count ||
                captures.Select(c => c.PhysicalDeviceId).Distinct(StringComparer.Ordinal).Count() != captures.Count)
                throw new InvalidDataException("Duplicate physical capture settings.");
            foreach (var capture in captures)
            {
                capture.Validate();
                if (!physical.Contains(capture.PhysicalDeviceId))
                    throw new InvalidDataException("Capture target crosses a station boundary.");
            }
        }
        if (station.InspectionTargets is { } inspectors)
        {
            if (inspectors.Count > station.ChannelIds.Count ||
                inspectors.Select(x => x.ChannelId).Distinct(StringComparer.Ordinal).Count() != inspectors.Count)
                throw new InvalidDataException("Duplicate channel inspection settings.");
            foreach (var inspector in inspectors)
            {
                inspector.Validate();
                if (!station.ChannelIds.Contains(inspector.ChannelId, StringComparer.Ordinal))
                    throw new InvalidDataException("Inspection algorithm crosses a logical channel boundary.");
            }
        }
    }
}

/// <summary>Adapter-supplied readback. Never store observed hardware state in immutable recipes.</summary>
public sealed record CameraConfigurationReadback(
    string PhysicalDeviceId, string AdapterId, CaptureTriggerMode TriggerMode,
    string? TriggerInput, int? FrameTimeoutMs, double? ExposureMicroseconds,
    double? GainDb, IReadOnlyDictionary<string, double> DeviceParameters,
    DateTimeOffset ObservedUtc, bool IsSimulation);

public enum CameraConfigurationState { TargetUnspecified, ReadbackPending, MatchObserved, MismatchObserved }

public sealed record CameraConfigurationView(
    CameraCaptureTarget? Target, CameraConfigurationReadback? Readback,
    CameraConfigurationState State)
{
    /// <summary>Observed equality is not field-verified authorization to operate a real device.</summary>
    public static CameraConfigurationView Compare(
        CameraCaptureTarget? target, CameraConfigurationReadback? observed)
    {
        if (target is null) return new(null, observed, CameraConfigurationState.TargetUnspecified);
        if (observed is null) return new(target, null, CameraConfigurationState.ReadbackPending);
        if (observed.ObservedUtc == default || observed.DeviceParameters is null)
            throw new InvalidDataException("Invalid camera adapter readback.");
        RecipeParameterRules.ValidateNumbers(observed.DeviceParameters, "readback");
        bool equal =
            target.PhysicalDeviceId == observed.PhysicalDeviceId &&
            target.AdapterId == observed.AdapterId &&
            target.TriggerMode == observed.TriggerMode &&
            target.TriggerInput == observed.TriggerInput &&
            (!target.FrameTimeoutMs.HasValue || target.FrameTimeoutMs == observed.FrameTimeoutMs) &&
            (!target.ExposureMicroseconds.HasValue || target.ExposureMicroseconds == observed.ExposureMicroseconds) &&
            (!target.GainDb.HasValue || target.GainDb == observed.GainDb) &&
            target.DeviceParameters.All(p => observed.DeviceParameters.TryGetValue(p.Key, out var value) && value == p.Value);
        return new(target, observed, equal
            ? CameraConfigurationState.MatchObserved : CameraConfigurationState.MismatchObserved);
    }
}

/// <summary>
/// P0 configuration injection DTO: no SDK objects and no frame ownership.
/// A3 FrameSource consumes Camera.Target; Inspector consumes Algorithm and ROI.
/// </summary>
public sealed record ChannelExecutionParameters(
    string SolutionId, string ReleaseId, string ReleaseSha256, long Epoch,
    string ChannelId, string StationId, string PhysicalDeviceId,
    MachineRole OwnerRole, string PlcSignalAddress, string? SharedFrameGroup,
    CameraConfigurationView Camera, InspectionAlgorithmTarget Algorithm,
    NormalizedInspectionRoi InspectionRoi);

/// <summary>
/// A release is pinned to a workpiece when it enters Core. Existing in-flight
/// items retain their bound snapshot after A0 switches the next-batch release.
/// No lookup of a mutable GUI draft is performed during capture or inspection.
/// </summary>
public sealed class PublishedParameterSnapshot
{
    private readonly IReadOnlyDictionary<string, ChannelExecutionParameters> _channels;
    public string SolutionId { get; }
    public string ReleaseId { get; }
    public string ContentSha256 { get; }
    public long Epoch { get; }

    public PublishedParameterSnapshot(PublishedRelease published, long epoch)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (epoch < 1) throw new ArgumentOutOfRangeException(nameof(epoch));
        published.Verify();
        var stations = published.Manifest.Stations.ToDictionary(x => x.StationId, StringComparer.Ordinal);
        var entries = new Dictionary<string, ChannelExecutionParameters>(StringComparer.Ordinal);
        foreach (var channel in ChannelCatalog.All)
        {
            var station = stations[channel.StationId];
            var capture = station.CaptureTargets?.SingleOrDefault(
                x => x.PhysicalDeviceId == channel.PhysicalDeviceId)?.Canonical();
            var configured = station.InspectionTargets?.SingleOrDefault(
                x => x.ChannelId == channel.ChannelId)?.Canonical();
            // Legacy Release compatibility: only historical SIM brightness, never
            // silently interpret an unregistered custom algorithm as "GOOD".
            var inspection = configured ?? new InspectionAlgorithmTarget(channel.ChannelId,
                "legacy.sim.brightness.v1",
                new SortedDictionary<string, double>(new Dictionary<string, double>
                {
                    ["brightThreshold"] = station.Tunables.TryGetValue("brightThreshold", out var t) ? t : 127,
                    ["minimumBrightFraction"] = station.Tunables.TryGetValue("minimumBrightFraction", out var f) ? f : 0.1
                }, StringComparer.Ordinal),
                new SortedDictionary<string, string>(StringComparer.Ordinal));
            inspection.Validate();
            var frozenAlgorithm = inspection with
            {
                NumericParameters = RecipeParameterRules.FreezeNumbers(inspection.NumericParameters),
                TextParameters = RecipeParameterRules.FreezeText(inspection.TextParameters)
            };
            var frozenCapture = capture is null ? null : capture with
            {
                DeviceParameters = RecipeParameterRules.FreezeNumbers(capture.DeviceParameters)
            };
            var roi = RoiReleaseWorkflow.ReadStationRoi(station, channel.ChannelId)?.Bounds
                ?? NormalizedInspectionRoi.FullFrame;
            entries.Add(channel.ChannelId, new ChannelExecutionParameters(
                published.Manifest.SolutionId, published.ReleaseId, published.ContentSha256, epoch,
                channel.ChannelId, channel.StationId, channel.PhysicalDeviceId,
                channel.OwnerRole, channel.PlcSignalAddress, channel.SharedFrameGroup,
                CameraConfigurationView.Compare(frozenCapture, null), frozenAlgorithm, roi));
        }
        _channels = new ReadOnlyDictionary<string, ChannelExecutionParameters>(entries);
        SolutionId = published.Manifest.SolutionId;
        ReleaseId = published.ReleaseId;
        ContentSha256 = published.ContentSha256;
        Epoch = epoch;
    }

    public WorkpieceParameterBinding Bind(WorkpieceIdentity item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        if (item.Epoch != Epoch || item.ReleaseId != ReleaseId)
            throw new InvalidOperationException("WORKPIECE_RELEASE_EPOCH_MISMATCH");
        return new WorkpieceParameterBinding(this, item);
    }

    internal ChannelExecutionParameters RequireChannel(string channelId) =>
        _channels.TryGetValue(channelId, out var channel) ? channel
            : throw new ArgumentException("Unknown channel: " + channelId, nameof(channelId));
}

public sealed class WorkpieceParameterBinding
{
    private readonly PublishedParameterSnapshot _snapshot;
    public WorkpieceIdentity Workpiece { get; }

    internal WorkpieceParameterBinding(PublishedParameterSnapshot snapshot, WorkpieceIdentity workpiece)
    {
        _snapshot = snapshot;
        Workpiece = workpiece;
    }

    public ChannelExecutionParameters ForChannel(string channelId, string physicalDeviceId,
        CameraConfigurationReadback? readback = null)
    {
        var values = _snapshot.RequireChannel(channelId);
        if (values.PhysicalDeviceId != physicalDeviceId)
            throw new InvalidOperationException("CAPTURE_DEVICE_CHANNEL_MISMATCH");
        return values with { Camera = CameraConfigurationView.Compare(values.Camera.Target, readback) };
    }
}

/// <summary>
/// Plugin router, intentionally no hardcoded image algorithm and no fallback
/// GOOD verdict: a missing AlgorithmId throws and must become an ERROR result in A3.
/// A3 owns actual frame source, image processor, and ChannelResult persistence.
/// </summary>
public interface IRecipeInspectionProcessor<in TFrame, out TResult>
{
    string AlgorithmId { get; }
    TResult Inspect(TFrame frame, ChannelExecutionParameters parameters);
}

public sealed class RecipeInspectionDispatcher<TFrame, TResult>
{
    private readonly IReadOnlyDictionary<string, IRecipeInspectionProcessor<TFrame, TResult>> _processors;
    public RecipeInspectionDispatcher(IEnumerable<IRecipeInspectionProcessor<TFrame, TResult>> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        var found = new Dictionary<string, IRecipeInspectionProcessor<TFrame, TResult>>(StringComparer.Ordinal);
        foreach (var processor in processors)
        {
            ArgumentNullException.ThrowIfNull(processor);
            RecipeParameterRules.RequireKey(processor.AlgorithmId, "registered inspection processor");
            if (!found.TryAdd(processor.AlgorithmId, processor))
                throw new ArgumentException("Duplicate inspection processor: " + processor.AlgorithmId);
        }
        _processors = new ReadOnlyDictionary<string, IRecipeInspectionProcessor<TFrame, TResult>>(found);
    }

    public TResult Inspect(TFrame frame, WorkpieceParameterBinding workpiece,
        string channelId, string physicalDeviceId,
        CameraConfigurationReadback? readback = null)
    {
        ArgumentNullException.ThrowIfNull(workpiece);
        var config = workpiece.ForChannel(channelId, physicalDeviceId, readback);
        if (!_processors.TryGetValue(config.Algorithm.AlgorithmId, out var processor))
            throw new NotSupportedException("INSPECTION_PROCESSOR_NOT_READY: " + config.Algorithm.AlgorithmId);
        return processor.Inspect(frame, config);
    }
}
