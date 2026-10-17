using AOI.Contracts;
using AOI.Domain;

namespace AOI.Ui.Client;

/// <summary>
/// Operator map coordinates are UI layout hints, not calibrated machine geometry.
/// Camera ownership, station and DM addresses always come from A0's ChannelCatalog.
/// </summary>
public sealed record OperatorStation(
    string StationId, string DisplayName, int MapX, int MapY,
    IReadOnlyList<string> ChannelIds, IReadOnlyList<string> DeviceIds);

public static class OperatorWorkspaceTopology
{
    // Map order follows the known station path; coordinates are only presentation.
    private static readonly (string Id, string Name, int X, int Y)[] Layout =
    [
        ("LOADING", "上料 / 定位", 50, 145),
        ("ST03", "检测工位 03", 260, 70),
        ("ST04", "检测工位 04", 475, 70),
        ("ST05", "检测工位 05", 690, 70),
        ("ST06", "检测工位 06", 690, 245),
        ("ST09", "线扫工位 09", 475, 245),
        ("ST10", "线扫工位 10", 260, 245)
    ];

    public static IReadOnlyList<OperatorStation> Stations { get; } = Layout.Select(x =>
    {
        var channels = ChannelCatalog.All.Where(c => c.StationId == x.Id).ToArray();
        if (channels.Length == 0)
            throw new InvalidOperationException("Unknown layout station: " + x.Id);
        return new OperatorStation(x.Id, x.Name, x.X, x.Y,
            channels.Select(c => c.ChannelId).ToArray(),
            channels.Select(c => c.PhysicalDeviceId).Distinct(StringComparer.Ordinal).ToArray());
    }).ToArray();

    public static OperatorStation Require(string stationId) =>
        Stations.FirstOrDefault(x => x.StationId == stationId)
        ?? throw new ArgumentException("Unknown map station: " + stationId, nameof(stationId));

    /// <summary>
    /// Exact channel DM mapping; shared line-scan DM addresses can match two channels.
    /// Non-camera PLC signals are localized only by their explicitly declared station.
    /// </summary>
    public static IReadOnlyList<string> ResolveSignalChannels(string dmAddress) =>
        ChannelCatalog.All.Where(c => string.Equals(c.PlcSignalAddress, dmAddress,
            StringComparison.Ordinal)).Select(c => c.ChannelId).ToArray();

    public static string? ResolveSignalStation(PlcEngineeringSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var channelIds = ResolveSignalChannels(signal.DmAddress);
        if (channelIds.Count != 0)
            return ChannelCatalog.Require(channelIds[0]).StationId;
        return Stations.Any(s => s.StationId == signal.StationId)
            ? signal.StationId : null;
    }

    public static int FramePreviewPort(string channelId) =>
        ChannelCatalog.Require(channelId).OwnerRole == MachineRole.Master ? 19631 : 19632;
}
