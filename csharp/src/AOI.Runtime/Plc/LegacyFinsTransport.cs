using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AOI.Runtime.Plc;

/// <summary>
/// A legacy FINS test routing PROFILE, not a verified production PLC configuration.
/// Explicit node/network/unit fields replace the old baked-in DA1=1 / SA1=99.
/// No remote address is expressible by the companion client or shadow listener.
/// </summary>
public readonly record struct LegacyFinsNodes(
    byte DestinationNode, byte SourceNode,
    byte DestinationNetwork = 0, byte SourceNetwork = 0,
    byte DestinationUnit = 0, byte SourceUnit = 0)
{
    public static LegacyFinsNodes Demo => new(1, 99);
    public static LegacyFinsNodes HistoricalCandidateMaster => new(50, 130);
    public static LegacyFinsNodes HistoricalCandidateSlave => new(50, 110);

    public void Validate()
    {
        if (DestinationNode == 0 || SourceNode == 0 ||
            DestinationNode == SourceNode)
            throw new ArgumentException("FINS routing nodes must be distinct nonzero values.");
    }
}

/// <summary>Unverified historical timing defaults, independently configurable by test/Host.</summary>
public sealed record LegacyPlcTiming(
    TimeSpan UdpRequestTimeout,
    TimeSpan AreaCameraDeadline,
    TimeSpan LineCameraDeadline,
    TimeSpan LoadBarcodeDeadline,
    TimeSpan EndResultDeadline)
{
    public static LegacyPlcTiming HistoricalReference { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2));

    public void Validate()
    {
        foreach (var span in new[] { UdpRequestTimeout, AreaCameraDeadline,
                     LineCameraDeadline, LoadBarcodeDeadline, EndResultDeadline })
            if (span <= TimeSpan.Zero || span > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException(nameof(LegacyPlcTiming),
                    "SIM_ONLY layer deadlines must be positive and bounded.");
    }
}

public sealed record LegacyDmRequest(bool IsWrite, ushort Address, ushort Count,
    ushort[] Values, byte Sid);

/// <summary>FINS/UDP DM-Word subset: route-checked packets with SID and swapped reply endpoints.</summary>
public static class LegacyFinsWire
{
    public const byte DmArea = 0x82;
    public static byte[] MakeRequest(bool write, ushort address, ReadOnlySpan<ushort> values,
        byte sid, LegacyFinsNodes nodes)
    {
        nodes.Validate();
        if (values.Length is < 1 or > 64 || address + values.Length > 5000)
            throw new ArgumentOutOfRangeException(nameof(values));
        var packet = new byte[18 + (write ? values.Length * 2 : 0)];
        packet[0] = 0x80; packet[2] = 2;
        packet[3] = nodes.DestinationNetwork; packet[4] = nodes.DestinationNode;
        packet[5] = nodes.DestinationUnit; packet[6] = nodes.SourceNetwork;
        packet[7] = nodes.SourceNode; packet[8] = nodes.SourceUnit;
        packet[9] = sid; packet[10] = 1; packet[11] = write ? (byte)2 : (byte)1;
        packet[12] = DmArea;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(13, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), (ushort)values.Length);
        if (write)
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18 + i * 2, 2), values[i]);
        return packet;
    }

    public static bool TryParseRequest(ReadOnlySpan<byte> request, LegacyFinsNodes nodes,
        out LegacyDmRequest operation)
    {
        operation = new LegacyDmRequest(false, 0, 0, [], 0);
        if (request.Length < 18 || request[0] != 0x80 || request[1] != 0 ||
            request[2] != 2 || request[3] != nodes.DestinationNetwork ||
            request[4] != nodes.DestinationNode || request[5] != nodes.DestinationUnit ||
            request[6] != nodes.SourceNetwork || request[7] != nodes.SourceNode ||
            request[8] != nodes.SourceUnit || request[10] != 1 ||
            request[11] is not (1 or 2) || request[12] != DmArea ||
            request[15] != 0) return false;

        bool isWrite = request[11] == 2;
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(13, 2));
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(16, 2));
        if (count is < 1 or > 64 || address + count > 5000 ||
            request.Length != 18 + (isWrite ? count * 2 : 0)) return false;
        ushort[] values = new ushort[isWrite ? count : 0];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(18 + 2 * i, 2));
        operation = new LegacyDmRequest(isWrite, address, count, values, request[9]);
        return true;
    }

    /// <summary>End code 0 is accepted; 0x0005 is shadow refusal (not a field PLC code claim).</summary>
    public static byte[] MakeResponse(ReadOnlySpan<byte> request, ushort endCode,
        ReadOnlySpan<ushort> words)
    {
        var reply = new byte[14 + words.Length * 2];
        request[..12].CopyTo(reply);
        reply[0] = 0xC0;
        reply[3] = request[6]; reply[4] = request[7]; reply[5] = request[8];
        reply[6] = request[3]; reply[7] = request[4]; reply[8] = request[5];
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(12, 2), endCode);
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(14 + i * 2, 2), words[i]);
        return reply;
    }

    public static ushort[] DecodeResponse(ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> reply, LegacyFinsNodes nodes)
    {
        if (!TryParseRequest(request, nodes, out var op))
            throw new InvalidDataException("Invalid original FINS request.");
        int expectedBytes = op.IsWrite ? 14 : 14 + 2 * op.Count;
        if (reply.Length < 14 || reply[0] != 0xC0 ||
            reply[1] != request[1] || reply[2] != request[2] ||
            reply[3] != request[6] || reply[4] != request[7] ||
            reply[5] != request[8] || reply[6] != request[3] ||
            reply[7] != request[4] || reply[8] != request[5] ||
            reply[9] != request[9] || reply[10] != request[10] ||
            reply[11] != request[11])
            throw new InvalidDataException("FINS response route/SID/command mismatch.");
        var endCode = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(12, 2));
        if (endCode != 0)
            throw new InvalidDataException($"SIM_LEGACY_FINS_REJECTED endcode=0x{endCode:X4}");
        if (reply.Length != expectedBytes)
            throw new InvalidDataException("FINS response DM payload length mismatch.");
        var values = new ushort[op.IsWrite ? 0 : op.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(reply.Slice(14 + i * 2, 2));
        return values;
    }
}

/// <summary>
/// Actual loopback FINS WRITE boundary, with one immutable operation identity and
/// the SID used in the raw UDP datagram. A sent write is NEVER a confirmed write.
/// </summary>
public sealed record LegacyFinsWriteProof(
    Guid OperationId, byte Sid, ushort DmAddress, ushort Value,
    DateTimeOffset SentUtc, DateTimeOffset? AcknowledgedUtc, bool ReplyVerified);

/// <summary>
/// Every phase describes the REAL datagram passed to or returned from the UDP socket.
/// Prepared is only a pre-send intent. WireSent means SendAsync returned successfully,
/// NOT that the peer received the packet. ReplyReceived is not a validated ACK.
/// AckAccepted requires matching endpoint, route, SID, opcode and FINS end-code.
/// ReadbackVerified is an independent real DM read (never inferred from a write).
/// </summary>
public enum LegacyFinsObservationStage
{
    Prepared,
    WireSent,
    ReplyReceived,
    AckAccepted,
    AckRejected,
    ReadbackVerified,
    TransportFailure
}

/// <summary>
/// Snapshot of a single localhost network operation: raw request/response are
/// immutable copies of the exact socket buffers. Empty ResponseBytes explicitly
/// means NO datagram was observed; it must never be filled with a synthetic reply.
/// Callback delivery is synchronous, in real network phase order, allowing A0/A7
/// to persist prepared intent before dispatching an irreversible output.
/// </summary>
public sealed record LegacyFinsDatagramObservation(
    Guid OperationId, byte Sid, ushort DmAddress, bool IsWrite,
    LegacyFinsObservationStage Stage, DateTimeOffset OccurredUtc,
    long MonotonicTimestamp, ImmutableArray<byte> RequestBytes,
    ImmutableArray<byte> ResponseBytes, string? Detail = null)
{
    public bool HasActualReply => !ResponseBytes.IsDefaultOrEmpty;
}

/// <summary>
/// Strict IPv4-loopback FINS/UDP test client; never accepts hostnames or production IPs.
/// FINS protocol SID, route and endpoint validated on each real network reply.
/// </summary>
public sealed class LegacyFinsLoopbackClient
{
    private readonly IPEndPoint _endpoint;
    private readonly LegacyFinsNodes _nodes;
    private readonly TimeSpan _timeout;
    private int _sid;

    public LegacyFinsLoopbackClient(int port, LegacyFinsNodes nodes, TimeSpan? timeout = null)
    {
        nodes.Validate();
        _endpoint = A2WritePolicy.RequireLoopback("127.0.0.1", port);
        _nodes = nodes;
        _timeout = timeout ?? LegacyPlcTiming.HistoricalReference.UdpRequestTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<ushort[]> ReadAsync(ushort address, ushort count,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(false, address, new ushort[count], cancellationToken);

    public async Task WriteAsync(ushort address, ushort[] values,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(true, address, values, cancellationToken);

    /// <summary>
    /// Original A1-compatible API, unchanged signature/semantics. Its pre-send
    /// callback still receives ONLY prepared intent, not a confirmed send/ACK.
    /// The shared raw transport now supplies the actual network proof internally.
    /// </summary>
    public Task<LegacyFinsWriteProof> WriteObservedAsync(
        ushort address, ushort value, Action<LegacyFinsWriteProof> onBeforeSend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBeforeSend);
        Guid operationId = Guid.NewGuid();
        return WriteRawObservedAsync(address, value, operationId, evt =>
        {
            if (evt.Stage == LegacyFinsObservationStage.Prepared)
                onBeforeSend(new LegacyFinsWriteProof(operationId, evt.Sid, address,
                    value, evt.OccurredUtc, AcknowledgedUtc: null, ReplyVerified: false));
        }, cancellationToken);
    }

    /// <summary>
    /// ADDITIVE raw write API. The OperationId is supplied by the durable caller;
    /// only this one FINS/UDP packet uses it. No implicit retry/readback.
    /// Observe Prepared BEFORE sending, WireSent AFTER successful SendAsync,
    /// and AckAccepted ONLY after receiving and validating the actual response.
    /// In a dropped-ACK scenario neither ReplyReceived nor AckAccepted is emitted.
    /// Observer exceptions propagate: caller must never assume a send was absent.
    /// </summary>
    public async Task<LegacyFinsWriteProof> WriteRawObservedAsync(
        ushort address, ushort value, Guid operationId,
        Action<LegacyFinsDatagramObservation> onObservation,
        CancellationToken cancellationToken = default)
    {
        var result = await ExchangeRawObservedAsync(true, address, [value],
            operationId, onObservation, cancellationToken);
        return new LegacyFinsWriteProof(operationId, result.Sid, address, value,
            result.PreparedUtc, result.AcknowledgedUtc, ReplyVerified: true);
    }

    /// <summary>
    /// ADDITIVE independent one-or-more-word READ: D2099=1 scanner observation
    /// and separate post-write D2099/D1016 readback are REAL socket operations.
    /// Only a validated read emits ReadbackVerified, with exact request AND reply.
    /// Read values return from verified reply bytes, never from expected write values.
    /// </summary>
    public async Task<ushort[]> ReadRawObservedAsync(
        ushort address, ushort count, Guid operationId,
        Action<LegacyFinsDatagramObservation> onObservation,
        CancellationToken cancellationToken = default)
    {
        var result = await ExchangeRawObservedAsync(false, address, new ushort[count],
            operationId, onObservation, cancellationToken);
        return result.Values;
    }

    private async Task<(ushort[] Values, byte Sid, DateTimeOffset PreparedUtc,
        DateTimeOffset AcknowledgedUtc)> ExchangeRawObservedAsync(
        bool write, ushort address, ushort[] values, Guid operationId,
        Action<LegacyFinsDatagramObservation> onObservation,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A durable FINS OperationId is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(onObservation);
        // The request is constructed ONCE; these exact bytes are sent to the socket.
        // No observer fabricates data from address/values or reconstructs a reply.
        byte sid = unchecked((byte)Interlocked.Increment(ref _sid));
        byte[] request = LegacyFinsWire.MakeRequest(write, address, values, sid, _nodes);
        ImmutableArray<byte> actualRequest = ImmutableArray.CreateRange(request);
        var emptyReply = ImmutableArray<byte>.Empty;
        void Emit(LegacyFinsObservationStage stage,
            ImmutableArray<byte> actualReply = default, string? detail = null,
            DateTimeOffset? occurredAt = null)
        {
            onObservation(new LegacyFinsDatagramObservation(
                operationId, sid, address, write, stage, occurredAt ?? DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp(), actualRequest,
                actualReply.IsDefault ? emptyReply : actualReply, detail));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var prepared = DateTimeOffset.UtcNow;
        // A1 persists this exact timestamp in the pre-send proof and demands
        // byte-for-byte identical SentUtc on its later acknowledged proof.
        Emit(LegacyFinsObservationStage.Prepared, occurredAt: prepared);
        try
        {
            await udp.SendAsync(request, _endpoint, budget.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            Emit(LegacyFinsObservationStage.TransportFailure, detail: "UDP_SEND_" + ex.GetType().Name);
            throw;
        }
        Emit(LegacyFinsObservationStage.WireSent);

        UdpReceiveResult received;
        try
        {
            received = await udp.ReceiveAsync(budget.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            // No received datagram => no ReplyReceived or AckAccepted, EVER.
            Emit(LegacyFinsObservationStage.TransportFailure,
                detail: "UDP_RECEIVE_" + ex.GetType().Name);
            throw;
        }

        // FINS DM read/write replies are at most 142 bytes by profile. Preserve
        // whole observed packets up to 4096; never silently truncate forged/invalid
        // oversized traffic and then call it accepted.
        if (received.Buffer.Length > 4096)
        {
            Emit(LegacyFinsObservationStage.TransportFailure,
                detail: "UDP_REPLY_OVERSIZED_UNRECORDED");
            throw new InvalidDataException("FINS UDP response exceeds bounded raw observation limit.");
        }

        ImmutableArray<byte> actualReply = ImmutableArray.CreateRange(received.Buffer);
        Emit(LegacyFinsObservationStage.ReplyReceived, actualReply);
        ushort[] decoded;
        try
        {
            if (!received.RemoteEndPoint.Equals(_endpoint))
                throw new InvalidDataException("Unexpected loopback FINS responder.");
            decoded = LegacyFinsWire.DecodeResponse(request, received.Buffer, _nodes);
        }
        catch (InvalidDataException ex)
        {
            Emit(LegacyFinsObservationStage.AckRejected, actualReply, ex.Message);
            throw;
        }

        var acknowledgedAt = DateTimeOffset.UtcNow;
        Emit(LegacyFinsObservationStage.AckAccepted, actualReply);
        if (!write)
            Emit(LegacyFinsObservationStage.ReadbackVerified, actualReply);
        return (decoded, sid, prepared, acknowledgedAt);
    }

    private async Task<ushort[]> ExecuteAsync(bool write, ushort address, ushort[] values,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        var request = LegacyFinsWire.MakeRequest(write, address, values,
            unchecked((byte)Interlocked.Increment(ref _sid)), _nodes);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(request, _endpoint, budget.Token);
        var reply = await udp.ReceiveAsync(budget.Token);
        if (!reply.RemoteEndPoint.Equals(_endpoint))
            throw new InvalidDataException("Unexpected loopback FINS responder.");
        return LegacyFinsWire.DecodeResponse(request, reply.Buffer, _nodes);
    }
}
