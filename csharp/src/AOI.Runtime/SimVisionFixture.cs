using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime;

/// <summary>
/// Explicitly virtual 9-physical-device / 11-logical-channel pixel source for offline QA.
/// Synthetic geometry and brightness have NO connection to field ROIs or AOI model quality.
/// </summary>
public static class SimVisionFixture
{
    public static IReadOnlyList<ChannelResult> CaptureAllMappedChannels(
        WorkpieceIdentity item, VisionAssetLedger ledger, SimPixelRule? rule = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        item.Validate();
        var results = new List<ChannelResult>();
        foreach (var channel in ChannelCatalog.All.Where(c => c.SharedFrameGroup is null))
        {
            using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(image, new Rect(8, 8, 48, 48), Scalar.White, -1);
            results.Add(ledger.CaptureArea(item, channel.ChannelId, image, simRule: rule));
        }

        foreach (var group in ChannelCatalog.All.Where(c => c.SharedFrameGroup is not null)
            .Select(c => c.SharedFrameGroup!).Distinct(StringComparer.Ordinal))
        {
            using var parent = new Mat(32, 64, MatType.CV_8UC1, Scalar.Black);
            // Deliberately small SIM fixture; NOT a field 8200x15000 optical configuration.
            Cv2.Rectangle(parent, new Rect(4, 4, 24, 24), Scalar.White, -1);
            Cv2.Rectangle(parent, new Rect(36, 4, 24, 24), Scalar.White, -1);
            results.AddRange(ledger.CaptureLinePair(item, group, parent,
                new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32),
                simRule: rule));
        }
        return results.OrderBy(x => x.ChannelId, StringComparer.Ordinal).ToArray();
    }
}
