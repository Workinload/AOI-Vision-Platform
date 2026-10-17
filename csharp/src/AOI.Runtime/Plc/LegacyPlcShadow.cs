using AOI.Domain;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AOI.Runtime.Plc;

/// <summary>
/// This model represents ONLY observable FINS registers/handshakes of the legacy PLC.
/// Nothing here models axes or makes an OEM hardware connection. D4300/D4302/D4303 are
/// explicitly PRIVATE simulation-injection commands, NOT historic PLC registers.
/// D1013 and the barcode windows are historical, FIELD_UNVERIFIED test profiles only.
/// </summary>
public enum LegacyExitScenario { Normal = 0, BarcodeFailure = 1, BarcodeMismatch = 2 }

public sealed record LegacyCaptureOffer(
    ushort Token, string PhysicalDeviceId, string StationId,
    IReadOnlyList<string> ChannelIds, ushort PlcAddress,
    DateTimeOffset RequestedUtc, DateTimeOffset DeadlineUtc, int AttemptNo,
    bool FieldUnverified);

public sealed record LegacyWireTransaction(
    long Sequence, DateTimeOffset ReceivedUtc, DateTimeOffset ResponsePreparedUtc,
    long MonotonicTimestamp, byte Sid, bool IsWrite,
    ushort Address, ushort[] Values, ushort Token,
    string RequestHex, string ResponseHex, string Outcome);

/// <summary>
/// Owns the shadow register view, reuses the existing SimFullVirtualPlc for D4000 FIFO,
/// and records the exact FINS frames actually handled (not inferred final DM values).
/// All public state operations and UDP packets serialize under the same lock.
/// </summary>
public sealed class LegacyPlcShadow
{
    public const ushort SimOfferCaptureAddress = 4300; // [token, historic camera DM address]
    public const ushort SimOfferLoadAddress = 4302;    // [token], barcode from existing D2000 fixture
    public const ushort SimOfferExitAddress = 4303;    // [token, LegacyExitScenario], from D2020 fixture
    public const ushort LoadRequestAddress = 1999;
    public const ushort FinalQualityAddress = 1016;
    public const ushort EndRequestAddress = 2099;
    public const ushort LoadBarcodeAddress = 2000;
    public const ushort EndBarcodeAddress = 2100;

    private sealed class CameraState(LegacyCaptureOffer offer)
    {
        public LegacyCaptureOffer Offer { get; } = offer;
        public bool Complete { get; set; }
    }
    private sealed class LoadState(ushort token, DateTimeOffset deadline)
    {
        public ushort Token { get; } = token;
        public DateTimeOffset DeadlineUtc { get; } = deadline;
        public bool Complete { get; set; }
    }
    private sealed class EndState(ushort token, LegacyExitScenario scenario, DateTimeOffset deadline)
    {
        public ushort Token { get; } = token;
        public LegacyExitScenario Scenario { get; } = scenario;
        public DateTimeOffset DeadlineUtc { get; } = deadline;
        public int Step { get; set; }
    }

    private readonly object _gate = new();
    private readonly SimFullVirtualPlc _existing;
    private readonly TimeProvider _clock;
    private readonly LegacyPlcTiming _timing;
    private readonly LegacyFinsNodes _nodes;
    private readonly ushort[] _dm = new ushort[5000];
    private readonly Dictionary<ushort, CameraState> _cameras = new();
    private readonly List<LegacyWireTransaction> _wire = new();
    private readonly HashSet<ushort> _dropOnce = new();
    // External end-scanner failure is observable on D2020 AND D2100 for the
    // specified FIFO token. Never change the existing FIFO barcode fixture.
    private readonly HashSet<ushort> _unreadableEndScanTokens;
    private LoadState? _load;
    private EndState? _end;
    private long _sequence;

    private static readonly IReadOnlyDictionary<ushort, ChannelDefinition[]> DevicesByAddress =
        ChannelCatalog.All.GroupBy(x => x.PhysicalDeviceId, StringComparer.Ordinal)
            .ToDictionary(group =>
            {
                string distinct = group.Select(x => x.PlcSignalAddress)
                    .Distinct(StringComparer.Ordinal).Single();
                if (!distinct.StartsWith('D') ||
                    !ushort.TryParse(distinct.AsSpan(1), out ushort address) ||
                    address is < 1006 or > 1014)
                    throw new InvalidOperationException("Unverified camera signal profile.");
                return address;
            }, group => group.ToArray());

    public LegacyPlcShadow(SimFullVirtualPlc existing,
        LegacyFinsNodes? nodes = null, LegacyPlcTiming? timing = null,
        TimeProvider? clock = null, IReadOnlyCollection<ushort>? unreadableEndScanTokens = null)
    {
        _existing = existing ?? throw new ArgumentNullException(nameof(existing));
        // Existing A0 Host constructs the shadow with no new argument. A vPLC-only
        // environment variable is enough to opt in without changing Host/DTO code.
        // Explicit tokens make tests independent of process-wide environment.
        _unreadableEndScanTokens = new HashSet<ushort>(
            unreadableEndScanTokens ?? ReadEndScanFaultTokensFromEnvironment());
        if (_unreadableEndScanTokens.Any(token => token is < 1 or > 128))
            throw new ArgumentOutOfRangeException(nameof(unreadableEndScanTokens));
        _nodes = nodes ?? LegacyFinsNodes.Demo;
        _nodes.Validate();
        _timing = timing ?? LegacyPlcTiming.HistoricalReference;
        _timing.Validate();
        _clock = clock ?? TimeProvider.System;
        if (DevicesByAddress.Count != 9)
            throw new InvalidOperationException("Legacy camera profile must contain 9 physical devices.");
    }

    /// <summary>
    /// Optional SIM_ONLY fixture: AOI_SIM_END_SCAN_FAIL_TOKENS=3 or 3,5.
    /// Never a real PLC register and never a production scanner control.
    /// </summary>
    private static IReadOnlyCollection<ushort> ReadEndScanFaultTokensFromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("AOI_SIM_END_SCAN_FAIL_TOKENS");
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<ushort>();
        var tokens = new List<ushort>();
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!ushort.TryParse(part, out var token) || token is < 1 or > 128)
                throw new InvalidOperationException(
                    "AOI_SIM_END_SCAN_FAIL_TOKENS must be comma-separated tokens 1..128.");
            tokens.Add(token);
        }
        return tokens;
    }

    public LegacyFinsNodes Nodes => _nodes;
    public LegacyPlcTiming Timing => _timing;
    public IReadOnlyList<LegacyWireTransaction> WireHistory
    {
        get { lock (_gate) return _wire.ToArray(); }
    }

    public LegacyCaptureOffer IssueCamera(ushort token, ushort plcAddress, int attemptNo = 1)
    {
        lock (_gate) return IssueCameraLocked(token, plcAddress, attemptNo);
    }

    private LegacyCaptureOffer IssueCameraLocked(ushort token, ushort plcAddress, int attemptNo)
    {
        if (token == 0 || attemptNo is < 1 or > 1000 ||
            !DevicesByAddress.TryGetValue(plcAddress, out var channels))
            throw new ArgumentException("Unknown token/camera request/attempt.");
        if (_cameras.TryGetValue(plcAddress, out var previous) && !previous.Complete)
            throw new InvalidOperationException("CAMERA_STATION_BUSY");
        DateTimeOffset now = _clock.GetUtcNow();
        string device = channels[0].PhysicalDeviceId;
        TimeSpan deadline = channels[0].SharedFrameGroup is null
            ? _timing.AreaCameraDeadline : _timing.LineCameraDeadline;
        var offer = new LegacyCaptureOffer(token, device, channels[0].StationId,
            channels.Select(x => x.ChannelId).ToArray(), plcAddress,
            now, now + deadline, attemptNo,
            FieldUnverified: plcAddress == 1013);
        _cameras[plcAddress] = new CameraState(offer);
        _dm[plcAddress] = 1;
        return offer;
    }

    public LegacyCaptureOffer? CameraAt(ushort address)
    {
        lock (_gate) return _cameras.TryGetValue(address, out var state)
            ? state.Offer : null;
    }

    public void IssueLoadingBarcode(ushort token, string barcode)
    {
        lock (_gate) IssueLoadingBarcodeLocked(token, barcode);
    }

    private void IssueLoadingBarcodeLocked(ushort token, string barcode)
    {
        if (token == 0 || string.IsNullOrWhiteSpace(barcode) ||
            (_load is not null && !_load.Complete))
            throw new InvalidOperationException("BARCODE_STATION_BUSY_OR_INVALID");
        var words = A2FinsProtocol.EncodeBaAscii(barcode, 10);
        Array.Copy(words, 0, _dm, LoadBarcodeAddress, words.Length);
        _load = new LoadState(token, _clock.GetUtcNow() + _timing.LoadBarcodeDeadline);
        _dm[LoadRequestAddress] = 1;
    }

    public void IssueEndScan(ushort token, LegacyExitScenario scenario, string endBarcode)
    {
        lock (_gate) IssueEndScanLocked(token, scenario, endBarcode);
    }

    private void IssueEndScanLocked(ushort token, LegacyExitScenario scenario, string barcode)
    {
        if (token == 0 || !Enum.IsDefined(scenario) ||
            (_end is not null && _end.Step != 2))
            throw new InvalidOperationException("END_STATION_BUSY_OR_INVALID");
        var words = A2FinsProtocol.EncodeBaAscii(barcode, 10);
        Array.Copy(words, 0, _dm, EndBarcodeAddress, words.Length);
        _end = new EndState(token, scenario,
            _clock.GetUtcNow() + _timing.EndResultDeadline);
        _dm[EndRequestAddress] = 1;
        _dm[FinalQualityAddress] = 0;
    }

    /// <summary>Drop the next *incoming* FINS packet for one address (no state mutation).</summary>
    public void SuppressNextPacket(ushort dmAddress)
    {
        lock (_gate) _dropOnce.Add(dmAddress);
    }

    /// <summary>
    /// Accept one actual FINS datagram from a loopback UDP socket. For unowned addresses,
    /// reuse the CURRENT SimFullVirtualPlc FIFO and reframe its response using our route.
    /// This keeps the preexisting D4000 legacy demo working without a second Host.
    /// </summary>
    public byte[] Handle(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (!LegacyFinsWire.TryParseRequest(frame, _nodes, out var op))
                return [];

            DateTimeOffset receivedAt = _clock.GetUtcNow();
            long monotonic = Stopwatch.GetTimestamp();
            ushort token = ResolveToken(op);
            ushort errorCode = 0;
            string outcome = "OK";
            byte[] response;

            if (_dropOnce.Remove(op.Address))
            {
                response = [];
                outcome = "INJECTED_UDP_TIMEOUT";
            }
            else if (IsUnreadableEndBarcodeRead(op))
            {
                // A real FINS/UDP READ sees zero-filled scanner words at D2020
                // for exactly the configured FIFO head. Later tokens are intact.
                response = LegacyFinsWire.MakeResponse(frame, 0, new ushort[op.Count]);
                outcome = "INJECTED_END_SCAN_UNREADABLE";
            }
            else if (!IsOwnedRange(op.Address, op.Count))
            {
                var queueReply = _existing.Handle(frame);
                if (queueReply.Length == 0)
                {
                    errorCode = 5;
                    outcome = "EXISTING_FIFO_REJECTED";
                    response = LegacyFinsWire.MakeResponse(frame, errorCode, []);
                }
                else if (op.IsWrite)
                {
                    response = LegacyFinsWire.MakeResponse(frame, 0, []);
                    outcome = "EXISTING_FIFO_WRITE";
                }
                else
                {
                    var values = new ushort[op.Count];
                    for (int i = 0; i < values.Length; i++)
                        values[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
                            queueReply.AsSpan(14 + i * 2, 2));
                    response = LegacyFinsWire.MakeResponse(frame, 0, values);
                    outcome = "EXISTING_FIFO_READ";
                }
            }
            else
            {
                try
                {
                    if (op.IsWrite)
                        ApplyWriteLocked(op);
                    var payload = op.IsWrite ? Array.Empty<ushort>() :
                        _dm.AsSpan(op.Address, op.Count).ToArray();
                    response = LegacyFinsWire.MakeResponse(frame, 0, payload);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    errorCode = 5; // explicitly simulator-defined reject; not an OEM PLC end-code.
                    outcome = "REJECTED_" + ex.Message;
                    response = LegacyFinsWire.MakeResponse(frame, errorCode, []);
                }
            }

            _wire.Add(new LegacyWireTransaction(++_sequence, receivedAt,
                _clock.GetUtcNow(), monotonic, op.Sid, op.IsWrite, op.Address,
                op.Values, token, Convert.ToHexString(frame),
                Convert.ToHexString(response), outcome));
            return response;
        }
    }

    private bool IsUnreadableEndBarcodeRead(LegacyDmRequest request) =>
        !request.IsWrite &&
        request.Address >= SimFullVirtualPlc.EndBarcodeAddress &&
        request.Address + request.Count <= SimFullVirtualPlc.EndBarcodeAddress + 10 &&
        _unreadableEndScanTokens.Contains(
            _existing.ReadForTest(SimFullVirtualPlc.ExitTokenAddress));

    private static bool IsOwnedWord(int word) =>
        word is >= 1006 and <= 1014 or 1016 or 1999 or 2099
            or >= 2000 and <= 2009 or >= 2100 and <= 2109
            or >= 4300 and <= 4304;

    private static bool IsOwnedRange(ushort address, ushort length)
    {
        for (int offset = 0; offset < length; offset++)
            if (IsOwnedWord(address + offset)) return true;
        return false;
    }

    private ushort ResolveToken(LegacyDmRequest op)
    {
        if (op.IsWrite && op.Address is SimOfferCaptureAddress or
            SimOfferLoadAddress or SimOfferExitAddress && op.Values.Length > 0)
            return op.Values[0];
        if (_cameras.TryGetValue(op.Address, out var camera))
            return camera.Offer.Token;
        if (op.Address is >= 2000 and <= 2009 or LoadRequestAddress)
            return _load?.Token ?? (ushort)0;
        if (op.Address is >= 2100 and <= 2109 or FinalQualityAddress or EndRequestAddress)
            return _end?.Token ?? (ushort)0;
        return 0;
    }

    private void ApplyWriteLocked(LegacyDmRequest op)
    {
        if (op.Address == SimOfferCaptureAddress && op.Count == 2)
        {
            IssueCameraLocked(op.Values[0], op.Values[1], 1);
            return;
        }
        if (op.Address == SimOfferLoadAddress && op.Count == 1)
        {
            var fixture = new ushort[10];
            for (int i = 0; i < fixture.Length; i++)
                fixture[i] = _existing.ReadForTest((ushort)(2000 + i));
            IssueLoadingBarcodeLocked(op.Values[0], A2FinsProtocol.DecodeBaAscii(fixture));
            return;
        }
        if (op.Address == SimOfferExitAddress && op.Count == 2)
        {
            if (op.Values[1] > 2) throw new InvalidOperationException("UNKNOWN_END_CASE");
            // Scanner failure belongs to the active FIFO head, not just a
            // caller-supplied token. This is the external SIM-only observation.
            ushort exitToken = _existing.ReadForTest(SimFullVirtualPlc.ExitTokenAddress);
            if (exitToken != op.Values[0])
                throw new InvalidOperationException("END_SCAN_TOKEN_NOT_FIFO_HEAD");
            bool unreadable = _unreadableEndScanTokens.Contains(exitToken);
            if (unreadable && op.Values[1] != (ushort)LegacyExitScenario.BarcodeFailure)
                throw new InvalidOperationException("UNREADABLE_END_SCAN_REQUIRES_FAILURE_BRANCH");
            var fixture = new ushort[10];
            for (int i = 0; i < fixture.Length; i++)
                fixture[i] = _existing.ReadForTest((ushort)(2020 + i));
            IssueEndScanLocked(op.Values[0], (LegacyExitScenario)op.Values[1],
                unreadable ? "" : A2FinsProtocol.DecodeBaAscii(fixture));
            return;
        }

        if (op.Count != 1)
            throw new InvalidOperationException("MULTI_WORD_WRITE_NOT_ALLOWED_IN_LEGACY_SHADOW");
        ushort value = op.Values[0];
        if (DevicesByAddress.ContainsKey(op.Address))
        {
            if (!_cameras.TryGetValue(op.Address, out var active) ||
                active.Complete || _clock.GetUtcNow() > active.Offer.DeadlineUtc ||
                value is not (2 or 3))
                throw new InvalidOperationException("CAMERA_ACK_NOT_EXPECTED_OR_EXPIRED");
            active.Complete = true;
            _dm[op.Address] = value; // completion 2 does NOT mean image OK.
            return;
        }
        if (op.Address == LoadRequestAddress)
        {
            if (_load is null || _load.Complete ||
                _clock.GetUtcNow() > _load.DeadlineUtc || value is not (2 or 3))
                throw new InvalidOperationException("LOAD_BARCODE_ACK_NOT_EXPECTED");
            _load.Complete = true;
            _dm[LoadRequestAddress] = value;
            return;
        }
        if (op.Address is FinalQualityAddress or EndRequestAddress)
        {
            ApplyEndWriteLocked(op.Address, value);
            return;
        }
        throw new InvalidOperationException("LEGACY_SHADOW_WRITE_FORBIDDEN");
    }

    private void ApplyEndWriteLocked(ushort address, ushort value)
    {
        if (_end is null || _end.Step >= 2 ||
            _clock.GetUtcNow() > _end.DeadlineUtc)
            throw new InvalidOperationException("END_SEQUENCE_NOT_OPEN_OR_EXPIRED");
        bool normal = _end.Scenario == LegacyExitScenario.Normal;
        if (_end.Step == 0)
        {
            if (normal ? address != FinalQualityAddress || value is not (1 or 2)
                : address != EndRequestAddress ||
                  value != (_end.Scenario == LegacyExitScenario.BarcodeFailure ? 3 : 4))
                throw new InvalidOperationException("END_FIRST_WRITE_WRONG_ORDER");
            _dm[address] = value;
            _end.Step = 1;
            return;
        }
        if (normal ? address != EndRequestAddress || value != 2
            : address != FinalQualityAddress || value != 2)
            throw new InvalidOperationException("END_SECOND_WRITE_WRONG_ORDER");
        _dm[address] = value;
        _end.Step = 2;
    }

    public bool EndSequenceComplete
    {
        get { lock (_gate) return _end is not null && _end.Step == 2; }
    }
}

/// <summary>
/// Replace ONLY the vPLC branch's RunAsync with this on the SAME 127.0.0.1 port.
/// Reuses the existing SimFullVirtualPlc instance and FINS/UDP socket, not a second Host.
/// Port 0 exists solely for short-lived parallel NUnit loopback tests.
/// </summary>
public sealed class LegacyPlcShadowUdp
{
    private readonly LegacyPlcShadow _shadow;
    private readonly int _port;
    private readonly TaskCompletionSource<int> _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LegacyPlcShadowUdp(LegacyPlcShadow shadow, int port = FinsDm.SimPort)
    {
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        if (port is < 0 or > 65535 || port is > 0 and < 1024)
            throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
    }

    public Task<int> Listening => _listening.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
        int listeningPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _listening.TrySetResult(listeningPort);
        Console.WriteLine($"LEGACY_PLC_SHADOW_LISTEN 127.0.0.1:{listeningPort} SIM_ONLY");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address)) continue;
            byte[] response = _shadow.Handle(request.Buffer);
            if (response.Length != 0)
                await udp.SendAsync(response, request.RemoteEndPoint, ct);
        }
    }
}
