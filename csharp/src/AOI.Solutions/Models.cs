using AOI.Contracts;
using AOI.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AOI.Solutions;

/// <summary>A6 is SIM_ONLY. These types cannot enable a real PLC or certify field interlocks.</summary>
public static class SolutionSafety
{
    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static void RequireId(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$"))
            throw new ArgumentException("Invalid stable identifier.", name);
    }

    public static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
            throw new ArgumentException("An attributable operator/actor is required.", nameof(actor));
    }
}

public sealed record MachineProfile(
    string ProfileId, int Version, IReadOnlyList<ChannelDefinition> Channels, string CatalogSha256)
{
    public static MachineProfile SimBaseline(string profileId = "SIM_MACHINE_01")
    {
        var channels = ChannelCatalog.All.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        return new(profileId, 1, channels, SolutionSafety.Sha256(WireFormat.Serialize(channels)));
    }

    public void Validate()
    {
        SolutionSafety.RequireId(ProfileId, nameof(ProfileId));
        if (Version < 1 || Channels is null || CatalogSha256 is null)
            throw new InvalidDataException("Invalid profile version or channel mapping.");
        // Mapping, devices and UNKNOWN PLC addresses remain controlled by the A0 catalog.
        var expected = ChannelCatalog.All.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        var actual = Channels.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        if (actual.Length != 11 || actual.Where((channel, i) => channel != expected[i]).Any() ||
            !string.Equals(CatalogSha256, SolutionSafety.Sha256(WireFormat.Serialize(expected)), StringComparison.Ordinal))
            throw new InvalidDataException("MachineProfile diverges from A0's 9-device/11-channel source mapping.");
    }
}

public sealed record ProductSolution(string SolutionId, string ProfileId, string DisplayName);

public sealed record StationRecipe(
    string StationId, IReadOnlyList<string> ChannelIds, IReadOnlyDictionary<string, double> Tunables)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StationId) || ChannelIds is null || Tunables is null)
            throw new InvalidDataException("Incomplete station recipe.");
        var expected = ChannelCatalog.All.Where(c => c.StationId == StationId)
            .Select(c => c.ChannelId).ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0 || ChannelIds.Count != ChannelIds.Distinct(StringComparer.Ordinal).Count() ||
            ChannelIds.Any(x => !expected.Contains(x)))
            throw new InvalidDataException("Recipe binds a channel to the wrong station, or duplicates one.");
        if (Tunables.Count > 128)
            throw new InvalidDataException("Excessive tunables.");
        foreach (var (key, value) in Tunables)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9_.-]{0,63}$") ||
                !double.IsFinite(value) || Math.Abs(value) > 1e9)
                throw new InvalidDataException("Invalid finite, bounded recipe tunable.");
        }
    }

    internal StationRecipe Canonical() => new(
        StationId, ChannelIds.Order(StringComparer.Ordinal).ToArray(),
        new SortedDictionary<string, double>(Tunables.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal), StringComparer.Ordinal));
}

public sealed record StationDraft(
    string SolutionId, string StationId, long Revision, StationRecipe Recipe, string EditedBy);

public sealed record ReleaseManifest(
    string SolutionId, string ProfileId, int ProfileVersion, string CatalogSha256,
    IReadOnlyList<StationRecipe> Stations)
{
    public ReleaseManifest Canonical()
    {
        if (Stations is null) throw new InvalidDataException("No stations.");
        return this with
        {
            Stations = Stations.Select(s => s.Canonical())
                .OrderBy(s => s.StationId, StringComparer.Ordinal).ToArray()
        };
    }

    public void Validate()
    {
        SolutionSafety.RequireId(SolutionId, nameof(SolutionId));
        SolutionSafety.RequireId(ProfileId, nameof(ProfileId));
        if (ProfileVersion < 1 || Stations is null)
            throw new InvalidDataException("Invalid release manifest.");
        var topology = MachineProfile.SimBaseline().CatalogSha256;
        if (!string.Equals(CatalogSha256, topology, StringComparison.Ordinal))
            throw new InvalidDataException("Release topology mismatch.");
        foreach (var station in Stations) station.Validate();
        var expectedStations = ChannelCatalog.All.Select(c => c.StationId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var actualStations = Stations.Select(s => s.StationId).Order(StringComparer.Ordinal).ToArray();
        if (!actualStations.SequenceEqual(expectedStations, StringComparer.Ordinal))
            throw new InvalidDataException("Release must explicitly cover all mapped stations.");
        var expectedChannels = ChannelCatalog.All.Select(c => c.ChannelId)
            .Order(StringComparer.Ordinal).ToArray();
        var actualChannels = Stations.SelectMany(s => s.ChannelIds)
            .Order(StringComparer.Ordinal).ToArray();
        if (!actualChannels.SequenceEqual(expectedChannels, StringComparer.Ordinal))
            throw new InvalidDataException("Missing or duplicate logical channels in release.");
    }
}

public sealed record PublishedRelease(
    string ReleaseId, string ContentSha256, ReleaseManifest Manifest,
    DateTimeOffset PublishedAtUtc, string Actor)
{
    public void Verify()
    {
        Manifest.Validate();
        foreach (var station in Manifest.Stations) RoiReleaseWorkflow.ValidateStationRois(station);
        var digest = SolutionSafety.Sha256(WireFormat.Serialize(Manifest.Canonical()));
        if (!string.Equals(ContentSha256, digest, StringComparison.Ordinal) ||
            !string.Equals(ReleaseId, "SIMREL-" + digest[..32], StringComparison.Ordinal))
            throw new InvalidDataException("Immutable release digest / ID mismatch.");
    }
}

public sealed record SimInterlocks(
    bool SimOnly, bool RealHardwareWritesDisabled, bool ProductionDrained,
    bool NoInFlightWorkpieces, bool OutputsInSafeHold, bool PeerHealthy)
{
    public static SimInterlocks AllSafeForOfflineTest { get; } =
        new(true, true, true, true, true, true);

    public void RequireSafe()
    {
        if (!SimOnly || !RealHardwareWritesDisabled || !ProductionDrained ||
            !NoInFlightWorkpieces || !OutputsInSafeHold || !PeerHealthy)
            throw new InvalidOperationException("CHANGEOVER_BLOCKED: all SIM_ONLY interlocks must be explicitly safe.");
    }
}

public sealed record ChangeoverRequest(
    Guid TransactionId, PublishedRelease Target, long ExpectedEpoch,
    string? ExpectedReleaseId, long NextEpoch, string Actor)
{
    public void Validate()
    {
        if (TransactionId == Guid.Empty || ExpectedEpoch < 0 || ExpectedEpoch == long.MaxValue ||
            NextEpoch != ExpectedEpoch + 1)
            throw new InvalidDataException("Epoch must advance by exactly one.");
        SolutionSafety.RequireActor(Actor);
        Target.Verify();
    }
}

public sealed record PreparedReceipt(
    Guid TransactionId, string NodeId, MachineRole Role, string ReleaseId,
    string ContentSha256, long BaseEpoch, long NextEpoch, string ReadBackSha256);

public sealed record DualPreparedCertificate(
    Guid TransactionId, string ReleaseId, string ContentSha256, long NextEpoch,
    PreparedReceipt Master, PreparedReceipt Slave)
{
    public void Validate()
    {
        if (TransactionId == Guid.Empty || string.IsNullOrWhiteSpace(ReleaseId) ||
            ContentSha256.Length != 64 || NextEpoch < 1 ||
            Master is null || Slave is null ||
            Master.NodeId != "master" || Master.Role != MachineRole.Master ||
            Slave.NodeId != "slave" || Slave.Role != MachineRole.Slave ||
            Master.TransactionId != TransactionId || Slave.TransactionId != TransactionId ||
            Master.ReleaseId != ReleaseId || Slave.ReleaseId != ReleaseId ||
            Master.ContentSha256 != ContentSha256 || Slave.ContentSha256 != ContentSha256 ||
            Master.ReadBackSha256 != ContentSha256 || Slave.ReadBackSha256 != ContentSha256 ||
            Master.BaseEpoch != NextEpoch - 1 || Slave.BaseEpoch != NextEpoch - 1 ||
            Master.NextEpoch != NextEpoch || Slave.NextEpoch != NextEpoch)
            throw new InvalidDataException("PREPARED_BOTH requires matching independent read-back receipts.");
    }
}

public enum NodeChangeoverPhase { Idle, Prepared, ActivatedHold, CommittedHold }
public sealed record NodeSnapshot(
    string NodeId, MachineRole Role, long ActiveEpoch, string? ActiveReleaseId,
    Guid? PendingTransactionId, NodeChangeoverPhase Phase, bool SafeHold,
    bool RealHardwareWritesEnabled);

public enum ChangeoverPhase { Preparing, PreparedBoth, CommitIntent, RecoveryRequired, Committed, Aborted }
public sealed record ChangeoverStatus(
    Guid TransactionId, ChangeoverPhase Phase, string ReleaseId, long NextEpoch,
    DualPreparedCertificate? Certificate, string? Detail);

public interface ISimChangeoverNode
{
    string NodeId { get; }
    MachineRole Role { get; }
    NodeSnapshot Snapshot();
    PreparedReceipt Prepare(ChangeoverRequest request, SimInterlocks evidence);
    void Activate(DualPreparedCertificate certificate, SimInterlocks evidence);
    void FinalizePair(DualPreparedCertificate certificate, SimInterlocks evidence);
    void Abort(Guid transactionId, string reason);
}
