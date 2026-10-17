using AOI.Contracts;
using AOI.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Host;

/// <summary>
/// Read-only local operator reporting endpoint backed by the Agent07 query engine.
/// This endpoint cannot save/change a production record, issue PLC instructions,
/// or invent rates from local UI counters.
/// </summary>
public static class SimReportIpc
{
    public const int Port = 19651;

    private static SimReportPeriod Period(ProductionPeriodSummary source) =>
        new(source.Label, source.FromUtc, source.UntilUtc, source.Total, source.Good,
            source.Ng, source.Hold, source.InProgress, source.GoodRate);

    public static SimProductionReports Capture(string dataRoot)
    {
        var reports = new ProductionReports(dataRoot);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var zone = TimeZoneInfo.Local;
        var recent = reports.Search(new ProductionSearch(Limit: 20));
        var result = new SimProductionReports(true, DateTimeOffset.UtcNow,
            Period(reports.Daily(today, zone)),
            Period(reports.Weekly(today, zone)),
            recent.TotalMatches,
            recent.Rows.Select(r => new SimReportWorkpiece(
                r.WorkpieceId, r.Barcode, r.ReleaseId, r.RegisteredUtc,
                r.Disposition.ToString(), r.CapturedChannels, r.HoldReason)).ToArray());
        result.Validate();
        return result;
    }

    public static async Task ServeAsync(string dataRoot, CancellationToken ct)
    {
        var tcp = new TcpListener(IPAddress.Loopback, Port);
        tcp.Start();
        Console.WriteLine($"SIM_REPORT_IPC_LISTEN 127.0.0.1:{Port} SIM_ONLY READ_ONLY");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await tcp.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                _ = Task.Run(() => ReplyAsync(dataRoot, client, ct), ct);
            }
        }
        finally { tcp.Stop(); }
    }

    private static async Task ReplyAsync(string root, TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var stream = client.GetStream();
                byte[] bytes = new byte[16];
                int count = 0;
                while (count < bytes.Length)
                {
                    int n = await stream.ReadAsync(bytes.AsMemory(count, 1), timeout.Token);
                    if (n <= 0) return;
                    if (bytes[count] == (byte)'\n') break;
                    count++;
                }
                if (count >= bytes.Length || Encoding.ASCII.GetString(bytes, 0, count) != "REPORT")
                    return;
                SimProductionReportReply reply;
                try
                {
                    reply = new SimProductionReportReply(true, "SIM_ONLY_READ_ONLY",
                        Capture(root));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or
                    InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or
                    FileNotFoundException or ArgumentException)
                {
                    reply = new SimProductionReportReply(false, ex.GetType().Name, null);
                }
                var answer = Encoding.UTF8.GetBytes(WireFormat.Serialize(reply) + "\n");
                await stream.WriteAsync(answer, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                Console.Error.WriteLine("SIM_REPORT_IPC_CLIENT_EXIT " + ex.GetType().Name);
            }
        }
    }
}
