using AOI.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AOI.Diagnostics;

/// <summary>
/// Historical legacy PLC SIM_ONLY exit branches; these are not field-verified
/// protocol values. Only Master emits final output actions.
/// </summary>
public enum FinalOutputAuditScenario { Normal, BarcodeFailure, BarcodeMismatch }

/// <summary>
/// Every phase is an independent DURABLE observation. "Prepared" means a
/// datagram MIGHT have been sent when a process crashes; only NotSent with
/// explicit transport evidence proves no send. ACK and readback are different.
/// </summary>
public enum FinalOutputAuditPhase
{
    ScannerRequestVerified,
    StepPrepared,
    WireSent,
    AckAccepted,
    AckRejected,
    AckUnknown,
    ReadbackVerified,
    ReadbackMismatch,
    ReadbackUnknown,
    ExplicitlyNotSent,
    PeerMessageLinked
}

public enum FinalOutputAuditStepState
{
    NotStarted,
    PreparedSendUncertain,
    ExplicitlyNotSent,
    SentAckUnknown,
    AckRejected,
    AckAcceptedReadbackPending,
    ReadbackUnknown,
    ReadbackMismatch,
    Verified
}

public sealed record FinalOutputAuditEvent(
    long Sequence, Guid EventId, Guid AttemptId, FinalOutputAuditPhase Phase,
    int StepNumber, Guid? OperationId, byte? FinsSid,
    ushort? DmAddress, ushort? Value,
    string? RequestSha256, string? ResponseSha256, ushort? ReadbackValue,
    Guid? PeerMessageId, DateTimeOffset Utc, Guid ProcessRunId,
    long MonotonicTick, long TickFrequency, string? Detail);

public sealed record FinalOutputAuditStep(
    int Number, ushort DmAddress, ushort ExpectedValue,
    FinalOutputAuditStepState State, Guid? OperationId, byte? FinsSid,
    string? WriteRequestSha256, string? WriteResponseSha256,
    string? ReadRequestSha256, string? ReadResponseSha256,
    ushort? ReadbackValue, string? Detail);

public sealed record FinalOutputAuditTrace(
    Guid AttemptId, Guid WorkpieceId, Guid TraceId, long Epoch,
    string ReleaseId, ushort Token, FinalOutputAuditScenario Scenario,
    FinalDisposition Disposition, Guid? PeerMessageId, DateTimeOffset StartedUtc,
    bool ScannerRequestVerified, FinalOutputAuditStep[] Steps,
    FinalOutputAuditEvent[] Events, bool FullyVerified,
    bool RequiresManualReconciliation, string[] Findings);

/// <summary>
/// Reads raw FINS/UDP word-one datagrams supplied by the A2 transport at the
/// REAL send/receive boundary. SHA256 is supplemental integrity evidence;
/// FINS/UDP has no per-datagram application CRC field to invent.
/// This is NOT a PLC client: it does not open sockets or authorize any write.
/// </summary>
public static class FinalOutputFinsEvidence
{
    public static (byte Sid, ushort Address, ushort Value) WriteRequest(ReadOnlySpan<byte> packet)
    {
        var (sid, address, count) = Header(packet, 2);
        if (packet.Length != 20 || count != 1)
            throw new InvalidDataException("Expected one DM write word.");
        return (sid, address, BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(18, 2)));
    }

    public static (byte Sid, ushort Address) ReadRequest(ReadOnlySpan<byte> packet)
    {
        var (sid, address, count) = Header(packet, 1);
        if (packet.Length != 18 || count != 1)
            throw new InvalidDataException("Expected one DM read word.");
        return (sid, address);
    }

    public static (ushort EndCode, ushort? Word) VerifiedReply(
        ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply, bool isRead)
    {
        _ = isRead ? ReadRequest(request) : WriteRequest(request);
        if (reply.Length < 14 || reply[0] != 0xc0 ||
            reply[1] != request[1] || reply[2] != request[2] ||
            reply[3] != request[6] || reply[4] != request[7] ||
            reply[5] != request[8] || reply[6] != request[3] ||
            reply[7] != request[4] || reply[8] != request[5] ||
            reply[9] != request[9] || reply[10] != request[10] ||
            reply[11] != request[11])
            throw new InvalidDataException("FINS reply route/SID/command cannot be paired to request.");
        ushort endCode = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(12, 2));
        int length = endCode == 0 && isRead ? 16 : 14;
        if (reply.Length != length)
            throw new InvalidDataException("FINS response length does not match command/end code.");
        ushort? word = length == 16
            ? BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(14, 2)) : null;
        return (endCode, word);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 256)
            throw new ArgumentException("Expected bounded nonempty raw FINS/UDP evidence.");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static (byte Sid, ushort Address, ushort Count) Header(ReadOnlySpan<byte> packet, byte opcode)
    {
        if (packet.Length < 18 || packet[0] != 0x80 || packet[1] != 0 ||
            packet[2] != 2 || packet[10] != 1 || packet[11] != opcode ||
            packet[12] != 0x82 || packet[15] != 0)
            throw new InvalidDataException("Invalid FINS/UDP DM request header.");
        return (packet[9],
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(13, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(16, 2)));
    }

    public static (ushort Address, ushort Value) Expected(
        FinalOutputAuditScenario scenario, FinalDisposition disposition, int step)
    {
        if (step is not (1 or 2) || !Enum.IsDefined(scenario) ||
            disposition is not (FinalDisposition.Good or FinalDisposition.Reject) ||
            (scenario != FinalOutputAuditScenario.Normal &&
             disposition != FinalDisposition.Reject))
            throw new ArgumentException("Invalid legacy final output branch/step.");
        ushort quality = disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2;
        return scenario switch
        {
            FinalOutputAuditScenario.Normal when step == 1 => (1016, quality),
            FinalOutputAuditScenario.Normal => (2099, 2),
            FinalOutputAuditScenario.BarcodeFailure when step == 1 => (2099, 3),
            FinalOutputAuditScenario.BarcodeFailure => (1016, 2),
            FinalOutputAuditScenario.BarcodeMismatch when step == 1 => (2099, 4),
            _ => (1016, 2)
        };
    }
}
