using System.Collections.ObjectModel;

namespace AOI.Domain;

public enum MachineRole { Master, Slave }
public enum ChannelOutcome { Ok, Ng, Error, Missing, Uncertain }
public enum FinalDisposition { Hold, Good, Reject }

/// <summary>Barcode is descriptive, NEVER the primary key. A new physical item always gets a new WorkpieceId.</summary>
public sealed record WorkpieceIdentity(
    Guid WorkpieceId, string Barcode, long Epoch, string ReleaseId, Guid TraceId,
    DateTimeOffset CreatedAtUtc)
{
    public static WorkpieceIdentity New(string barcode, long epoch, string releaseId) =>
        new(Guid.NewGuid(), barcode, epoch, releaseId, Guid.NewGuid(), DateTimeOffset.UtcNow);

    public void Validate()
    {
        if (WorkpieceId == Guid.Empty || TraceId == Guid.Empty || Epoch < 0 ||
            string.IsNullOrWhiteSpace(ReleaseId))
            throw new ArgumentException("Missing workpiece identity, epoch, release or trace.");
        // Unreadable / late barcode is a valid, explicitly uncertain state; it is not an identity.
    }
}

public sealed record ChannelDefinition(
    string ChannelId, string StationId, MachineRole OwnerRole, string PhysicalDeviceId,
    string PlcSignalAddress, bool IsPositioning, string? SharedFrameGroup);

public static class ChannelCatalog
{
    // Values from v2.0 07_可机读清单/station_channel_map.csv (2026-10-15); field validation remains pending.
    public static IReadOnlyList<ChannelDefinition> All { get; } = new ReadOnlyCollection<ChannelDefinition>([
        new("CH01", "LOADING", MachineRole.Master, "HIK-M-1", "D1006", true, null),
        new("CH02", "ST03", MachineRole.Slave, "HIK-S-2", "D1008", false, null),
        new("CH03", "ST03", MachineRole.Slave, "HIK-S-1", "D1007", false, null),
        new("CH04", "ST04", MachineRole.Slave, "HIK-S-4", "D1010", false, null),
        new("CH05", "ST04", MachineRole.Slave, "HIK-S-3", "D1009", false, null),
        new("CH06", "ST05", MachineRole.Master, "HIK-M-2", "D1011", false, null),
        new("CH07", "ST06", MachineRole.Master, "HIK-M-3", "D1012", false, null),
        new("CH08", "ST09", MachineRole.Slave, "LINE-S-8", "D1013", false, "LINE-S-8"),
        new("CH09", "ST09", MachineRole.Slave, "LINE-S-8", "D1013", false, "LINE-S-8"),
        new("CH10", "ST10", MachineRole.Master, "LINE-M-2", "D1014", false, "LINE-M-2"),
        new("CH11", "ST10", MachineRole.Master, "LINE-M-2", "D1014", false, "LINE-M-2")
    ]);

    public static ChannelDefinition Require(string channelId) =>
        All.FirstOrDefault(x => x.ChannelId == channelId)
        ?? throw new ArgumentException($"Unknown channel: {channelId}", nameof(channelId));
}

public sealed record ChannelResult(
    Guid ResultId, Guid WorkpieceId, long Epoch, string ReleaseId, Guid TraceId,
    string ChannelId, string StationId, string PhysicalDeviceId,
    Guid CaptureId, Guid FrameId, Guid? ParentFrameId, int AttemptNo,
    string SourceFrameSha256, ChannelOutcome Outcome, bool IsSimulated, DateTimeOffset FinishedUtc)
{
    public void ValidateFor(WorkpieceIdentity workpiece)
    {
        workpiece.Validate();
        if (ResultId == Guid.Empty || CaptureId == Guid.Empty || FrameId == Guid.Empty || AttemptNo < 1 ||
            WorkpieceId != workpiece.WorkpieceId || Epoch != workpiece.Epoch ||
            ReleaseId != workpiece.ReleaseId || TraceId != workpiece.TraceId)
            throw new InvalidOperationException("Result/workpiece identity mismatch or incomplete capture lineage.");
        var expected = ChannelCatalog.Require(ChannelId);
        if (StationId != expected.StationId || PhysicalDeviceId != expected.PhysicalDeviceId)
            throw new InvalidOperationException("Wrong station/device/channel binding; fail closed.");
        if (expected.SharedFrameGroup is null && ParentFrameId is not null)
            throw new InvalidOperationException("Area camera must not have line-scan parent frame.");
        if (expected.SharedFrameGroup is not null && ParentFrameId is null)
            throw new InvalidOperationException("Line-scan child frame must retain parent frame identity.");
        if (SourceFrameSha256.Length != 64 || !SourceFrameSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Missing/invalid raw frame SHA256.");
    }
}

public sealed record FinalDecision(FinalDisposition Disposition, bool IsSimulation,
    IReadOnlyList<string> Expected, IReadOnlyList<string> Received, string Proof);

public static class QualityRules
{
    public static FinalDecision Evaluate(WorkpieceIdentity workpiece,
        IEnumerable<string> expectedChannels, IEnumerable<ChannelResult> observations, bool endBarcodeMatches)
    {
        workpiece.Validate();
        var expected = expectedChannels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0 || expected.Any(x => ChannelCatalog.Require(x).IsPositioning))
            throw new ArgumentException("A product requires explicit inspection channels; CH01 is a positioning prerequisite.");

        var accepted = new Dictionary<string, ChannelResult>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (!expected.Contains(observation.ChannelId, StringComparer.Ordinal))
                continue;
            try { observation.ValidateFor(workpiece); }
            catch (Exception ex) { return Hold(expected, accepted, $"ASSOCIATION_UNCERTAIN: {ex.Message}"); }
            if (accepted.TryGetValue(observation.ChannelId, out var prior))
            {
                if (observation.AttemptNo < prior.AttemptNo) continue;
                if (observation.AttemptNo == prior.AttemptNo && observation != prior)
                    return Hold(expected, accepted, "CONFLICTING_RESULT_FOR_SAME_ATTEMPT");
            }
            accepted[observation.ChannelId] = observation;
        }
        if (!endBarcodeMatches) return Hold(expected, accepted, "END_BARCODE_MISMATCH_OR_MISSING");
        var absent = expected.Where(x => !accepted.ContainsKey(x)).ToArray();
        if (absent.Length != 0) return Hold(expected, accepted, "MISSING_CHANNELS:" + string.Join(",", absent));
        if (accepted.Values.Any(x => x.Outcome is ChannelOutcome.Error or ChannelOutcome.Missing or ChannelOutcome.Uncertain))
            return Hold(expected, accepted, "PROCESSING_INCOMPLETE_OR_UNCERTAIN");
        var simulated = accepted.Values.Any(x => x.IsSimulated);
        return accepted.Values.Any(x => x.Outcome == ChannelOutcome.Ng)
            ? new(FinalDisposition.Reject, simulated, expected, expected, "COMPLETE_NG" + (simulated ? ":SIM_ONLY" : ""))
            : new(FinalDisposition.Good, simulated, expected, expected, "COMPLETE_OK" + (simulated ? ":SIM_ONLY" : ""));
    }

    private static FinalDecision Hold(string[] expected, Dictionary<string, ChannelResult> received, string why) =>
        new(FinalDisposition.Hold, received.Values.Any(x => x.IsSimulated), expected,
            received.Keys.Order(StringComparer.Ordinal).ToArray(), why);
}
