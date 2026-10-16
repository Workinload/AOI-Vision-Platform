using AOI.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AOI.Contracts;

public static class WireFormat
{
    public const int ProtocolVersion = 1;
    public static JsonSerializerOptions Json { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidDataException($"Could not parse {typeof(T).Name}");
}

public sealed record PeerEnvelope(
    int ProtocolVersion, Guid MessageId, string SenderNodeId, string ReceiverNodeId,
    long Epoch, Guid TraceId, Guid WorkpieceId, string? StationId, string? ChannelId,
    string SolutionReleaseId, long Sequence, DateTimeOffset CreatedAtUtc,
    string PayloadType, string PayloadHash, string PayloadJson)
{
    public static PeerEnvelope Create<T>(string sender, string receiver, long seq,
        WorkpieceIdentity workpiece, string type, T payload, string? channelId = null)
    {
        workpiece.Validate();
        var body = WireFormat.Serialize(payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return new(WireFormat.ProtocolVersion, Guid.NewGuid(), sender, receiver,
            workpiece.Epoch, workpiece.TraceId, workpiece.WorkpieceId,
            channelId is null ? null : ChannelCatalog.Require(channelId).StationId,
            channelId, workpiece.ReleaseId, seq, DateTimeOffset.UtcNow, type, hash, body);
    }
    public void Validate()
    {
        if (ProtocolVersion != WireFormat.ProtocolVersion || MessageId == Guid.Empty ||
            WorkpieceId == Guid.Empty || TraceId == Guid.Empty || Epoch < 0 || Sequence < 1 ||
            string.IsNullOrWhiteSpace(SenderNodeId) || string.IsNullOrWhiteSpace(ReceiverNodeId) ||
            string.IsNullOrWhiteSpace(SolutionReleaseId) ||
            !string.Equals(PayloadHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PayloadJson))).ToLowerInvariant(),
                StringComparison.Ordinal))
            throw new InvalidDataException("Invalid envelope, version or payload digest.");
    }
}

public sealed record PersistedAck(Guid MessageId, string SenderNodeId, string PayloadHash,
    DateTimeOffset PersistedAtUtc);
public sealed record PeerPacket(string Kind, PeerEnvelope? Envelope, PersistedAck? Ack);

public sealed record ChannelSnapshot(string ChannelId, string StationId, string PhysicalDeviceId,
    string Status, Guid? WorkpieceId, bool IsSimulated, DateTimeOffset? LastUpdatedUtc);
public sealed record MachineSnapshot(string NodeId, MachineRole Role, string MachineState,
    WorkpieceIdentity? LatestWorkpiece, FinalDecision? LatestFinal,
    IReadOnlyList<ChannelSnapshot> Channels, DateTimeOffset UpdatedUtc,
    bool RealHardwareWritesEnabled, string SimulationMode);
