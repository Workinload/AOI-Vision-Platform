using AOI.Contracts;
using AOI.Domain;

namespace AOI.Runtime.Agent01;

/// <summary>
/// Reconciles the crash window between durable slave Inbox commit (and ACK) and
/// synthetic result generation. Restricted to the known CH02 SIM_LIGHT fixture.
/// No real camera, PLC, command channel or production-write path is exposed.
/// </summary>
public static class SimOnlySlaveRecovery
{
    public const string FixtureRelease = "SIM_LIGHT_TEST_RELEASE";
    public const string FixtureChannel = "CH02";

    public static bool ProcessRegisteredWorkpiece(
        ProductionStore store, WorkpieceIdentity item,
        Func<WorkpieceIdentity, ChannelResult> captureFixture)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(captureFixture);
        item.Validate();
        if (item.ReleaseId != FixtureRelease)
            throw new InvalidOperationException("Fixture generation must not run for an unapproved release.");
        var persisted = store.FindWorkpiece(item.WorkpieceId)
            ?? throw new InvalidDataException("Cannot capture workpiece before its Inbox is durable.");
        if (persisted != item)
            throw new InvalidDataException("Registered workpiece lineage is inconsistent.");
        if (store.ResultsFor(item.WorkpieceId).Any(r => r.ChannelId == FixtureChannel))
            return false;

        // Image generation happens outside the database transaction. The result and
        // the outbound notification are then persisted atomically by ProductionStore.
        // After a crash, an orphan fixture PNG is possible; it is not final evidence.
        var result = captureFixture(item);
        result.ValidateFor(item);
        if (result.ChannelId != FixtureChannel || !result.IsSimulated)
            throw new InvalidDataException("Fixture must return synthetic CH02 bound to the same workpiece.");
        var outbound = PeerEnvelope.Create("slave", "master", 1, item,
            "channel_result", result, FixtureChannel);
        store.SaveLocalResult(result, outbound);
        return true;
    }

    public static int RecoverMissingResults(
        ProductionStore store, Func<WorkpieceIdentity, ChannelResult> captureFixture,
        int limit = 256)
    {
        ArgumentNullException.ThrowIfNull(store);
        var recovered = 0;
        foreach (var item in store.WorkpiecesMissingChannel(FixtureChannel, limit))
        {
            // Do not let old or future REAL releases enter the SIM_ONLY producer.
            if (item.ReleaseId != FixtureRelease) continue;
            if (ProcessRegisteredWorkpiece(store, item, captureFixture))
                recovered++;
        }
        return recovered;
    }
}
