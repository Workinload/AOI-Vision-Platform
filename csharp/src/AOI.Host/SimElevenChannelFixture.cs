using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using AOI.Solutions;
using System.Globalization;

namespace AOI.Host;

/// <summary>
/// Opt-in SIM_LIGHT 11-logical-channel integration fixture. No real PLC or camera routes.
/// Assets are committed to their own WAL before ProductionStore result + outbox commits.
/// Incomplete crash-recovery is deliberately HOLD and does not auto-certify quality.
/// </summary>
public static class SimElevenChannelFixture
{
    public static bool Enabled => Environment.GetEnvironmentVariable("AOI_SIM_CHANNEL_SET") switch
    {
        null or "" => false,
        "ALL_11" => true,
        _ => throw new InvalidOperationException(
            "Unknown SIM channel set. Only unset/default or ALL_11 is permitted.")
    };

    public static IReadOnlyList<string> InspectionChannels { get; } =
        ChannelCatalog.All.Where(c => !c.IsPositioning)
            .Select(c => c.ChannelId).Order(StringComparer.Ordinal).ToArray();

    public static int CaptureOwned(WorkpieceIdentity item, MachineRole role,
        ProductionStore store, string nodeFolder, SimProductRuntime? product = null)
    {
        if (product is null)
        {
            if (item.ReleaseId != "SIM_LIGHT_TEST_RELEASE")
                throw new InvalidOperationException("SIM_LIGHT fixture does not accept an unverified product.");
        }
        else product.VerifyWorkpiece(item);
        var ledger = new VisionAssetLedger(Path.Combine(nodeFolder, "vision"));
        Func<ChannelDefinition, SimPixelRule>? rules = product is null ? null : product.RuleFor;
        ImageRegion? ReleasedRoi(ChannelDefinition c, int width, int height)
        {
            var published = product?.Published;
            if (published is null) return null;
            var roi = RoiReleaseWorkflow.ReadPublishedRoi(published, c.ChannelId);
            if (roi is null) return null;
            var bounds = roi.Bounds.ToPixels(width, height);
            return new ImageRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        var root = Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_ROOT");
        IReadOnlyList<ChannelResult> frames;
        if (!string.IsNullOrWhiteSpace(root))
        {
            // Explicit operator PNG input; NO silent synthetic fallback on missing pixels.
            var layout = Environment.GetEnvironmentVariable("AOI_SIM_IMAGE_LAYOUT");
            if (layout is not (null or "" or "FLAT"))
                throw new InvalidOperationException("Only workpiece-scoped or explicit FLAT SIM_ONLY image layout is supported.");
            double minArea = 8;
            var raw = Environment.GetEnvironmentVariable("AOI_SIM_CONTOUR_MIN_AREA");
            if (!string.IsNullOrWhiteSpace(raw) &&
                (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out minArea) ||
                 !double.IsFinite(minArea) || minArea <= 0 || minArea > 1000000))
                throw new InvalidDataException("Invalid SIM_ONLY contour minimum area.");
            var replay = new OfflineVisionReplay(ledger);
            var batch = replay.CaptureOwned(item, role, root,
                Path.Combine(nodeFolder, "vision-overlays"),
                new ContourDefectRule(127, minArea),
                flatDemoFolder: layout == "FLAT",
                releasedRoiForImage: (channelId, width, height) =>
                    ReleasedRoi(ChannelCatalog.Require(channelId), width, height),
                releasedRuleForChannel: channelId =>
                    new ContourDefectRule(product?.RuleFor(ChannelCatalog.Require(channelId)).BrightThreshold
                        ?? (byte)127, minArea));
            if (batch.Missing != 0 || batch.Channels.Any(c =>
                c.Observation is null || c.Outcome is ChannelOutcome.Missing or ChannelOutcome.Error or ChannelOutcome.Uncertain))
                throw new InvalidDataException("SIM_IMAGE_INPUT_INCOMPLETE: no fake GOOD, missing/invalid real PNG.");
            frames = batch.Channels.Select(c => c.Observation!).ToArray();
            Console.WriteLine($"SIM_IMAGE_REAL_IMPORTED role={role} id={item.WorkpieceId} " +
                $"channels={frames.Count} defective={batch.Defective} overlays=TRUE");
        }
        else
        {
            frames = SimVisionFixture.CaptureOwnedChannels(item, role, ledger,
                new SimPixelRule(127, 0.1), rules, ReleasedRoi);
        }
        int expected = ChannelCatalog.All.Count(c => c.OwnerRole == role);
        if (frames.Count != expected ||
            frames.Any(x => !x.IsSimulated || x.Outcome is ChannelOutcome.Missing or ChannelOutcome.Error or ChannelOutcome.Uncertain))
            throw new InvalidDataException("SIM_ONLY channel capture incomplete, HOLD.");
        long sequence = 0;
        foreach (var result in frames)
        {
            PeerEnvelope? outbound = role == MachineRole.Slave
                ? PeerEnvelope.Create("slave", "master", ++sequence, item, "channel_result",
                    result, result.ChannelId)
                : null;
            store.SaveLocalResult(result, outbound);
        }
        return frames.Count;
    }
}
