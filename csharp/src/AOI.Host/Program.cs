using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Agent01;
using AOI.Host;
using System.Net;
using System.Net.Sockets;

if (args.Length < 1 || args[0] is not ("master" or "slave" or "vplc"))
{
    Console.Error.WriteLine("Usage: AOI.Host [master|slave|vplc] [--data /path]  (strictly SIM_ONLY)");
    return 2;
}

string role = args[0];
bool allEleven = SimElevenChannelFixture.Enabled;
string dataRoot = args.Length >= 3 && args[1] == "--data"
    ? Path.GetFullPath(args[2]) : Path.GetFullPath("./.artifacts/sim");
using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cancelled.Cancel(); };
Console.WriteLine($"AOI_HOST role={role} mode=SIM_LIGHT channels={(allEleven ? 11 : 2)} real_plc_writes=DISABLED");
try
{
    switch (role)
    {
        case "vplc":
            await new VirtualPlc().RunAsync(cancelled.Token);
            break;
        case "master":
            await RunMasterAsync(dataRoot, allEleven, cancelled.Token);
            break;
        case "slave":
            await RunSlaveAsync(dataRoot, allEleven, cancelled.Token);
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

static async Task RunSlaveAsync(string root, bool allEleven, CancellationToken ct)
{
    const string self = "slave";
    var folder = Path.Combine(root, self);
    var store = new ProductionStore(Path.Combine(folder, "production.db"));
    _ = SnapshotIpc.ServeAsync(store, self, MachineRole.Slave, 19622, ct);
    if (allEleven)
    {
        // Crash recovery of incomplete 11-channel captures requires the A1/A3 asset/outbox
        // reconciliation stage; deliberately HOLD instead of fabricating missing frames.
        int pending = store.WorkpiecesMissingChannel("CH02").Count;
        Console.WriteLine($"SLAVE_11CH_STARTUP_PENDING_HOLD {pending}");
    }
    else
    {
        int recovered = SimOnlySlaveRecovery.RecoverMissingResults(store,
            item => SyntheticVision.Generate(item, "CH02", Path.Combine(folder, "assets")));
        Console.WriteLine($"SLAVE_RECOVERED_SIM_ONLY {recovered}");
    }
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
                    if (message.PayloadType != "workpiece_registered") return;
                    var item = WireFormat.Parse<WorkpieceIdentity>(message.PayloadJson);
                    if (allEleven)
                    {
                        if (duplicate)
                        {
                            // Already committed results are delivered by the durable Outbox retry.
                            // Partial capture is held for manual replay/reconciliation.
                            int count = store.ResultsFor(item.WorkpieceId).Count;
                            Console.WriteLine($"SLAVE_11CH_RETRY workpiece={item.WorkpieceId} results={count} held={count != 6}");
                        }
                        else
                        {
                            int count = SimElevenChannelFixture.CaptureOwned(
                                item, MachineRole.Slave, store, folder);
                            Console.WriteLine($"SLAVE_11CH_NATIVE workpiece={item.WorkpieceId} channels={count}");
                        }
                    }
                    else
                    {
                        bool generated = SimOnlySlaveRecovery.ProcessRegisteredWorkpiece(store, item,
                            identity => SyntheticVision.Generate(identity, "CH02", Path.Combine(folder, "assets")));
                        Console.WriteLine($"SLAVE_CV_RESULT workpiece={item.WorkpieceId} generated={generated} duplicate={duplicate} channel=CH02");
                    }
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

static async Task RunMasterAsync(string root, bool allEleven, CancellationToken ct)
{
    const string self = "master";
    var folder = Path.Combine(root, self);
    var store = new ProductionStore(Path.Combine(folder, "production.db"));
    using var finalGate = new SemaphoreSlim(1, 1);
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
        var production = PollVirtualPlcAsync(store, fins, folder, allEleven, finalGate, sessionCancel.Token);
        try
        {
            await session.ReadAsync(self, "slave", store, async (message, _, token) =>
            {
                if (message.PayloadType != "channel_result") return;
                var workpiece = store.FindWorkpiece(message.WorkpieceId);
                if (workpiece is null)
                    throw new InvalidDataException("Persisted result has no matching WorkpieceId.");
                await TryFinalizeAsync(workpiece, store, fins, allEleven, finalGate, token);
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

static async Task PollVirtualPlcAsync(ProductionStore store, FinsSimClient fins, string folder,
    bool allEleven, SemaphoreSlim finalGate, CancellationToken ct)
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
                    if (allEleven)
                    {
                        int count = SimElevenChannelFixture.CaptureOwned(item, MachineRole.Master, store, folder);
                        Console.WriteLine($"MASTER_11CH_NATIVE workpiece={item.WorkpieceId} channels={count}");
                    }
                    else
                    {
                        var localResult = SyntheticVision.Generate(item, "CH06", Path.Combine(folder, "assets"));
                        store.SaveLocalResult(localResult);
                    }
                    await fins.WriteAsync(1999, 2); // loopback vPLC ONLY
                    Console.WriteLine($"MASTER_WORKPIECE_REGISTERED workpiece={item.WorkpieceId} barcode={barcode} opencv={SyntheticVision.NativeVersion}");
                    // A fast Slave can finish before Master has committed all local frames.
                    // Whichever side finishes last attempts finalization under one gate.
                    if (allEleven)
                        await TryFinalizeAsync(item, store, fins, allEleven, finalGate, ct);
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
    FinsSimClient fins, bool allEleven, SemaphoreSlim finalGate, CancellationToken ct)
{
    await finalGate.WaitAsync(ct);
    try
    {
    if (store.LatestFinal(workpiece.WorkpieceId) is not null) return;
    // Existing A0 fixture has ONE global virtual exit/eject actuator. Never write it
    // for a delayed older item: full queued physical effects require A2 integration.
    if (store.LatestWorkpiece()?.WorkpieceId != workpiece.WorkpieceId)
    {
        Console.WriteLine($"MASTER_HOLD workpiece={workpiece.WorkpieceId} why=SIM_FIXTURE_EXIT_ACTUATOR_NOT_OWNED");
        return;
    }
    var observations = store.ResultsFor(workpiece.WorkpieceId);
    // This is a fixture echo from the SAME simulated barcode DM range, not an independent end scanner.
    // Consequently the final disposition is marked SIM_ONLY and cannot certify physical exit inspection.
    var endCode = FinsDm.SimBarcode(await fins.ReadAsync(2000, 10, ct));
    var expected = allEleven ? SimElevenChannelFixture.InspectionChannels : new[] { "CH02", "CH06" };
    // CH01 is the positioning prerequisite; it must be stored/displayed,
    // but excluded from the 10-channel quality assessment contract.
    var inspectionObservations = observations
        .Where(result => !ChannelCatalog.Require(result.ChannelId).IsPositioning)
        .ToArray();
    var assessment = WorkpieceFlow.Assess(workpiece, expected, inspectionObservations,
        EndScanEvidence.SimulatedFixtureEcho(workpiece, endCode),
        store.LatestFinal(workpiece.WorkpieceId), simulationOnly: true);
    if (assessment.Phase != WorkpiecePhase.ReadyToCommit || assessment.Decision is null)
    {
        Console.WriteLine($"MASTER_HOLD workpiece={workpiece.WorkpieceId} phase={assessment.Phase} why={assessment.Reason}");
        return;
    }
    var decision = assessment.Decision;
    store.StoreFinal(workpiece, decision);
    // All writes here target 127.0.0.1:19600 vPLC; production PLC routing is IMPOSSIBLE in this host.
    await fins.WriteAsync(1016, decision.Disposition == FinalDisposition.Good ? (ushort)1 : (ushort)2);
    await fins.WriteAsync(2099, 2); // synthetic normal branch D1016 -> D2099 only
    Console.WriteLine($"MASTER_SIM_FINAL {decision.Disposition} workpiece={workpiece.WorkpieceId} proof={decision.Proof}");
    }
    finally
    {
        finalGate.Release();
    }
}
