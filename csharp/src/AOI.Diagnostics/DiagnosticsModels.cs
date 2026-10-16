using AOI.Contracts;
using AOI.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AOI.Diagnostics;

/// <summary>
/// Read-only evidence projections of A0's two independent production WALs.
/// No raw camera frames, PLC credentials, peer payload JSON or barcodes are exported.
/// The record types are local to A7; A0-owned wire contracts are untouched.
/// </summary>
public sealed record WorkpieceEvidence(WorkpieceIdentity Identity, FinalDecision? Final);

public sealed record OutboxEvidence(
    Guid MessageId, string SenderNodeId, string ReceiverNodeId,
    Guid WorkpieceId, Guid TraceId, long Epoch, string ReleaseId,
    string PayloadType, string? ChannelId, string PayloadHash,
    DateTimeOffset CreatedUtc, DateTimeOffset? AcknowledgedUtc,
    bool EnvelopeValid);

public sealed record InboxEvidence(
    string SenderNodeId, Guid MessageId, string PayloadHash, DateTimeOffset ReceivedUtc);

public sealed record FlightEventEvidence(
    Guid EventId, DateTimeOffset Utc, string Kind,
    Guid? WorkpieceId, Guid? MessageId, string Details);

public sealed record NodeEvidence(
    string NodeId, bool DatabaseAvailable, WorkpieceEvidence[] Workpieces,
    ChannelResult[] Results, OutboxEvidence[] Outbox, InboxEvidence[] Inbox,
    FlightEventEvidence[] Events);

public sealed record DiagnosticSnapshot(
    int SchemaVersion, string Mode, DateTimeOffset CapturedUtc, NodeEvidence[] Nodes);

public sealed record DiagnosticFinding(
    string Severity, string Code, string NodeId, Guid? WorkpieceId, string Evidence);

public sealed record DiagnosticAssessment(
    DateTimeOffset EvaluatedUtc, int WorkpieceCount, int ResultCount,
    DiagnosticFinding[] Findings);

public sealed record FlightRecord(
    int FormatVersion, long Sequence, string PreviousSha256,
    string SnapshotSha256, string EntrySha256, DiagnosticSnapshot Snapshot);

public sealed record DiagnosticBundleManifest(
    int BundleVersion, string Mode, DateTimeOffset CreatedUtc,
    int RecordCount, string JournalHeadSha256, string JournalFileSha256,
    string FindingsFileSha256, string TriageFileSha256);

public static class DiagnosticsFormat
{
    public const int SnapshotVersion = 1;
    public const int JournalVersion = 1;
    public const string SimMode = "SIM_ONLY_READ_ONLY";
    public static readonly string GenesisHash = new('0', 64);

    // Use the existing A0 serialization convention for interoperable DTOs.
    public static JsonSerializerOptions Json => WireFormat.Json;
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidDataException($"Cannot parse diagnostic {typeof(T).Name}.");

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    public static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // Wall-clock time is intentionally excluded from a *state* fingerprint.
    // It remains part of every tamper-evident journal entry and replay assessment.
    public static string StateHash(DiagnosticSnapshot snapshot) =>
        Sha256(Serialize(snapshot with { CapturedUtc = DateTimeOffset.UnixEpoch }));
}
