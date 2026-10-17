using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime;

/// <summary>SIM_ONLY native pixel source, never a production camera or model.</summary>
public static class SimVisionFixture
{
    public static IReadOnlyList<ChannelResult> CaptureAllMappedChannels(
        WorkpieceIdentity item, VisionAssetLedger ledger, SimPixelRule? rule = null) =>
        CaptureMappedChannels(item, ledger, rule, owner: null, perChannelRule: null, perChannelRoi: null);

    /// <summary>
    /// Capture only this Core's physical devices: 5 logical Master / 6 logical Slave channels.
    /// Shared line-camera parents are captured once, not once per child channel.
    /// </summary>
    public static IReadOnlyList<ChannelResult> CaptureOwnedChannels(
        WorkpieceIdentity item, MachineRole owner, VisionAssetLedger ledger, SimPixelRule? rule = null,
        Func<ChannelDefinition, SimPixelRule>? perChannelRule = null,
        Func<ChannelDefinition, int, int, ImageRegion?>? perChannelRoi = null) =>
        CaptureMappedChannels(item, ledger, rule, owner, perChannelRule, perChannelRoi);

    private static IReadOnlyList<ChannelResult> CaptureMappedChannels(
        WorkpieceIdentity item, VisionAssetLedger ledger, SimPixelRule? rule, MachineRole? owner,
        Func<ChannelDefinition, SimPixelRule>? perChannelRule,
        Func<ChannelDefinition, int, int, ImageRegion?>? perChannelRoi)
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
            var pixelRule = perChannelRule?.Invoke(channel) ?? rule;
            var publishedRoi = perChannelRoi?.Invoke(channel, image.Cols, image.Rows);
            if (publishedRoi is null)
                results.Add(ledger.CaptureArea(item, channel.ChannelId, image, simRule: pixelRule));
            else
            {
                // Real A6 released ROI changes SIM_ONLY detection, not the immutable source pixels.
                publishedRoi.ValidateWithin(image.Cols, image.Rows);
                if (pixelRule is null) throw new InvalidDataException("Released ROI requires a pixel rule.");
                using var inspected = new Mat(image, publishedRoi.ToRect());
                var probe = VisionPixelOps.Probe(inspected, pixelRule.BrightThreshold);
                var result = probe.BrightFraction >= pixelRule.MinimumBrightFraction
                    ? ChannelOutcome.Ok : ChannelOutcome.Ng;
                results.Add(ledger.CaptureAreaDetected(item, channel.ChannelId, image, result));
            }
        }

        foreach (var group in selected.Where(c => c.SharedFrameGroup is not null)
            .Select(c => c.SharedFrameGroup!).Distinct(StringComparer.Ordinal))
        {
            if (perChannelRoi is not null &&
                selected.Where(c => c.SharedFrameGroup == group)
                    .Any(c => perChannelRoi(c, 64, 16) is not null))
                throw new InvalidDataException(
                    "Published interleaved-line ROI requires real PNG replay; old synthetic rectangular split is not compatible.");
            using var parent = new Mat(32, 64, MatType.CV_8UC1, Scalar.Black);
            // Tiny SIM_LIGHT geometry only. Field parent 8200x15000 NOT validated.
            Cv2.Rectangle(parent, new Rect(4, 4, 24, 24), Scalar.White, -1);
            Cv2.Rectangle(parent, new Rect(36, 4, 24, 24), Scalar.White, -1);
            results.AddRange(ledger.CaptureLinePair(item, group, parent,
                new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32),
                simRule: perChannelRule?.Invoke(selected.First(c => c.SharedFrameGroup == group)) ?? rule));
        }
        return results.OrderBy(x => x.ChannelId, StringComparer.Ordinal).ToArray();
    }
}
