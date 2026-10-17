using System.Buffers.Binary;
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
    /// Execute a single REAL loopback UDP datagram. onBeforeSend runs BEFORE
    /// SendAsync, so a durable timing event never asserts an ACK prematurely.
    /// An acknowledged proof is returned only after actual SID, route, opcode
    /// and FINS end-code validation. The raw vPLC logs retain the matching SID.
    /// </summary>
    public async Task<LegacyFinsWriteProof> WriteObservedAsync(
        ushort address, ushort value, Action<LegacyFinsWriteProof> onBeforeSend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBeforeSend);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        byte sid = unchecked((byte)Interlocked.Increment(ref _sid));
        var request = LegacyFinsWire.MakeRequest(true, address, [value], sid, _nodes);
        var sent = new LegacyFinsWriteProof(Guid.NewGuid(), sid, address, value,
            DateTimeOffset.UtcNow, AcknowledgedUtc: null, ReplyVerified: false);
        onBeforeSend(sent);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(request, _endpoint, budget.Token);
        var reply = await udp.ReceiveAsync(budget.Token);
        if (!reply.RemoteEndPoint.Equals(_endpoint))
            throw new InvalidDataException("Unexpected loopback FINS responder.");
        _ = LegacyFinsWire.DecodeResponse(request, reply.Buffer, _nodes);
        return sent with { AcknowledgedUtc = DateTimeOffset.UtcNow, ReplyVerified = true };
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
