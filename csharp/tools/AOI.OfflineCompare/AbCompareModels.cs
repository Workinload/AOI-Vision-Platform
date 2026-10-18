using AOI.Runtime;
using AOI.Runtime.Vision;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AOI.OfflineCompare;

/// <summary>
/// OFFLINE_ONLY channel status. `Positioning` marks CH01: a positioning
/// prerequisite is never reported as an inspection OK/NG verdict.
/// </summary>
public enum AbChannelStatus { Ok, Ng, Error, Missing, Positioning, Unsupported }

public sealed record AbDefect(int X, int Y, int Width, int Height, double AreaPixels);

/// <summary>One release's real result for one logical channel on one immutable input.</summary>
public sealed record AbChannelSide(
    AbChannelStatus Status,
    string Reason,
    string AlgorithmId,
    byte? BinaryThreshold,
    double? MinimumContourAreaPixels,
    bool? FindBrightDefects,
    int? MaximumDetections,
    double? MinimumBrightFraction,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight,
    double ElapsedMilliseconds,
    IReadOnlyList<AbDefect> Defects,
    string? AnnotationRelativePath)
{
    public static AbChannelSide Unavailable(AbChannelStatus status, string reason) =>
        new(status, reason, "UNAVAILABLE", null, null, null, null, null, 0, 0, 0, 0, 0,
            Array.Empty<AbDefect>(), null);
}

public sealed record AbChannelResult(
    string ChannelId,
    string StationId,
    string PhysicalDeviceId,
    string OwnerRole,
    bool IsPositioning,
    string? SharedFrameGroup,
    string InputFile,
    string? InputSha256,
    string? ParentInputSha256,
    int InputWidth,
    int InputHeight,
    int PixelsWidth,
    int PixelsHeight,
    AbChannelSide A,
    AbChannelSide B,
    bool Changed,
    string Difference);

public sealed record AbPhysicalDevice(
    string PhysicalDeviceId,
    string DeviceType,
    IReadOnlyList<string> ChannelIds,
    string InputFile,
    string? InputSha256,
    int Width,
    int Height,
    AbChannelStatus DecodeStatus,
    string? DecodeReason,
    IReadOnlyList<AbChannelResult> Channels);

public sealed record AbReleaseInfo(
    string ReleaseId,
    string RecordedContentSha256,
    string RecomputedContentSha256,
    bool DigestVerified,
    string SolutionId,
    string ProfileId,
    int ProfileVersion,
    DateTimeOffset PublishedAtUtc,
    string Actor,
    int StationCount,
    int ChannelCount);

public sealed record AbCompareManifest(
    string Tool,
    string Mode,
    string GeneratedUtc,
    string Host,
    string OpenCvVersion,
    string DotNetVersion,
    string DatabasePath,
    string DatabaseSha256Before,
    string DatabaseSha256After,
    string DatabaseSnapshotSha256,
    bool DatabaseUnchanged,
    string ImageRoot,
    string OutputRoot,
    string LineRowMapping,
    long Epoch,
    AbReleaseInfo ReleaseA,
    AbReleaseInfo ReleaseB,
    IReadOnlyList<AbPhysicalDevice> Devices,
    IReadOnlyList<string> ChangedChannels,
    IReadOnlyList<string> UnchangedChannels,
    IReadOnlyList<AbChannelResult> Channels,
    bool InputImagesUnchanged,
    bool AbsolutePathsIncluded,
    IReadOnlyList<string> NotVerified);

/// <summary>Explicit offline request. Nothing here can reach a PLC, camera or production WAL.</summary>
public sealed record AbCompareRequest(
    string DatabasePath,
    string ImageRoot,
    string OutputRoot,
    string ReleaseIdA,
    string ReleaseIdB,
    long Epoch = 1,
    LineScanRowMapping LineMapping = LineScanRowMapping.DemoEvenOdd,
    bool IncludeAbsolutePaths = false)
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
