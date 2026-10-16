using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AOI.Runtime;

/// <summary>
/// Dedicated offline mechanical-queue simulator. D4000-D4009 are PRIVATE test
/// registers, not mappings to the real machine. Cannot connect to non-loopback.
/// </summary>
public sealed class SimFullVirtualPlc
{
    public const ushort LoadTokenAddress = 4000;   // read: next virtual load token
    public const ushort LoadAckAddress = 4001;     // write: token, exact once
    public const ushort ExitTokenAddress = 4002;   // read: oldest in-flight token
    public const ushort ExitDecisionAddress = 4003;// write [token, 1=GOOD/2=NG]
    public const ushort ExitedCountAddress = 4005; // read: completed ejections
    public const ushort ActiveCountAddress = 4006; // read: tokens in conveyor
    public const ushort EndBarcodeAddress = 2020;  // read 10 words, independent fixture station

    private readonly ushort[] _words = new ushort[5000];
    private readonly Queue<ushort> _inFlight = new();
    private readonly object _gate = new();
    private readonly int _itemCount;
    private readonly int _maxInFlight;
    private readonly int? _flipEndBarcodeToken;
    private readonly bool _repeatBarcodes;
    private readonly uint _bootNonce = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    public uint BootNonce => _bootNonce;
    private int _issued;
    private int _exited;
    private ushort _offered;
    public int MaxObservedInFlight { get; private set; }
    public int ExitedCount { get { lock (_gate) return _exited; } }
    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }
    public IReadOnlyList<(ushort Token, ushort Decision)> Completed
    {
        get { lock (_gate) return _completed.ToArray(); }
    }
    private readonly List<(ushort Token, ushort Decision)> _completed = new();

    public SimFullVirtualPlc(int itemCount, int maxInFlight = 3,
        int? flipEndBarcodeToken = null, bool repeatBarcodes = false)
    {
        if (itemCount is < 2 or > 128 || maxInFlight is < 2 or > 8 ||
            (flipEndBarcodeToken.HasValue &&
             (flipEndBarcodeToken.Value < 1 || flipEndBarcodeToken.Value > itemCount)))
            throw new ArgumentOutOfRangeException(nameof(itemCount),
                "SIM_FULL supports 2..128 virtual items, 2..8 slots and an in-range fault token.");
        _itemCount = itemCount;
        _maxInFlight = maxInFlight;
        _flipEndBarcodeToken = flipEndBarcodeToken;
        _repeatBarcodes = repeatBarcodes;
        Refresh();
    }

    public static string Barcode(ushort token, bool repeats = false) =>
        repeats ? "SIM-DUPLICATE" : $"SIM-{token:0000}";

    private void WriteBarcode(int address, string barcode)
    {
        var bytes = Encoding.ASCII.GetBytes(barcode);
        if (bytes.Length > 20) throw new InvalidDataException("SIM barcode exceeds 20 ASCII bytes.");
        for (int i = 0; i < 10; i++)
            _words[address + i] = (ushort)(((i * 2 < bytes.Length ? bytes[i * 2] : 0) << 8) |
                (i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : 0));
    }

    private void Refresh()
    {
        if (_offered == 0 && _issued < _itemCount && _inFlight.Count < _maxInFlight)
        {
            _offered = checked((ushort)++_issued);
            WriteBarcode(2000, Barcode(_offered, _repeatBarcodes));
        }
        _words[4007] = (ushort)(_bootNonce >> 16);
        _words[4008] = (ushort)_bootNonce;
        _words[LoadTokenAddress] = _offered;
        _words[ActiveCountAddress] = checked((ushort)_inFlight.Count);
        _words[ExitedCountAddress] = checked((ushort)_exited);
        if (_inFlight.TryPeek(out ushort token))
        {
            _words[ExitTokenAddress] = token;
            WriteBarcode(EndBarcodeAddress,
                _flipEndBarcodeToken == token ? "SIM-BAD-END" : Barcode(token, _repeatBarcodes));
        }
        else
        {
            _words[ExitTokenAddress] = 0;
            WriteBarcode(EndBarcodeAddress, "");
        }
    }

    /// <summary>Atomic simulator-only token handshakes, denies arbitrary DM writes.</summary>
    public byte[] Handle(ReadOnlySpan<byte> request)
    {
        lock (_gate)
        {
            if (request.Length < 18) return [];
            bool write = request[10] == 1 && request[11] == 2;
            ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(13, 2));
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(16, 2));
            ushort acceptedToken = 0;
            ushort acceptedDecision = 0;
            if (write)
            {
                if (request.Length != 18 + count * 2) return [];
                if (address == LoadAckAddress && count == 1)
                {
                    acceptedToken = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(18, 2));
                    if (acceptedToken == 0 || acceptedToken != _offered ||
                        _inFlight.Count >= _maxInFlight) return [];
                }
                else if (address == ExitDecisionAddress && count == 2)
                {
                    acceptedToken = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(18, 2));
                    acceptedDecision = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(20, 2));
                    if (!_inFlight.TryPeek(out var front) || acceptedToken != front ||
                        acceptedDecision is not (1 or 2)) return [];
                }
                else return []; // no unrestricted FINS writes, not even to legacy output D1016
            }
            byte[] response = FinsDm.SimulateResponse(request, _words);
            if (response.Length == 0) return [];
            if (write && address == LoadAckAddress)
            {
                _inFlight.Enqueue(acceptedToken);
                _offered = 0;
                MaxObservedInFlight = Math.Max(MaxObservedInFlight, _inFlight.Count);
                Console.WriteLine($"SIM_FULL_VPLC_LOAD token={acceptedToken} active={_inFlight.Count}");
            }
            else if (write && address == ExitDecisionAddress)
            {
                _inFlight.Dequeue();
                _completed.Add((acceptedToken, acceptedDecision));
                _exited++;
                // Legacy output mirrors exist ONLY INSIDE THIS VIRTUAL MEMORY.
                _words[1016] = acceptedDecision;
                _words[2099] = 2;
                Console.WriteLine($"SIM_FULL_VPLC_EXIT token={acceptedToken} decision={acceptedDecision} completed={_exited}");
            }
            Refresh();
            return response;
        }
    }

    public ushort ReadForTest(ushort address) { lock (_gate) return _words[address]; }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, FinsDm.SimPort));
        Console.WriteLine($"SIM_FULL_VPLC_LISTEN 127.0.0.1:{FinsDm.SimPort} items={_itemCount} " +
            $"max_inflight={_maxInFlight} REAL_DISABLED");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (!IPAddress.IsLoopback(received.RemoteEndPoint.Address)) continue;
            byte[] response = Handle(received.Buffer);
            if (response.Length > 0)
                await udp.SendAsync(response, received.RemoteEndPoint, ct);
        }
    }
}
