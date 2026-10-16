using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Runtime;

/// <summary>Small real FINS/UDP DM-word slice for the offline vPLC; NOT a full production FINS driver.</summary>
public static class FinsDm
{
    public const int SimPort = 19600;
    private const byte DmMemoryArea = 0x82;

    public static byte[] Request(bool write, ushort address, ReadOnlySpan<ushort> words, byte sequence)
    {
        var packet = new byte[18 + (write ? words.Length * 2 : 0)];
        packet[0] = 0x80; packet[2] = 0x02; packet[4] = 1; packet[7] = 99; packet[9] = sequence;
        packet[10] = 0x01; packet[11] = write ? (byte)0x02 : (byte)0x01;
        packet[12] = DmMemoryArea;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(13, 2), address);
        packet[15] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), checked((ushort)words.Length));
        if (write)
            for (var i = 0; i < words.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18 + i * 2, 2), words[i]);
        return packet;
    }

    public static byte[] SimulateResponse(ReadOnlySpan<byte> request, ushort[] memory)
    {
        if (request.Length < 18 || request[0] != 0x80 || request[10] != 1 ||
            (request[11] != 1 && request[11] != 2) || request[12] != DmMemoryArea ||
            request[15] != 0)
            return []; // Invalid FINS request is not acknowledged as successful.
        var start = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(13, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(16, 2));
        if (length is < 1 or > 64 || start + length > memory.Length ||
            (request[11] == 2 && request.Length != 18 + length * 2) ||
            (request[11] == 1 && request.Length != 18)) return [];
        var write = request[11] == 2;
        var response = new byte[14 + (write ? 0 : length * 2)];
        request[..12].CopyTo(response);
        response[0] = 0xC0;
        response[4] = request[7]; response[7] = request[4]; // destination/source node swap
        // response end-code at offset 12..13 is 0 only after a successful operation
        if (write)
        {
            for (var i = 0; i < length; i++)
                memory[start + i] = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(18 + i * 2, 2));
        }
        else
        {
            for (var i = 0; i < length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(14 + i * 2, 2), memory[start + i]);
        }
        return response;
    }

    public static ushort[] DecodeResponse(byte[] request, ReadOnlySpan<byte> response)
    {
        var expectedWords = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16, 2));
        var write = request[11] == 2;
        if (response.Length != 14 + (write ? 0 : expectedWords * 2) ||
            response[0] != 0xC0 || response[9] != request[9] ||
            response[10] != 1 || response[11] != request[11] ||
            response[12] != 0 || response[13] != 0)
            throw new InvalidDataException("FINS packet, SID or response end-code invalid.");
        if (write) return [];
        var words = new ushort[expectedWords];
        for (var i = 0; i < expectedWords; i++)
            words[i] = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(14 + i * 2, 2));
        return words;
    }

    public static string SimBarcode(ReadOnlySpan<ushort> words)
    {
        var chars = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(chars.AsSpan(i * 2, 2), words[i]);
        return Encoding.ASCII.GetString(chars).TrimEnd('\0', ' ');
    }
}

public sealed class VirtualPlc
{
    private readonly ushort[] _memory = new ushort[5000];
    private readonly object _gate = new();

    public VirtualPlc(string testBarcode = "SIM-0001")
    {
        var raw = Encoding.ASCII.GetBytes(testBarcode);
        if (raw.Length > 20) throw new ArgumentException("SIM barcode exceeds 10 DM words.");
        for (var i = 0; i < 20; i += 2)
        {
            var first = i < raw.Length ? raw[i] : (byte)0;
            var second = i + 1 < raw.Length ? raw[i + 1] : (byte)0;
            _memory[2000 + i / 2] = (ushort)((first << 8) | second);
        }
        _memory[1999] = 1; // PLC requests a simulated load cycle.
    }

    public ushort ReadForTest(ushort address)
    {
        lock (_gate) return _memory[address];
    }

    public byte[] Handle(ReadOnlySpan<byte> request)
    {
        lock (_gate) return FinsDm.SimulateResponse(request, _memory);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, FinsDm.SimPort));
        Console.WriteLine($"VPLC_FINS_LISTEN 127.0.0.1:{FinsDm.SimPort} SIM_ONLY");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address)) continue;
            byte[] response = Handle(request.Buffer);
            if (response.Length != 0) await udp.SendAsync(response, request.RemoteEndPoint, ct);
        }
    }
}

public sealed class FinsSimClient
{
    private int _sequence;
    // Hard-coded loopback endpoint; there is deliberately no arbitrary target-address parameter.
    public async Task<ushort[]> ReadAsync(ushort dmAddress, ushort count, CancellationToken ct)
        => await ExecuteAsync(false, dmAddress, new ushort[count], ct);

    public async Task WriteAsync(ushort dmAddress, params ushort[] values)
        => _ = await ExecuteAsync(true, dmAddress, values, CancellationToken.None);

    private async Task<ushort[]> ExecuteAsync(bool write, ushort address, ushort[] words, CancellationToken ct)
    {
        if (words.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(words));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var request = FinsDm.Request(write, address, words, unchecked((byte)Interlocked.Increment(ref _sequence)));
        var receiver = new IPEndPoint(IPAddress.Loopback, FinsDm.SimPort);
        await client.SendAsync(request, receiver, timeout.Token);
        var response = await client.ReceiveAsync(timeout.Token);
        if (!response.RemoteEndPoint.Equals(receiver))
            throw new InvalidDataException("Unexpected FINS response source.");
        return FinsDm.DecodeResponse(request, response.Buffer);
    }
}
