using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using System.Net;
using System.Net.Sockets;

if (args.Length < 1 || args[0] is not ("master" or "slave" or "vplc"))
{
    Console.Error.WriteLine("Usage: AOI.Host [master|slave|vplc] [--data /path]  (strictly SIM_ONLY)");
    return 2;
}

string role = args[0];
string dataRoot = args.Length >= 3 && args[1] == "--data"
    ? Path.GetFullPath(args[2]) : Path.GetFullPath("./.artifacts/sim");
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cancelled.Cancel(); };
Console.WriteLine($"AOI_HOST role={role} mode=SIM_LIGHT real_plc_writes=DISABLED");
try
{
    switch (role)
    {
        case "vplc":
            await new VirtualPlc().RunAsync(cancelled.Token);
            break;
        case "master":
            await RunMasterAsync(dataRoot, cancelled.Token);
            break;
        case "slave":
            await RunSlaveAsync(dataRoot, cancelled.Token);
            break;
    }
    return 0;
}
catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { return 0; }
catch (Exception ex)
{
    Console.Error.WriteLine($"AOI_HOST_FATAL {role} {ex}");
    return 1;
}

static async Task RunSlaveAsync(string root, CancellationToken ct)
{
    const string self = "slave";
    var folder = Path.Combine(root, self);
    var store = new ProductionStore(Path.Combine(folder, "production.db"));
    _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Slave, 19622, ct);
    var listener = new TcpListener(IPAddress.Loopback, 19610);
    listener.Start();
    Console.WriteLine("SLAVE_PEER_LISTEN 127.0.0.1:19610");
    try
    {
        while (!ct.IsCancellationRequested)
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            using var session = new PeerSession(tcp);
            using var sessionCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sending = session.PumpOutboxAsync(store, sessionCancel.Token);
            try
            {
                await session.ReadAsync(self, "master", store, async (message, duplicate, token) =>
                {
                    if (duplicate || message.PayloadType != "workpiece_registered") return;
                    var item = WireFormat.Parse<WorkpieceIdentity>(message.PayloadJson);
                    var result = SyntheticVision.Generate(item, "CH02", Path.Combine(folder, "assets"));
                    var outbound = PeerEnvelope.Create(self, "master", 1, item, "channel_result", result, "CH02");
                    store.SaveLocalResult(result, outbound);
                    Console.WriteLine($"SLAVE_CV_RESULT workpiece={item.WorkpieceId} channel=CH02 opencv={SyntheticVision.NativeVersion}");
                    await Task.CompletedTask;
                }, sessionCancel.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is IOException or SocketException)
            {
                Console.Error.WriteLine("SLAVE_PEER_RECONNECT " + ex.Message);
            }
            finally
            {
                sessionCancel.Cancel();
                try { await sending; } catch (OperationCanceledException) { }
            }
        }
    }
    finally { listener.Stop(); }
}

static async Task RunMasterAsync(string root, CancellationToken ct)
{
    const string self = "master";
    var folder = Path.Combine(root, self);
    var store = new ProductionStore(Path.Combine(folder, "production.db"));
    _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Master, 19621, ct);
    var fins = new FinsSimClient();
    while (!ct.IsCancellationRequested)
    {
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(IPAddress.Loopback, 19610, ct); }
        catch (SocketException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine("MASTER_WAITING_FOR_SLAVE");
            await Task.Delay(500, ct);
            continue;
        }
        using var session = new PeerSession(tcp);
        using var sessionCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.WriteLine("MASTER_PEER_CONNECTED");
        var pumping = session.PumpOutboxAsync(store, sessionCancel.Token);
        var production = PollVirtualPlcAsync(store, fins, folder, sessionCancel.Token);
        try
        {
            await session.ReadAsync(self, "slave", store, async (message, _, token) =>
            {
                if (message.PayloadType != "channel_result") return;
                var workpiece = store.LatestWorkpiece();
                if (workpiece is null || workpiece.WorkpieceId != message.WorkpieceId)
                    return; // persisted but not (mis)attached to latest; event remains in flight ledger.
                await TryFinalizeAsync(workpiece, store, fins, token);
            }, sessionCancel.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException)
        {
            Console.Error.WriteLine("MASTER_PEER_RECONNECT " + ex.Message);
        }
        finally
        {
            sessionCancel.Cancel();
            try { await Task.WhenAll(pumping, production); } catch (OperationCanceledException) { }
        }
    }
}

static async Task PollVirtualPlcAsync(ProductionStore store, FinsSimClient fins, string folder, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (store.LatestWorkpiece() is null)
        {
            try
            {
                var signal = await fins.ReadAsync(1999, 1, ct);
                if (signal[0] == 1)
                {
                    // D2000 fixture uses 10 words only, unlike unverified legacy 20-word read.
                    var barcode = FinsDm.SimBarcode(await fins.ReadAsync(2000, 10, ct));
                    var item = WorkpieceIdentity.New(barcode, 1, "SIM_LIGHT_TEST_RELEASE");
                    var registration = PeerEnvelope.Create("master", "slave", 1, item,
                        "workpiece_registered", item);
                    store.CreateAndQueueRegistration(item, registration);
                    var localResult = SyntheticVision.Generate(item, "CH06", Path.Combine(folder, "assets"));
                    store.SaveLocalResult(localResult);
                    await fins.WriteAsync(1999, 2); // loopback vPLC ONLY
                    Console.WriteLine($"MASTER_WORKPIECE_REGISTERED workpiece={item.WorkpieceId} barcode={barcode} opencv={SyntheticVision.NativeVersion}");
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
                Console.Error.WriteLine("MASTER_VPLC_PENDING " + ex.Message);
            }
        }
        await Task.Delay(250, ct);
    }
}

static async Task TryFinalizeAsync(WorkpieceIdentity workpiece, ProductionStore store,
    FinsSimClient fins, CancellationToken ct)
{
    if (store.LatestFinal(workpiece.WorkpieceId) is not null) return;
    var observations = store.ResultsFor(workpiece.WorkpieceId);
    // This is a fixture echo from the SAME simulated barcode DM range, not an independent end scanner.
    // Consequently the final disposition is marked SIM_ONLY and cannot certify physical exit inspection.
    var endCode = FinsDm.SimBarcode(await fins.ReadAsync(2000, 10, ct));
    var decision = QualityRules.Evaluate(workpiece, ["CH02", "CH06"], observations,
        endBarcodeMatches: endCode == workpiece.Barcode && !string.IsNullOrWhiteSpace(endCode));
    if (decision.Disposition == FinalDisposition.Hold)
    {
        Console.WriteLine($"MASTER_HOLD workpiece={workpiece.WorkpieceId} why={decision.Proof}");
        return;
    }
    store.StoreFinal(workpiece, decision);
    // All writes here target 127.0.0.1:19600 vPLC; production PLC routing is IMPOSSIBLE in this host.
    await fins.WriteAsync(1016, decision.Disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2);
    await fins.WriteAsync(2099, 2); // synthetic normal branch D1016 -> D2099 only
    Console.WriteLine($"MASTER_SIM_FINAL {decision.Disposition} workpiece={workpiece.WorkpieceId} proof={decision.Proof}");
}
