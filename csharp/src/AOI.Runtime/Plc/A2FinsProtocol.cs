using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Runtime.Plc;

/// <summary>Simulation boundary: no production endpoint is representable by this gateway.</summary>
public sealed class A2WritePolicy
{
    public bool ProductionWritesEnabled => false;
    public static void DenyRealHardware(string? endpoint = null) =>
        throw new UnauthorizedAccessException("REAL_PLC_IO_DENIED: FG-01..FG-14 remain pending.");
    public static IPEndPoint RequireLoopback(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip))
            throw new UnauthorizedAccessException("SIM_ONLY_IP_LITERAL_LOOPBACK_REQUIRED");
        if (ip.AddressFamily != AddressFamily.InterNetwork || port is < 1024 or > 65535)
            throw new UnauthorizedAccessException("SIM_ONLY_IPV4_PORT_DENIED");
        return new IPEndPoint(ip, port);
    }
    public void AuthorizeVirtualWrite(ushort address, int words)
    {
        if (words is < 1 or > 64 || address + words > 5000)
            throw new ArgumentOutOfRangeException(nameof(words));
        // Physical field conflicts remain disallowed even in the virtual adapter by default.
        if (Enumerable.Range(address, words).Any(n => A2SignalRegistry.AtWord((ushort)n)
                .Any(signal => signal.ValidationState == "BLOCKED_CONFLICT")))
            throw new UnauthorizedAccessException("PLC_ADDRESS_FIELD_CONFLICT_DENIED");
    }
}

/// <summary>Strict FINS/UDP DM request/response with explicit SID/node validation.</summary>
public static class A2FinsProtocol
{
    public const byte MemoryAreaDmWord = 0x82;
    public static byte[] Create(bool write, ushort firstWord, ReadOnlySpan<ushort> values, byte sid)
    {
        if (values.Length is < 1 or > 64 || firstWord + values.Length > 5000)
            throw new ArgumentOutOfRangeException(nameof(values));
        var request = new byte[18 + (write ? values.Length * 2 : 0)];
        request[0] = 0x80; request[2] = 0x02;
        request[4] = 1; request[7] = 99; request[9] = sid;
        request[10] = 1; request[11] = write ? (byte)2 : (byte)1;
        request[12] = MemoryAreaDmWord;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(13, 2), firstWord);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(16, 2), (ushort)values.Length);
        if (write)
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(18 + i * 2, 2), values[i]);
        return request;
    }

    public static byte[] ExecuteVirtual(ReadOnlySpan<byte> request, ushort[] dm)
    {
        if (request.Length < 18 || request[0] != 0x80 || request[1] != 0 ||
            request[2] != 2 || request[3] != 0 || request[5] != 0 ||
            request[6] != 0 || request[8] != 0 || request[10] != 1 ||
            request[11] is not (1 or 2) || request[12] != MemoryAreaDmWord ||
            request[15] != 0) return [];
        var start = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(13, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(16, 2));
        bool write = request[11] == 2;
        if (count is < 1 or > 64 || start + count > dm.Length ||
            request.Length != 18 + (write ? count * 2 : 0)) return [];
        var response = new byte[14 + (write ? 0 : count * 2)];
        request[..12].CopyTo(response);
        response[0] = 0xC0;
        response[4] = request[7]; response[7] = request[4];
        if (write)
            for (int i = 0; i < count; i++)
                dm[start + i] = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(18 + i * 2, 2));
        else
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(14 + i * 2, 2), dm[start + i]);
        return response;
    }

    public static ushort[] Decode(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (request.Length < 18) throw new InvalidDataException("FINS_REQUEST_TRUNCATED");
        int count = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(16, 2));
        bool write = request[11] == 2;
        if (request[10] != 1 || request[11] is not (1 or 2) ||
            response.Length != 14 + (write ? 0 : count * 2) ||
            response[0] != 0xC0 || response[1] != request[1] ||
            response[2] != request[2] || response[3] != request[3] ||
            response[4] != request[7] || response[7] != request[4] ||
            response[9] != request[9] || response[10] != request[10] ||
            response[11] != request[11] || response[12] != 0 || response[13] != 0)
            throw new InvalidDataException("FINS_HEADER_SID_NODE_LENGTH_OR_END_CODE_INVALID");
        var result = new ushort[write ? 0 : count];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(14 + 2 * i, 2));
        return result;
    }

    /// <summary>Float32 BADC: IEEE754 ABCD bytes rearranged B,A,D,C on wire.</summary>
    public static ushort[] EncodeFloatBadc(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        int bits = BitConverter.SingleToInt32Bits(value);
        return [(ushort)((((bits >> 16) & 255) << 8) | ((bits >> 24) & 255)),
                (ushort)(((bits & 255) << 8) | ((bits >> 8) & 255))];
    }
    public static float DecodeFloatBadc(ReadOnlySpan<ushort> words)
    {
        if (words.Length != 2) throw new ArgumentException("FLOAT32_BADC_NEEDS_TWO_WORDS");
        uint bits = ((uint)(words[0] & 255) << 24) |
                    ((uint)(words[0] >> 8) << 16) |
                    ((uint)(words[1] & 255) << 8) | (uint)(words[1] >> 8);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }
    public static ushort[] EncodeBaAscii(string value, int wordCount)
    {
        if (wordCount is < 1 or > 64 || value.Length > wordCount * 2 || value.Any(c => c > 127))
            throw new ArgumentException("BA_ASCII_INVALID");
        var bytes = Encoding.ASCII.GetBytes(value);
        var result = new ushort[wordCount];
        for (int i = 0; i < bytes.Length; i++)
            result[i / 2] |= (ushort)(i % 2 == 0 ? bytes[i] << 8 : bytes[i]);
        return result;
    }
    public static string DecodeBaAscii(ReadOnlySpan<ushort> words)
    {
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), words[i]);
        int end = Array.FindIndex(bytes, x => x is 0 or 13);
        if (end < 0) end = bytes.Length;
        if (bytes.AsSpan(0, end).IndexOfAnyInRange((byte)128, byte.MaxValue) >= 0)
            throw new InvalidDataException("NON_ASCII_BARCODE");
        return Encoding.ASCII.GetString(bytes, 0, end).TrimEnd(' ');
    }
}

public sealed class A2VirtualPlc
{
    private readonly ushort[] _dm = new ushort[5000];
    private readonly object _gate = new();
    private readonly A2WritePolicy _policy = new();
    public A2VirtualPlc(string barcode = "SIM-0001")
    {
        var words = A2FinsProtocol.EncodeBaAscii(barcode, 10);
        words.CopyTo(_dm, 2000);
        _dm[1999] = 1;
    }
    public ushort Read(ushort address) { lock (_gate) return _dm[address]; }
    public byte[] Handle(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (frame.Length < 18) return [];
            if (frame[11] == 2)
            {
                var start = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(13, 2));
                var count = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16, 2));
                try { _policy.AuthorizeVirtualWrite(start, count); }
                catch (Exception e) when (e is UnauthorizedAccessException or ArgumentOutOfRangeException) { return []; }
            }
            return A2FinsProtocol.ExecuteVirtual(frame, _dm);
        }
    }
}
