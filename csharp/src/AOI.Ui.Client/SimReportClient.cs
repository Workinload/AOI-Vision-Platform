using AOI.Contracts;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>Read-only localhost report query; no direct SQLite access from Avalonia.</summary>
public sealed class SimReportClient(int port = 19651)
{
    public async Task<SimProductionReports> ReadAsync(CancellationToken ct = default)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, timed.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync("REPORT\n"u8.ToArray(), timed.Token);
        var raw = new byte[32768];
        int count = 0;
        while (count < raw.Length)
        {
            int n = await stream.ReadAsync(raw.AsMemory(count, 1), timed.Token);
            if (n == 0) throw new EndOfStreamException("Master report Core IPC ended without a response.");
            if (raw[count] == (byte)'\n') break;
            count++;
        }
        if (count >= raw.Length) throw new InvalidDataException("Oversized Core report response.");
        var reply = WireFormat.Parse<SimProductionReportReply>(
            Encoding.UTF8.GetString(raw, 0, count));
        if (!reply.Accepted || reply.Reports is null)
            throw new InvalidOperationException("Master report unavailable: " + reply.Message);
        reply.Reports.Validate();
        return reply.Reports;
    }
}
