using AOI.Contracts;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace AOI.Ui.Client;

/// <summary>Read-only versioned IPC. No Core, database or hardware dependency.</summary>
public sealed class SnapshotClient(int port)
{
    public async IAsyncEnumerable<MachineSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            bool connected;
            try
            {
                await tcp.ConnectAsync("127.0.0.1", port, cancellationToken);
                connected = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { yield break; }
            catch (SocketException) { connected = false; }
            if (connected)
            {
                using var reader = new StreamReader(tcp.GetStream());
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(cancellationToken); }
                    catch (IOException) { break; }
                    catch (SocketException) { break; }
                    if (line is null) break;
                    var snapshot = WireFormat.Parse<MachineSnapshot>(line);
                    if (snapshot.RealHardwareWritesEnabled)
                        throw new InvalidDataException("Unexpected REAL write access in SIM-only UI stream.");
                    yield return snapshot;
                }
            }
            try { await Task.Delay(400, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { yield break; }
        }
    }
}
