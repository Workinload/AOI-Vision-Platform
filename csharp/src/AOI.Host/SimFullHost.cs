using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
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
        var store = new ProductionStore(Path.Combine(folder, "production.db"));
        _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Slave, 19622, ct, "SIM_FULL");
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
                try
                {
                    await session.ReadAsync(self, "master", store,
                        (message, duplicate, _) =>
                        {
                            if (message.PayloadType != "workpiece_registered")
                                throw new InvalidDataException("SIM_FULL Slave received unsupported packet.");
                            var item = WireFormat.Parse<WorkpieceIdentity>(message.PayloadJson);
                            if (!duplicate)
                            {
                                int resultCount = SimElevenChannelFixture.CaptureOwned(item,
                                    MachineRole.Slave, store, folder);
                                if (resultCount != 6) throw new InvalidDataException("Missing Slave channel: HOLD.");
                                Console.WriteLine($"SIM_FULL_SLAVE_CAPTURE id={item.WorkpieceId} channels={resultCount}");
                            }
                            else if (store.ResultsFor(item.WorkpieceId).Count != 6)
                                throw new InvalidDataException("Incomplete duplicate registration; manual HOLD.");
                            return Task.CompletedTask;
                        }, linked.Token);
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
                }
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task RunMasterAsync(string root, CancellationToken ct)
    {
        const string self = "master";
        var folder = NodeFolder(root, self);
        // Bind one boot nonce before accepting any virtual load or exit token.
        var fins = new FinsSimClient();
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
        _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Master, 19621, ct, "SIM_FULL");
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
            var production = PollAsync(store, ledger, fins, folder, bootNonce, linked.Token);
            Console.WriteLine("SIM_FULL_MASTER_PEER_CONNECTED");
            try
            {
                await session.ReadAsync(self, "slave", store, (_, _, _) => Task.CompletedTask, linked.Token);
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
        FinsSimClient fins, string folder, uint bootNonce, CancellationToken ct)
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
            if (load != 0)
            {
                if (ledger.Find(load) is not null)
                    throw new InvalidDataException("SIM_FULL_DUPLICATE_LOAD_HOLD");
                var barcode = FinsDm.SimBarcode(await fins.ReadAsync(2000, 10, ct));
                if (string.IsNullOrWhiteSpace(barcode))
                    throw new InvalidDataException("SIM_FULL_EMPTY_LOAD_BARCODE_HOLD");
                // Persistent identity never derives from barcode or reused token.
                var item = WorkpieceIdentity.New(barcode, 1, "SIM_LIGHT_TEST_RELEASE");
                var registration = PeerEnvelope.Create("master", "slave", load, item,
                    "workpiece_registered", item);
                store.CreateAndQueueRegistration(item, registration);
                ledger.Register(load, item);
                int frames = SimElevenChannelFixture.CaptureOwned(item, MachineRole.Master, store, folder);
                if (frames != 5) throw new InvalidDataException("SIM_FULL_MASTER_FRAMES_HOLD");
                await EnsureNonceAsync(fins, bootNonce, ct);
                await fins.WriteAsync(SimFullVirtualPlc.LoadAckAddress, load);
                Console.WriteLine($"SIM_FULL_LOADED token={load} id={item.WorkpieceId} " +
                    $"barcode={item.Barcode} master_channels={frames}");
            }

            ushort exit = (await fins.ReadAsync(SimFullVirtualPlc.ExitTokenAddress, 1, ct))[0];
            if (exit != 0 && !held.Contains(exit) && !exited.Contains(exit))
            {
                var item = ledger.Find(exit)
                    ?? throw new InvalidDataException("SIM_FULL_UNKNOWN_EXIT_TOKEN_HOLD");
                var observations = store.ResultsFor(item.WorkpieceId);
                if (observations.Select(x => x.ChannelId).Distinct(StringComparer.Ordinal).Count() == 11 &&
                    observations.Count == 11)
                {
                    var scanned = FinsDm.SimBarcode(
                        await fins.ReadAsync(SimFullVirtualPlc.EndBarcodeAddress, 10, ct));
                    var decision = QualityRules.Evaluate(item,
                        SimElevenChannelFixture.InspectionChannels, observations,
                        endBarcodeMatches: scanned == item.Barcode);
                    if (decision.Disposition == FinalDisposition.Hold || !decision.IsSimulation)
                    {
                        held.Add(exit);
                        Console.WriteLine($"SIM_FULL_HOLD token={exit} id={item.WorkpieceId} " +
                            $"why={decision.Proof} expected_barcode={item.Barcode} end_barcode={scanned}");
                    }
                    else
                    {
                        // Persist immutable final first. Unknown PLC response/restart is
                        // an operator-reconciliation HOLD, never an untracked re-drive.
                        store.StoreFinal(item, decision);
                        ushort command = decision.Disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2;
                        await EnsureNonceAsync(fins, bootNonce, ct);
                        await fins.WriteAsync(SimFullVirtualPlc.ExitDecisionAddress, exit, command);
                        ledger.MarkExited(exit, decision);
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

    private static async Task EnsureNonceAsync(FinsSimClient fins, uint expected, CancellationToken ct)
    {
        var actual = await fins.ReadAsync(4007, 2, ct);
        if (((uint)actual[0] << 16 | actual[1]) != expected)
            throw new InvalidDataException("SIM_FULL_VPLC_RESTART_DURING_COMMIT_HOLD");
    }
}
