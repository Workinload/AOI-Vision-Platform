using AOI.Contracts;
using AOI.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Host;

/// <summary>
/// Operator-safe, bounded live timing read path. Only A7's already committed
/// event rows are exposed; this endpoint cannot drive a camera or PLC.
/// The result is a live WAL query, not a forensic snapshot of a stopped Core.
/// </summary>
internal static class SimTimingIpc
{
    public const int Port = 19652;
    private const int Limit = 120;

    internal static SimTimingReply Capture(string root, Guid workpiece)
    {
        if (workpiece == Guid.Empty) throw new ArgumentException("WorkpieceId required.");
        var timeline = new ProductionTimelineReader(root).Query(
            new TimingFilter(workpiece, IncludeExistingCoreEvents: false, Limit: Limit));
        var reply = new SimTimingReply(true, "SIM_ONLY_READ_ONLY", workpiece, true,
            timeline.Truncated,
            timeline.Events.Select(e => new SimTimingMilestone(
                e.Sequence, e.NodeId, e.Origin, e.Stage.ToString(),
                e.Utc, e.WorkpieceId, e.TraceId, e.Epoch, e.ReleaseId,
                e.ProcessRunId, e.MonotonicTick, e.CaptureId,
                e.PhysicalDeviceId, e.ChannelId, e.AttemptNo, e.FrameId,
                e.OperationId, e.MessageId, e.PlcDmAddress, e.PlcValue,
                e.FinsSid, e.Status)).ToArray(),
            timeline.DiagnosticDifferences.Select(x =>
                new SimTimingFinding(x.Code, x.Severity, x.Detail)).ToArray());
        reply.Validate();
        return reply;
    }

    public static async Task ServeAsync(string root, CancellationToken ct)
    {
        var server = new TcpListener(IPAddress.Loopback, Port);
        server.Start();
        Console.WriteLine($"SIM_TIMING_IPC_LISTEN 127.0.0.1:{Port} SIM_ONLY READ_ONLY");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await server.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _ = Task.Run(() => ReplyAsync(root, client, ct), ct);
            }
        }
        finally { server.Stop(); }
    }

    private static async Task ReplyAsync(string root, TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null || line.Length > 64 ||
                    !line.StartsWith("TIMING|", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(line.AsSpan(7), "D", out Guid workpiece) ||
                    workpiece == Guid.Empty)
                    return;
                SimTimingReply reply;
                try { reply = Capture(root, workpiece); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or
                    InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or
                    FileNotFoundException or ArgumentException)
                {
                    reply = new SimTimingReply(false, ex.GetType().Name, workpiece,
                        true, false, [], []);
                }
                reply.Validate();
                byte[] bytes = Encoding.UTF8.GetBytes(WireFormat.Serialize(reply) + "\n");
                await client.GetStream().WriteAsync(bytes, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or
                OperationCanceledException)
            {
                Console.Error.WriteLine("SIM_TIMING_IPC_CLIENT_EXIT " + ex.GetType().Name);
            }
        }
    }
}
