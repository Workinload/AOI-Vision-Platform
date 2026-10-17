using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Plc;
using AOI.Runtime.Plc.Mechanics;
using System.Net;
using System.Net.Sockets;

namespace AOI.Host;

/// <summary>
/// Opt-in SIM_FULL five-process finite conveyor. Not a real production controller.
/// Each virtual token belongs to one WorkpieceId, independent of its barcode.
/// Fail closed on restart, missing peer evidence, mutated exit barcode, or PLC boot change.
/// </summary>
public static class SimFullHost
{
    public static async Task RunAsync(string role, string dataRoot, CancellationToken ct)
    {
        switch (role)
        {
            case "vplc":
                int count = GetInt("AOI_SIM_ITEM_COUNT", 5, 2, 128);
                int slots = GetInt("AOI_SIM_MAX_IN_FLIGHT", 3, 2, 8);
                int flip = GetInt("AOI_SIM_END_BARCODE_FLIP_TOKEN", 0, 0, count);
                var plc = new SimFullVirtualPlc(count, slots, flip == 0 ? null : flip,
                    Environment.GetEnvironmentVariable("AOI_SIM_REPEAT_BARCODE") == "1");
                if (LegacyShadowHostBridge.Enabled)
                    await LegacyShadowHostBridge.RunPlcAsync(plc, dataRoot, ct);
                else
                    await plc.RunAsync(ct);
                break;
            case "master":
                await RunMasterAsync(dataRoot, ct);
                break;
            case "slave":
                await RunSlaveAsync(dataRoot, ct);
                break;
            default: throw new ArgumentException("Unknown SIM_FULL role.");
        }
    }

    private static int GetInt(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out int value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"SIM_FULL {name} must be {min}..{max}.");
        return value;
    }

    private static string NodeFolder(string root, string role)
    {
        var folder = Path.Combine(root, role);
        if (File.Exists(Path.Combine(folder, "production.db")))
            throw new InvalidOperationException(
                "SIM_FULL_RESTART_HOLD: previous Core WAL found. Manual reconciliation required; " +
                "do not delete production evidence or automatically re-drive eject output.");
        return folder;
    }

    private static async Task RunSlaveAsync(string root, CancellationToken ct)
    {
        const string self = "slave";
        var folder = NodeFolder(root, self);
        var product = SimProductRuntime.Load(root);
        Console.WriteLine($"SIM_FULL_SLAVE_PRODUCT epoch={product.Epoch} release={product.ReleaseId}");
        var store = new ProductionStore(Path.Combine(folder, "production.db"));
        var legacyFins = LegacyShadowHostBridge.Enabled
            ? LegacyShadowHostBridge.NewClient() : null;
        var physical = legacyFins is null ? null :
            new LegacyPhysicalCapture(folder, MachineRole.Slave, store, product, legacyFins);
        var simConfig = new RoiConfigStore(Path.Combine(folder, "sim-config.sqlite"), MachineRole.Slave);
        _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Slave, 19622, ct, "SIM_FULL", product.Overview, simConfig);
        _ = RoiDraftIpc.ServeAsync(simConfig, 19642,
            Environment.GetEnvironmentVariable("AOI_SIM_CONFIG_TOKEN"), ct);
        _ = FramePreviewIpc.ServeAsync(store, MachineRole.Slave, folder, 19632, ct);
        var listener = new TcpListener(IPAddress.Loopback, 19610);
        listener.Start();
        Console.WriteLine("SIM_FULL_SLAVE_PEER_LISTEN 127.0.0.1:19610");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var tcp = await listener.AcceptTcpClientAsync(ct);
                using var session = new PeerSession(tcp);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pump = session.PumpOutboxAsync(store, linked.Token);
                Task? captureWorker = physical is null ? null :
                    RunSlaveCaptureWorkerAsync(store, physical, linked.Token);
                try
                {
                    var read = session.ReadAsync(self, "master", store,
                        (message, duplicate, _) =>
                        {
                            if (message.PayloadType == "capture_trigger" && physical is not null)
                            {
                                // Store.AcceptIncoming already journaled this trigger
                                // in the same transaction BEFORE PeerLink ACK.
                                Console.WriteLine("SIM_SLAVE_TRIGGER_PERSISTED capture=" +
                                    WireFormat.Parse<PhysicalCaptureTrigger>(message.PayloadJson).CaptureId +
                                    " duplicate=" + duplicate);
                                return Task.CompletedTask;
                            }
                            if (message.PayloadType != "workpiece_registered")
                                throw new InvalidDataException("SIM_FULL Slave received unsupported packet.");
                            var item = WireFormat.Parse<WorkpieceIdentity>(message.PayloadJson);
                            product.VerifyWorkpiece(item);
                            if (physical is not null)
                            {
                                Console.WriteLine("SIM_FULL_SLAVE_REGISTERED_FOR_TRIGGER id=" +
                                    item.WorkpieceId);
                                return Task.CompletedTask;
                            }
                            if (!duplicate)
                            {
                                int resultCount = SimElevenChannelFixture.CaptureOwned(item,
                                    MachineRole.Slave, store, folder, product);
                                if (resultCount != 6) throw new InvalidDataException("Missing Slave channel: HOLD.");
                                Console.WriteLine($"SIM_FULL_SLAVE_CAPTURE id={item.WorkpieceId} channels={resultCount}");
                            }
                            else if (store.ResultsFor(item.WorkpieceId).Count != 6)
                                throw new InvalidDataException("Incomplete duplicate registration; manual HOLD.");
                            return Task.CompletedTask;
                        }, linked.Token);
                    if (captureWorker is not null)
                    {
                        var first = await Task.WhenAny(read, captureWorker);
                        if (ReferenceEquals(first, captureWorker))
                        {
                            await captureWorker; // propagate real worker failure
                            throw new InvalidOperationException("Slave capture worker exited unexpectedly.");
                        }
                    }
                    await read;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is IOException or SocketException)
                {
                    Console.Error.WriteLine("SIM_FULL_SLAVE_RECONNECT " + ex.Message);
                }
                finally
                {
                    linked.Cancel();
                    try { await pump; } catch (OperationCanceledException) { }
                    if (captureWorker is not null)
                        try { await captureWorker; }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                }
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task RunMasterAsync(string root, CancellationToken ct)
    {
        const string self = "master";
        var folder = NodeFolder(root, self);
        var product = SimProductRuntime.Load(root);
        Console.WriteLine($"SIM_FULL_MASTER_PRODUCT epoch={product.Epoch} release={product.ReleaseId}");
        // Bind one boot nonce before accepting any virtual load or exit token.
        var fins = new FinsSimClient();
        var legacyFins = LegacyShadowHostBridge.Enabled
            ? LegacyShadowHostBridge.NewClient() : null;
        ushort[] nonceParts;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                nonceParts = await fins.ReadAsync(4007, 2, ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is SocketException or OperationCanceledException)
            {
                Console.WriteLine("SIM_FULL_MASTER_WAITING_FOR_VPLC");
                await Task.Delay(250, ct);
            }
        }
        uint bootNonce = ((uint)nonceParts[0] << 16) | nonceParts[1];
        if (bootNonce == 0) throw new InvalidDataException("Invalid virtual PLC boot proof.");
        var ledger = new SimFullCycleLedger(Path.Combine(folder, "sim-full-cycle.db"), bootNonce);
        var store = new ProductionStore(Path.Combine(folder, "production.db"));
        _ = SimReportIpc.ServeAsync(root, ct);
        var simConfig = new RoiConfigStore(Path.Combine(folder, "sim-config.sqlite"), MachineRole.Master);
        var bridge = new SimProductionBridge(folder);
        var physicalCapture = legacyFins is null ? null :
            new LegacyPhysicalCapture(folder, MachineRole.Master, store, product, legacyFins);
        _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Master, 19621, ct, "SIM_FULL",
            product.Overview, simConfig, bridge.Snapshot);
        _ = RoiDraftIpc.ServeAsync(simConfig, 19641,
            Environment.GetEnvironmentVariable("AOI_SIM_CONFIG_TOKEN"), ct);
        _ = FramePreviewIpc.ServeAsync(store, MachineRole.Master, folder, 19631, ct);
        while (!ct.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            try { await tcp.ConnectAsync(IPAddress.Loopback, 19610, ct); }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine("SIM_FULL_MASTER_WAITING_FOR_SLAVE");
                await Task.Delay(250, ct);
                continue;
            }
            using var session = new PeerSession(tcp);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump = session.PumpOutboxAsync(store, linked.Token);
            var production = PollAsync(store, ledger, fins, legacyFins, physicalCapture,
                folder, bootNonce, product, bridge, linked.Token);
            Console.WriteLine("SIM_FULL_MASTER_PEER_CONNECTED");
            // A production task can fault while the peer read task waits forever.
            // Observe the FIRST task to finish and surface production failure to
            // the process supervisor; never leave an apparently live idle Core.
            var receive = session.ReadAsync(self, "slave", store,
                (message, duplicate, _) =>
                {
                    if (message.PayloadType == "capture_fault")
                    {
                        var fault = WireFormat.Parse<PhysicalCaptureFault>(message.PayloadJson);
                        Console.WriteLine("SIM_MASTER_REMOTE_CAPTURE_HOLD token=" +
                            fault.Trigger.Token + " workpiece=" +
                            fault.Trigger.Workpiece.WorkpieceId + " capture=" +
                            fault.Trigger.CaptureId + " device=" +
                            fault.Trigger.PhysicalDeviceId + " duplicate=" + duplicate);
                    }
                    return Task.CompletedTask;
                }, linked.Token);
            try
            {
                var first = await Task.WhenAny(receive, production);
                if (ReferenceEquals(first, production))
                {
                    try { await production; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("SIM_FULL_MASTER_PRODUCTION_FAILED " + ex);
                        throw new InvalidOperationException("SIM_FULL_MASTER_PRODUCTION_FAILED", ex);
                    }
                    throw new InvalidOperationException("SIM_FULL_PRODUCTION_STOPPED_WITHOUT_CANCELLATION");
                }
                await receive;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is IOException or SocketException)
            {
                Console.Error.WriteLine("SIM_FULL_MASTER_RECONNECT " + ex.Message);
            }
            finally
            {
                linked.Cancel();
                try { await Task.WhenAll(pump, production); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
        }
    }

    private static async Task PollAsync(ProductionStore store, SimFullCycleLedger ledger,
        FinsSimClient fins, LegacyFinsLoopbackClient? legacyFins,
        LegacyPhysicalCapture? physicalCapture, string folder,
        uint bootNonce, SimProductRuntime product, SimProductionBridge bridge,
        CancellationToken ct)
    {
        var held = new HashSet<ushort>();
        var exited = new HashSet<ushort>();
        while (!ct.IsCancellationRequested)
        {
            // Detect restart of the in-memory mechanical queue, even with an
            // unchanged token sequence. A new boot must not borrow old WorkpieceIds.
            var nonce = await fins.ReadAsync(4007, 2, ct);
            uint current = ((uint)nonce[0] << 16) | nonce[1];
            if (current != bootNonce) throw new InvalidDataException("SIM_FULL_VPLC_RESTART_HOLD");
            ushort load = (await fins.ReadAsync(SimFullVirtualPlc.LoadTokenAddress, 1, ct))[0];
            if (load != 0 && bridge.CanLoad)
            {
                if (ledger.Find(load) is not null)
                    throw new InvalidDataException("SIM_FULL_DUPLICATE_LOAD_HOLD");
                // The legacy shadow owns D2000: D4302 makes the PLC post the
                // independent D1999=1 scan and publish the current barcode.
                // Reading D2000 before the PLC offer would otherwise see zeros.
                if (legacyFins is not null)
                    await LegacyShadowHostBridge.OfferLoadAsync(legacyFins, load, ct);
                var barcode = FinsDm.SimBarcode(await fins.ReadAsync(2000, 10, ct));
                if (string.IsNullOrWhiteSpace(barcode))
                    throw new InvalidDataException("SIM_FULL_EMPTY_LOAD_BARCODE_HOLD");
                // Persistent identity never derives from barcode or reused token.
                var item = WorkpieceIdentity.New(barcode, product.Epoch, product.ReleaseId);
                var registration = PeerEnvelope.Create("master", "slave", load, item,
                    "workpiece_registered", item);
                store.CreateAndQueueRegistration(item, registration);
                ledger.Register(load, item);
                int frames = physicalCapture is null
                    ? SimElevenChannelFixture.CaptureOwned(item, MachineRole.Master,
                        store, folder, product) : 0;
                if (frames != (physicalCapture is null ? 5 : 0))
                    throw new InvalidDataException("SIM_FULL_MASTER_FRAMES_HOLD");
                bridge.Register(load, item);
                if (legacyFins is not null)
                    await LegacyShadowHostBridge.ConfirmLoadAsync(
                        legacyFins, load, item.Barcode, ct);
                await EnsureNonceAsync(fins, bootNonce, ct);
                await fins.WriteAsync(SimFullVirtualPlc.LoadAckAddress, load);
                Console.WriteLine($"SIM_FULL_LOADED token={load} id={item.WorkpieceId} " +
                    $"barcode={item.Barcode} master_channels={frames}");
            }

            // Existing verified frames are accounted for only once a virtual
            // station sensor actually fires. This drives A1 WAL progress and
            // A2 item positions while keeping PeerLink ownership unchanged.
            var stationEvents = bridge.Advance(store);
            if (physicalCapture is not null)
                foreach (var evt in stationEvents)
                {
                    if (evt.Kind != SimMechanicalEventKind.CaptureRequested ||
                        string.IsNullOrWhiteSpace(evt.PhysicalDeviceId)) continue;
                    var capturedItem = ledger.Find(evt.Token);
                    if (capturedItem is null || capturedItem.WorkpieceId != evt.WorkpieceId)
                        throw new InvalidDataException("Physical sensor has no durable workpiece owner.");
                    var trigger = new PhysicalCaptureTrigger(capturedItem, Guid.NewGuid(),
                        evt.Token, evt.PhysicalDeviceId, 1, evt.Sequence, DateTimeOffset.UtcNow);
                    trigger.Validate();
                    if (trigger.OwnerRole == MachineRole.Master)
                        await physicalCapture.HandleAsync(trigger, ct);
                    else
                    {
                        // The physical trigger belongs to a Slave-owned camera.
                        // Never synthesize its image/result inside Master.
                        var msg = PeerEnvelope.Create("master", "slave", evt.Sequence,
                            capturedItem, "capture_trigger", trigger,
                            trigger.Channels[0].ChannelId);
                        store.QueueCaptureTrigger(trigger, msg);
                        Console.WriteLine("SIM_MASTER_SLAVE_TRIGGER_QUEUED token=" +
                            evt.Token + " device=" + evt.PhysicalDeviceId +
                            " workpiece=" + evt.WorkpieceId + " capture=" + trigger.CaptureId);
                    }
                }
            ushort exit = (await fins.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1, ct))[0];
            if (exit != 0 && !held.Contains(exit) && !exited.Contains(exit))
            {
                var item = ledger.Find(exit)
                    ?? throw new InvalidDataException("SIM_FULL_UNKNOWN_EXIT_TOKEN_HOLD");
                // Slave faults enter Master WAL in the SAME PeerLink inbox
                // transaction that authorizes the peer ACK. A missing channel
                // cannot silently stall forever or later become GOOD.
                if (store.HasWorkpieceHold(item.WorkpieceId))
                {
                    held.Add(exit);
                    Console.WriteLine("SIM_FULL_HOLD token=" + exit +
                        " id=" + item.WorkpieceId +
                        " why=CAPTURE_FAULT_DURABLE manual_reconciliation=1");
                    continue;
                }
                var observations = store.ResultsFor(item.WorkpieceId);
                if (observations.Select(x => x.ChannelId).Distinct(StringComparer.Ordinal).Count() == 11 &&
                    observations.Count == 11 &&
                    bridge.ReadyToScan(exit, item.WorkpieceId))
                {
                    var scanned = FinsDm.SimBarcode(
                        await fins.ReadAsync(SimFullVirtualPlc.EndBarcodeAddress, 10, ct));
                    var coordinated = bridge.ObserveAndEvaluate(item.WorkpieceId, scanned);
                    var decision = QualityRules.Evaluate(item,
                        SimElevenChannelFixture.InspectionChannels, observations,
                        endBarcodeMatches: scanned == item.Barcode);
                    if (decision.Disposition == FinalDisposition.Hold ||
                        !decision.IsSimulation || coordinated is null ||
                        coordinated.Disposition != decision.Disposition ||
                        coordinated.Proof != decision.Proof ||
                        !coordinated.Expected.SequenceEqual(decision.Expected, StringComparer.Ordinal) ||
                        !coordinated.Received.SequenceEqual(decision.Received, StringComparer.Ordinal))
                    {
                        // Persist the actual failure reason BEFORE marking the FIFO blocked.
                        // Never accept this workpiece for a subsequent GOOD/NG final.
                        store.RecordWorkpieceHold(item,
                            scanned == item.Barcode ? "UNVERIFIED_FINAL_PROOF" : "END_BARCODE_MISMATCH",
                            $"SIM_FULL virtual exit token {exit}: final proof rejected; manual reconciliation required");
                        held.Add(exit);
                        Console.WriteLine($"SIM_FULL_HOLD token={exit} id={item.WorkpieceId} " +
                            $"why={decision.Proof} expected_barcode={item.Barcode} end_barcode={scanned}");
                    }
                    else
                    {
                        // Persist immutable final first. Unknown PLC response/restart is
                        // an operator-reconciliation HOLD, never an untracked re-drive.
                        store.StoreFinal(item, decision);
                        bridge.FinalPersisted(item.WorkpieceId, decision);
                        ushort command = decision.Disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2;
                        // In the opt-in legacy profile, this is a real TWO-write FINS
                        // handshake. D4003 remains only the simulator FIFO commit.
                        // A lost ACK/readback is deliberately fatal; never auto-redrive.
                        if (legacyFins is not null)
                            await LegacyShadowHostBridge.WriteNormalFinalAsync(
                                legacyFins, exit, decision.Disposition, ct);
                        await EnsureNonceAsync(fins, bootNonce, ct);
                        await fins.WriteAsync(SimFullVirtualPlc.ExitDecisionAddress, exit, command);
                        ledger.MarkExited(exit, decision);
                        bridge.ConfirmEjection(exit, item.WorkpieceId, decision.Disposition);
                        exited.Add(exit);
                        Console.WriteLine($"SIM_FULL_EXITED token={exit} id={item.WorkpieceId} " +
                            $"decision={decision.Disposition} barcode={item.Barcode} " +
                            $"received={observations.Count}");
                    }
                }
            }
            await Task.Delay(55, ct);
        }
    }

    private static async Task RunSlaveCaptureWorkerAsync(ProductionStore store,
        LegacyPhysicalCapture physical, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pending = store.PendingCaptureTriggers();
            foreach (var trigger in pending)
            {
                ct.ThrowIfCancellationRequested();
                await physical.HandleAsync(trigger, ct);
                store.CompleteCaptureTrigger(trigger);
                Console.WriteLine("SIM_SLAVE_TRIGGER_COMPLETED capture=" +
                    trigger.CaptureId + " device=" + trigger.PhysicalDeviceId);
            }
            await Task.Delay(35, ct);
        }
    }

    private static async Task EnsureNonceAsync(FinsSimClient fins, uint expected, CancellationToken ct)
    {
        var actual = await fins.ReadAsync(4007, 2, ct);
        if (((uint)actual[0] << 16 | actual[1]) != expected)
            throw new InvalidDataException("SIM_FULL_VPLC_RESTART_DURING_COMMIT_HOLD");
    }
}
