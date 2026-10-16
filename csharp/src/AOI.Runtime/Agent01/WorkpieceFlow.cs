using AOI.Domain;

namespace AOI.Runtime.Agent01;

/// <summary>Derived, per-WorkpieceId Core state. It never actuates a PLC.</summary>
public enum WorkpiecePhase
{
    WaitingForChannels,
    WaitingForEndVerification,
    Held,
    ReadyToCommit,
    FinalCommitted
}

/// <summary>
/// A barcode is evidence, not identity. A fixture echo is not an independent exit scan.
/// The caller must bind the scan to its physical workpiece before constructing this value.
/// </summary>
public sealed record EndScanEvidence(
    Guid WorkpieceId, long Epoch, string ReleaseId, Guid TraceId,
    string Barcode, bool IndependentRead, bool Simulated)
{
    public static EndScanEvidence SimulatedFixtureEcho(WorkpieceIdentity item, string barcode) =>
        new(item.WorkpieceId, item.Epoch, item.ReleaseId, item.TraceId,
            barcode, IndependentRead: false, Simulated: true);
}

public sealed record WorkpieceAssessment(
    Guid WorkpieceId, WorkpiecePhase Phase, FinalDecision? Decision, string Reason);

/// <summary>
/// Deterministic state calculation using persisted observations. Safe to re-run after a Core restart.
/// Every call is scoped to one immutable WorkpieceId, Epoch, ReleaseId and TraceId.
/// </summary>
public static class WorkpieceFlow
{
    public static WorkpieceAssessment Assess(
        WorkpieceIdentity item, IEnumerable<string> requiredChannels,
        IEnumerable<ChannelResult> observations, EndScanEvidence? endScan,
        FinalDecision? committedFinal, bool simulationOnly)
    {
        item.Validate();
        ArgumentNullException.ThrowIfNull(requiredChannels);
        ArgumentNullException.ThrowIfNull(observations);

        WorkpieceAssessment At(WorkpiecePhase phase, string why, FinalDecision? result = null) =>
            new(item.WorkpieceId, phase, result, why);

        var expected = requiredChannels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (expected.Length == 0 || expected.Any(c => ChannelCatalog.Require(c).IsPositioning))
            throw new ArgumentException("Explicit inspection channels are required; CH01 is positioning only.",
                nameof(requiredChannels));

        if (committedFinal is not null)
        {
            if (committedFinal.Disposition == FinalDisposition.Hold ||
                (simulationOnly && !committedFinal.IsSimulation))
                return At(WorkpiecePhase.Held, "INVALID_OR_UNSAFE_PERSISTED_FINAL");
            return At(WorkpiecePhase.FinalCommitted, "FINAL_ALREADY_PERSISTED", committedFinal);
        }

        var rows = observations.ToArray();
        foreach (var observation in rows)
        {
            if (!expected.Contains(observation.ChannelId, StringComparer.Ordinal))
                return At(WorkpiecePhase.Held, "UNEXPECTED_CHANNEL_FOR_WORKPIECE");
            try { observation.ValidateFor(item); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return At(WorkpiecePhase.Held, "INVALID_CAPTURE_LINEAGE:" + ex.Message);
            }
            if (simulationOnly != observation.IsSimulated)
                return At(WorkpiecePhase.Held, "MIXED_SIMULATION_AND_REAL_CAPTURE");
        }

        // Passing true only checks completeness / attempt conflicts / quality here.
        // Exit verification is an independent, mandatory stage below.
        var candidate = QualityRules.Evaluate(item, expected, rows, endBarcodeMatches: true);
        if (candidate.Disposition == FinalDisposition.Hold)
        {
            return candidate.Proof.StartsWith("MISSING_CHANNELS:", StringComparison.Ordinal)
                ? At(WorkpiecePhase.WaitingForChannels, candidate.Proof)
                : At(WorkpiecePhase.Held, candidate.Proof);
        }

        if (endScan is null)
            return At(WorkpiecePhase.WaitingForEndVerification, "END_SCAN_NOT_RECEIVED");

        if (endScan.WorkpieceId != item.WorkpieceId || endScan.Epoch != item.Epoch ||
            endScan.ReleaseId != item.ReleaseId || endScan.TraceId != item.TraceId)
            return At(WorkpiecePhase.Held, "END_SCAN_WORKPIECE_EPOCH_OR_RELEASE_MISMATCH");
        if (string.IsNullOrWhiteSpace(endScan.Barcode) ||
            !string.Equals(endScan.Barcode, item.Barcode, StringComparison.Ordinal))
            return At(WorkpiecePhase.Held, "END_BARCODE_MISMATCH_OR_MISSING");
        if (!endScan.IndependentRead && !endScan.Simulated)
            return At(WorkpiecePhase.Held, "INDEPENDENT_END_SCAN_REQUIRED");
        if (simulationOnly != endScan.Simulated)
            return At(WorkpiecePhase.Held, "END_SCAN_EXECUTION_MODE_MISMATCH");

        // Fixture evidence may only produce SIM_ONLY dispositions, never REAL proof.
        if (simulationOnly)
            candidate = candidate with
            {
                IsSimulation = true,
                Proof = candidate.Proof.Contains("SIM_ONLY", StringComparison.Ordinal)
                    ? candidate.Proof : candidate.Proof + ":SIM_ONLY_FIXTURE_ECHO"
            };
        return At(WorkpiecePhase.ReadyToCommit, candidate.Proof, candidate);
    }
}
