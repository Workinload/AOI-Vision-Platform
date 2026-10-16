using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;

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
        ProductionStore store, string nodeFolder)
    {
        if (item.ReleaseId != "SIM_LIGHT_TEST_RELEASE")
            throw new InvalidOperationException("11-channel fixture is SIM_LIGHT_TEST_RELEASE only.");
        var ledger = new VisionAssetLedger(Path.Combine(nodeFolder, "vision"));
        var frames = SimVisionFixture.CaptureOwnedChannels(item, role, ledger, new SimPixelRule(127, 0.1));
        int expected = ChannelCatalog.All.Count(c => c.OwnerRole == role);
        if (frames.Count != expected || frames.Any(x => !x.IsSimulated || x.Outcome != ChannelOutcome.Ok))
            throw new InvalidDataException("Synthetic channel capture incomplete, HOLD.");
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
