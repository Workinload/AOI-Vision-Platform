using AOI.Contracts;
using AOI.Domain;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>
/// A0 #131 Master read-only timing IPC consumer. The public SimTimingReply and
/// SimTimingMilestone are owned by AOI.Contracts; no duplicate wire types.
/// Does not read SQLite, change Core state or issue PLC/camera writes.
/// </summary>
public sealed class SimTimingReadOnlyClient(int port = 19652)
{
    private const int MaxReplyBytes = 262144;

    public async Task<SimTimingReply> ReadAsync(Guid workpieceId,
        CancellationToken cancellationToken = default)
    {
        if (workpieceId == Guid.Empty)
            throw new ArgumentException("An exact nonempty WorkpieceId is mandatory.", nameof(workpieceId));
        if (port is < 1025 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes("TIMING|" + workpieceId.ToString("D") + "\n");
        await stream.WriteAsync(request, timeout.Token);

        using var replyBytes = new MemoryStream();
        var buffer = new byte[4096];
        bool complete = false;
        while (replyBytes.Length < MaxReplyBytes)
        {
            int n = await stream.ReadAsync(buffer, timeout.Token);
            if (n == 0) break;
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, n);
            int contentLength = newline < 0 ? n : newline;
            if (replyBytes.Length + contentLength > MaxReplyBytes)
                throw new InvalidDataException("Oversized Core timing IPC reply.");
            replyBytes.Write(buffer, 0, contentLength);
            if (newline >= 0) { complete = true; break; }
        }
        if (!complete) throw new InvalidDataException("Truncated/unterminated Core timing IPC reply.");
        if (replyBytes.Length == 0) throw new InvalidDataException("Empty Core timing reply.");

        var reply = WireFormat.Parse<SimTimingReply>(
            Encoding.UTF8.GetString(replyBytes.ToArray()));
        if (!reply.Accepted)
            throw new InvalidOperationException("Core timing query refused: " +
                (reply.Error ?? "unknown"));
        // Fail closed: a successful response must belong to the exact requested
        // physical item, even if a local Core or proxy is misconfigured.
        if (reply.WorkpieceId != workpieceId || reply.Error is null ||
            reply.Events is null || reply.Findings is null)
            throw new InvalidDataException("Core returned a different workpiece or invalid timing payload.");
        reply.Validate();
        if (reply.Events.Any(e => e is null ||
            e.NodeId is not ("master" or "slave") ||
            e.CaptureId == Guid.Empty || e.FrameId == Guid.Empty ||
            e.OperationId == Guid.Empty ||
            (e.ChannelId is not null &&
                !ChannelCatalog.All.Any(c => c.ChannelId == e.ChannelId &&
                    c.PhysicalDeviceId == e.PhysicalDeviceId)) ||
            (e.PhysicalDeviceId is not null &&
                !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == e.PhysicalDeviceId))))
            throw new InvalidDataException("Incorrect source/camera identity in Core timing evidence.");
        return reply;
    }
}
