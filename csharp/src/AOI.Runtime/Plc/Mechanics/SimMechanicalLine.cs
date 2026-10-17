using AOI.Domain;

namespace AOI.Runtime.Plc.Mechanics;

/// <summary>
/// Physical itinerary used by the SIM_ONLY mechanical twin. Intermediate transfer geometry
/// is not field-verified: these are the stations evidenced by ChannelCatalog plus ST11.
/// Never interpret the itinerary as real PLC addresses or control an OEM machine with it.
/// </summary>
public sealed record SimMechanicalStation(
    string StationId, int Ordinal, string Label, int DwellTicks,
    IReadOnlyList<string> ChannelIds);

public enum SimMechanicalEventKind
{
    Loaded,
    Arrived,
    SensorTriggered,
    CaptureRequested,
    EndScanRequested,
    Departed,
    Ejected,
    SensorStuck,
    SensorRestored
}

/// <summary>
/// Every capture request carries exactly one physical device and all its logical channels:
/// LINE-S-8 => [CH08,CH09], LINE-M-2 => [CH10,CH11] (one parent capture each).
/// Events are simulation-only intentions, NOT successful captures or quality verdicts.
/// </summary>
public sealed record SimMechanicalEvent(
    long Sequence, long Tick, DateTimeOffset VirtualUtc, SimMechanicalEventKind Kind,
    ushort Token, Guid WorkpieceId, string StationId, string? PhysicalDeviceId,
    IReadOnlyList<string> ChannelIds, string? Detail);

public sealed record SimMechanicalPosition(
    ushort Token, Guid WorkpieceId, string Barcode, string StationId, int StationOrdinal,
    int DwellTicksElapsed, bool SensorTriggered, bool SensorStuck,
    bool ReadyForExit, double ProgressFraction);

public sealed record SimMechanicalSnapshot(
    long Tick, DateTimeOffset VirtualUtc, int LoadedCount, int ExitedCount,
    IReadOnlyList<SimMechanicalPosition> Active,
    IReadOnlyList<string> StuckSensors,
    IReadOnlyList<SimMechanicalStation> Stations);

/// <summary>
/// Deterministic multi-piece offline mechanical simulator. One occupant per station,
/// tail-to-head movement, bounded dwell, one sensor event per visit, and exactly one
/// capture request per physical camera (9 devices mapped to 11 channels).
///
/// Call TryLoad immediately after durable registration of the same virtual token and
/// WorkpieceIdentity, AdvanceTicks from the host's simulation clock, and TryEject only
/// after the Core has persisted a SIM_ONLY final decision. No device IO is performed.
/// </summary>
public sealed class SimMechanicalLine
{
    private sealed class Occupant
    {
        public ushort Token { get; }
        public WorkpieceIdentity Item { get; }
        public int StationOrdinal { get; set; }
        public long ArrivalTick { get; set; }
        public long? TriggerTick { get; set; }

        public Occupant(ushort token, WorkpieceIdentity item, int stationOrdinal, long tick)
        {
            Token = token;
            Item = item;
            StationOrdinal = stationOrdinal;
            ArrivalTick = tick;
        }
    }

    private static readonly string[] StationOrder =
        ["LOADING", "ST03", "ST04", "ST05", "ST06", "ST09", "ST10", "ST11"];

    private static readonly string[] StationLabels =
        ["上料定位", "正面极耳/前短边", "反面极耳/左长边", "右长边",
         "后短边", "反面线扫", "正面线扫", "末端扫码与出料"];

    private readonly object _sync = new();
    private readonly Occupant?[] _slots;
    private readonly Dictionary<ushort, Occupant> _active = new();
    private readonly HashSet<ushort> _everLoadedTokens = new();
    private readonly HashSet<Guid> _everLoadedWorkpieces = new();
    private readonly HashSet<string> _stuckSensors = new(StringComparer.Ordinal);
    private readonly Queue<SimMechanicalEvent> _pendingEvents = new();
    private readonly DateTimeOffset _startUtc;
    private readonly TimeSpan _tickDuration;
    private readonly IReadOnlyList<SimMechanicalStation> _stations;
    private long _tick;
    private long _sequence;
    private int _exited;

    public SimMechanicalLine(
        IReadOnlyDictionary<string, int>? dwellTicks = null,
        TimeSpan? tickDuration = null,
        DateTimeOffset? startUtc = null)
    {
        _tickDuration = tickDuration ?? TimeSpan.FromMilliseconds(100);
        if (_tickDuration <= TimeSpan.Zero || _tickDuration > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(tickDuration));

        if (dwellTicks is not null && dwellTicks.Keys.Any(x =>
                !StationOrder.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown simulated station dwell override.", nameof(dwellTicks));

        var stations = new List<SimMechanicalStation>();
        for (int i = 0; i < StationOrder.Length; i++)
        {
            string id = StationOrder[i];
            int dwell = dwellTicks is not null && dwellTicks.TryGetValue(id, out int overridden)
                ? overridden : id is "ST09" or "ST10" ? 3 : 2;
            if (dwell is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(dwellTicks),
                    "Station dwell must be 1..1000 simulation ticks.");
            var channels = ChannelCatalog.All.Where(x => x.StationId == id)
                .Select(x => x.ChannelId).ToArray();
            if (id == "ST11" && channels.Length != 0)
                throw new InvalidOperationException("ST11 must be an independent end scanner.");
            stations.Add(new SimMechanicalStation(id, i, StationLabels[i], dwell, channels));
        }

        var observed = stations.SelectMany(s => s.ChannelIds)
            .OrderBy(x => x, StringComparer.Ordinal);
        var expected = ChannelCatalog.All.Select(x => x.ChannelId)
            .OrderBy(x => x, StringComparer.Ordinal);
        if (!observed.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Mechanical itinerary does not cover 11 known AOI channels.");

        _stations = Array.AsReadOnly(stations.ToArray());
        _slots = new Occupant[stations.Count];
        _startUtc = startUtc ?? DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<SimMechanicalStation> Stations => _stations;

    public bool CanLoad { get { lock (_sync) return _slots[0] is null; } }
    public int ActiveCount { get { lock (_sync) return _active.Count; } }
    public long TickCount { get { lock (_sync) return _tick; } }

    /// <summary>
    /// Returns false only when the LOADING station is occupied. Duplicate identities/tokens
    /// are programming errors, not a retry signal, and throw even if the original item exited.
    /// </summary>
    public bool TryLoad(ushort token, WorkpieceIdentity item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        if (token == 0) throw new ArgumentOutOfRangeException(nameof(token));
        lock (_sync)
        {
            if (_everLoadedTokens.Contains(token) || _everLoadedWorkpieces.Contains(item.WorkpieceId))
                throw new InvalidOperationException("SIM_MECHANICAL_DUPLICATE_IDENTITY_OR_TOKEN");
            if (_slots[0] is not null) return false;
            var cell = new Occupant(token, item, 0, _tick);
            _slots[0] = cell;
            _active.Add(token, cell);
            _everLoadedTokens.Add(token);
            _everLoadedWorkpieces.Add(item.WorkpieceId);
            Emit(SimMechanicalEventKind.Loaded, cell, "LOADING");
            Emit(SimMechanicalEventKind.Arrived, cell, "LOADING");
            return true;
        }
    }

    /// <summary>
    /// Sensor stuck prevents sensor triggering AND departure from this station.
    /// Any upstream station backs up naturally; no token jumps over an occupied slot.
    /// </summary>
    public void SetSensorStuck(string stationId, bool stuck)
    {
        if (string.IsNullOrWhiteSpace(stationId))
            throw new ArgumentException("Station is required.", nameof(stationId));
        lock (_sync)
        {
            int index = Array.FindIndex(StationOrder, s =>
                string.Equals(s, stationId, StringComparison.Ordinal));
            if (index < 0) throw new ArgumentException("Unknown mechanical station.", nameof(stationId));
            bool changed = stuck ? _stuckSensors.Add(stationId) : _stuckSensors.Remove(stationId);
            if (changed)
                Emit(stuck ? SimMechanicalEventKind.SensorStuck
                           : SimMechanicalEventKind.SensorRestored,
                    _slots[index], stationId,
                    detail: "SIM_ONLY_SENSOR_FAULT");
        }
    }

    /// <summary>One discrete 100-ms (default) mechanical cycle. No timers or OS device writes.</summary>
    public void AdvanceTicks(int count = 1)
    {
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_sync)
        {
            for (int n = 0; n < count; n++)
            {
                _tick = checked(_tick + 1);
                // Tail first: each object can advance at most ONE real station per tick.
                // An upstream part can enter a freshly vacated downstream position.
                for (int index = _slots.Length - 1; index >= 0; index--)
                {
                    Occupant? cell = _slots[index];
                    if (cell is null) continue;
                    var station = _stations[index];
                    if (_stuckSensors.Contains(station.StationId)) continue;

                    if (cell.TriggerTick is null)
                    {
                        if (_tick - cell.ArrivalTick >= station.DwellTicks)
                        {
                            cell.TriggerTick = _tick;
                            Emit(SimMechanicalEventKind.SensorTriggered, cell, station.StationId);
                            if (index == _slots.Length - 1)
                            {
                                Emit(SimMechanicalEventKind.EndScanRequested, cell, station.StationId,
                                    detail: "INDEPENDENT_SIM_END_SCAN_REQUIRED");
                            }
                            else
                            {
                                foreach (var physical in station.ChannelIds
                                    .Select(ChannelCatalog.Require)
                                    .GroupBy(x => x.PhysicalDeviceId, StringComparer.Ordinal))
                                {
                                    Emit(SimMechanicalEventKind.CaptureRequested, cell, station.StationId,
                                        physical.Key,
                                        physical.Select(x => x.ChannelId).ToArray());
                                }
                            }
                        }
                    }
                    else if (index < _slots.Length - 1 && _slots[index + 1] is null &&
                             _tick > cell.TriggerTick.Value)
                    {
                        Emit(SimMechanicalEventKind.Departed, cell, station.StationId);
                        _slots[index] = null;
                        _slots[index + 1] = cell;
                        cell.StationOrdinal = index + 1;
                        cell.ArrivalTick = _tick;
                        cell.TriggerTick = null;
                        Emit(SimMechanicalEventKind.Arrived, cell, _stations[index + 1].StationId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A persisted simulation result must be supplied by the caller. This only releases the
    /// physical SIM token at ST11, AFTER its end-scan sensor fired. HOLD never ejects.
    /// </summary>
    public bool TryEject(ushort token, FinalDisposition final)
    {
        if (final is not (FinalDisposition.Good or FinalDisposition.Reject))
            throw new ArgumentException("A final GOOD or REJECT disposition is required.", nameof(final));
        lock (_sync)
        {
            if (!_active.TryGetValue(token, out var cell) ||
                cell.StationOrdinal != _slots.Length - 1 ||
                cell.TriggerTick is null ||
                _stuckSensors.Contains(_stations[^1].StationId))
                return false;
            _slots[^1] = null;
            _active.Remove(token);
            _exited++;
            Emit(SimMechanicalEventKind.Ejected, cell, _stations[^1].StationId,
                detail: "SIM_ONLY_" + final.ToString().ToUpperInvariant());
            return true;
        }
    }

    public SimMechanicalPosition? FindPosition(Guid workpieceId)
    {
        lock (_sync)
        {
            Occupant? cell = _active.Values.FirstOrDefault(x => x.Item.WorkpieceId == workpieceId);
            return cell is null ? null : Position(cell);
        }
    }

    public SimMechanicalSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new SimMechanicalSnapshot(_tick, VirtualUtc,
                _everLoadedTokens.Count, _exited,
                _slots.OfType<Occupant>().Select(Position).ToArray(),
                _stuckSensors.Order(StringComparer.Ordinal).ToArray(),
                _stations);
        }
    }

    /// <summary>Drains exactly-once virtual events; caller persists/publishes them as needed.</summary>
    public IReadOnlyList<SimMechanicalEvent> DrainEvents()
    {
        lock (_sync)
        {
            var result = _pendingEvents.ToArray();
            _pendingEvents.Clear();
            return result;
        }
    }

    private DateTimeOffset VirtualUtc => _startUtc.AddTicks(checked(_tick * _tickDuration.Ticks));

    private SimMechanicalPosition Position(Occupant cell)
    {
        string id = _stations[cell.StationOrdinal].StationId;
        return new SimMechanicalPosition(
            cell.Token, cell.Item.WorkpieceId, cell.Item.Barcode, id,
            cell.StationOrdinal,
            (int)Math.Min(int.MaxValue, _tick - cell.ArrivalTick),
            cell.TriggerTick.HasValue, _stuckSensors.Contains(id),
            cell.StationOrdinal == _slots.Length - 1 && cell.TriggerTick.HasValue &&
                !_stuckSensors.Contains(id),
            (double)cell.StationOrdinal / (_slots.Length - 1));
    }

    private void Emit(SimMechanicalEventKind kind, Occupant? cell, string stationId,
        string? physicalDevice = null, IReadOnlyList<string>? channels = null,
        string? detail = null)
    {
        _pendingEvents.Enqueue(new SimMechanicalEvent(
            ++_sequence, _tick, VirtualUtc, kind, cell?.Token ?? (ushort)0,
            cell?.Item.WorkpieceId ?? Guid.Empty, stationId, physicalDevice,
            channels ?? Array.Empty<string>(), detail));
    }
}
