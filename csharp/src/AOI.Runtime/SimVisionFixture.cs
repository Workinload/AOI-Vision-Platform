using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime;

/// <summary>SIM_ONLY native pixel source, never a production camera or model.</summary>
public static class SimVisionFixture
{
    public static IReadOnlyList<ChannelResult> CaptureAllMappedChannels(
        WorkpieceIdentity item, VisionAssetLedger ledger, SimPixelRule? rule = null) =>
        CaptureMappedChannels(item, ledger, rule, owner: null);

    /// <summary>
    /// Capture only this Core's physical devices: 5 logical Master / 6 logical Slave channels.
    /// Shared line-camera parents are captured once, not once per child channel.
    /// </summary>
    public static IReadOnlyList<ChannelResult> CaptureOwnedChannels(
        WorkpieceIdentity item, MachineRole owner, VisionAssetLedger ledger, SimPixelRule? rule = null) =>
        CaptureMappedChannels(item, ledger, rule, owner);

    private static IReadOnlyList<ChannelResult> CaptureMappedChannels(
        WorkpieceIdentity item, VisionAssetLedger ledger, SimPixelRule? rule, MachineRole? owner)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        item.Validate();
        var selected = ChannelCatalog.All
            .Where(c => owner is null || c.OwnerRole == owner.Value).ToArray();
        var results = new List<ChannelResult>();
        foreach (var channel in selected.Where(c => c.SharedFrameGroup is null))
        {
            using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(image, new Rect(8, 8, 48, 48), Scalar.White, -1);
            results.Add(ledger.CaptureArea(item, channel.ChannelId, image, simRule: rule));
        }

        foreach (var group in selected.Where(c => c.SharedFrameGroup is not null)
            .Select(c => c.SharedFrameGroup!).Distinct(StringComparer.Ordinal))
        {
            using var parent = new Mat(32, 64, MatType.CV_8UC1, Scalar.Black);
            // Tiny SIM_LIGHT geometry only. Field parent 8200x15000 NOT validated.
            Cv2.Rectangle(parent, new Rect(4, 4, 24, 24), Scalar.White, -1);
            Cv2.Rectangle(parent, new Rect(36, 4, 24, 24), Scalar.White, -1);
            results.AddRange(ledger.CaptureLinePair(item, group, parent,
                new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32),
                simRule: rule));
        }
        return results.OrderBy(x => x.ChannelId, StringComparer.Ordinal).ToArray();
    }
}
