using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Runtime.Plc.Mechanics;

namespace AOI.Host;

/// <summary>
/// A0 bridge: Agent02 deterministic sensor/motion events drive Agent01
/// immutable per-workpiece station progress. Existing PLC and camera capture
/// remain separate: virtual CaptureRequested is NOT asserted as real hardware.
/// The upstream SIM_FULL fixture currently pre-acquires pixel evidence; this
/// bridge only marks a channel/station complete when its sensor actually fires.
/// </summary>
internal sealed class SimProductionBridge
{
    private readonly SimMechanicalLine _mechanics = new();
    private readonly IProductionFlowCoordinator _flow;

    public SimProductionBridge(string masterNodeFolder)
    {
        _flow = new ProductionFlowCoordinator(
            Path.Combine(masterNodeFolder, "production-progress.db"));
        var sensor = Environment.GetEnvironmentVariable("AOI_SIM_STUCK_SENSOR");
        if (!string.IsNullOrWhiteSpace(sensor))
            _mechanics.SetSensorStuck(sensor, true);
    }

    public bool CanLoad => _mechanics.CanLoad;

    public void Register(ushort token, WorkpieceIdentity item)
    {
        if (!_mechanics.CanLoad)
            throw new InvalidOperationException("SIM_MECHANICAL_LOADING_OCCUPIED");
        _flow.Register(item);
        if (!_mechanics.TryLoad(token, item))
            throw new InvalidOperationException("SIM_MECHANICAL_LOAD_RACE");
        Console.WriteLine($"SIM_BRIDGE_LOAD token={token} id={item.WorkpieceId}");
    }

    public IReadOnlyList<SimMechanicalEvent> Advance(ProductionStore store)
    {
        _mechanics.AdvanceTicks();
        var positions = _mechanics.Snapshot().Active;
        foreach (var position in positions)
        {
            var recorded = store.ResultsFor(position.WorkpieceId);
            foreach (var result in recorded)
                _flow.RecordChannel(result); // true WAL evidence, never barcode match
            // An arrived but untriggered sensor is not a completed station. All
            // prior stations must have fired before a physical part moved on.
            int maxCompleted = Math.Min(ProductionFlowCoordinator.StationOrder.Count - 1,
                position.StationOrdinal - (position.SensorTriggered ? 0 : 1));
            for (int index = 0; index <= maxCompleted; index++)
            {
                var stationId = ProductionFlowCoordinator.StationOrder[index];
                var state = _flow.Get(position.WorkpieceId);
                var station = state.Stations[index];
                if (!station.HasEntered)
                    _flow.EnterStation(position.WorkpieceId, stationId);
                if (!station.IsCompleted)
                {
                    if (station.ExpectedChannels.Any(channel =>
                        !state.CapturedChannels.Contains(channel, StringComparer.Ordinal)))
                        break; // wait for real Slave PeerLink result, not pretend capture
                    _flow.CompleteStation(position.WorkpieceId, stationId);
                    Console.WriteLine($"SIM_BRIDGE_STATION_COMPLETE id={position.WorkpieceId} " +
                        $"station={stationId} channels={station.ExpectedChannels.Count}");
                }
            }
        }
        var observedEvents = _mechanics.DrainEvents();
        foreach (var evt in observedEvents)
        {
            if (evt.Kind is SimMechanicalEventKind.SensorTriggered or
                SimMechanicalEventKind.CaptureRequested or
                SimMechanicalEventKind.EndScanRequested or
                SimMechanicalEventKind.Ejected or
                SimMechanicalEventKind.SensorStuck)
                Console.WriteLine($"SIM_BRIDGE_MECHANICAL event={evt.Kind} id={evt.WorkpieceId} " +
                    $"station={evt.StationId} device={evt.PhysicalDeviceId} tick={evt.Tick}");
        }
        return observedEvents;
    }

    public bool ReadyToScan(ushort token, Guid workpieceId)
    {
        var position = _mechanics.Snapshot().Active.SingleOrDefault(p =>
            p.Token == token && p.WorkpieceId == workpieceId);
        if (position is not { ReadyForExit: true }) return false;
        var state = _flow.Get(workpieceId);
        return state.CompletedStationCount == 7 &&
               state.CapturedChannelCount == ChannelCatalog.All.Count;
    }

    public FinalDecision? ObserveAndEvaluate(Guid workpieceId, string endBarcode)
    {
        _flow.ObserveEndBarcode(workpieceId, endBarcode);
        return _flow.EvaluateFinal(workpieceId);
    }

    public void FinalPersisted(Guid workpieceId, FinalDecision decision)
        => _flow.FinalPersisted(workpieceId, decision);

    public void ConfirmEjection(ushort token, Guid workpieceId, FinalDisposition disposition)
    {
        if (!_mechanics.TryEject(token, disposition))
            throw new InvalidOperationException("SIM_MECHANICAL_TOKEN_EXIT_MISMATCH");
        _flow.EjectionConfirmed(workpieceId);
        Console.WriteLine($"SIM_BRIDGE_EJECTED token={token} id={workpieceId}");
    }

    public SimConveyorOverview Snapshot()
    {
        var mechanics = _mechanics.Snapshot();
        var overview = new SimConveyorOverview(mechanics.Tick, mechanics.LoadedCount,
            mechanics.ExitedCount,
            mechanics.Active.Select(x =>
            {
                var flow = _flow.Get(x.WorkpieceId);
                return new SimConveyorItem(x.WorkpieceId, x.Token, x.Barcode, x.StationId,
                    x.SensorTriggered, flow.CapturedChannelCount,
                    flow.CompletedStationCount, flow.Phase, x.ReadyForExit);
            }).ToArray(), mechanics.StuckSensors);
        overview.Validate();
        return overview;
    }
}
