using AOI.Contracts;
using AOI.Domain;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Runtime;

/// <summary>Read-only loopback UI IPC. No PLC commands or direct storage exposure.</summary>
public static class SnapshotIpc
{
    public static async Task ServeAsync(ProductionStore store, string nodeId,
        MachineRole role, int port, CancellationToken ct, string simulationMode = "SIM_LIGHT")
    {
        var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        Console.WriteLine($"IPC_LISTEN {nodeId} 127.0.0.1:{port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await server.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _ = Task.Run(async () =>
                {
                    using (socket)
                    using (var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        try
                        {
                            while (socket.Connected && !ct.IsCancellationRequested)
                            {
                                MachineSnapshot snapshot = store.Snapshot(nodeId, role) with
                                {
                                    SimulationMode = simulationMode
                                };
                                await writer.WriteLineAsync(WireFormat.Serialize(snapshot));
                                await Task.Delay(350, ct);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                    }
                }, ct);
            }
        }
        finally { server.Stop(); }
    }
}
