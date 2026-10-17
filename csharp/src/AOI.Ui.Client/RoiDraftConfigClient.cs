using AOI.Contracts;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>SIM_ONLY operator draft-save IPC; cannot apply to published product or hardware.</summary>
public sealed class RoiDraftConfigClient(int port)
{
    public async Task<RoiDraftSaveReply> SaveAsync(RoiDraftEdit edit, string token,
        CancellationToken ct = default)
    {
        edit.Validate();
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
            throw new InvalidOperationException("SIM_ONLY operator session token not configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var stream = socket.GetStream();
        var bytes = Encoding.UTF8.GetBytes(WireFormat.Serialize(new RoiDraftSaveRequest(edit, token)) + "\n");
        if (bytes.Length > 2048) throw new InvalidDataException("Bounded ROI edit exceeded 2KiB.");
        await stream.WriteAsync(bytes, timeout.Token);
        byte[] reply = new byte[4096];
        int count = 0;
        while (count < reply.Length)
        {
            int n = await stream.ReadAsync(reply.AsMemory(count, 1), timeout.Token);
            if (n == 0) throw new EndOfStreamException("SIM ROI IPC closed without a reply.");
            if (reply[count] == (byte)'\n') break;
            count++;
        }
        if (count >= reply.Length)
            throw new InvalidDataException("SIM ROI IPC response exceeds 4KiB.");
        var result = WireFormat.Parse<RoiDraftSaveReply>(Encoding.UTF8.GetString(reply, 0, count));
        if (result.Accepted)
        {
            if (result.Saved is null) throw new InvalidDataException("SIM ROI success missing durable proof.");
            result.Saved.Validate();
            if (result.Saved.ChannelId != edit.ChannelId ||
                result.Saved.Revision != edit.ExpectedRevision + 1 ||
                result.Saved.X != edit.X || result.Saved.Y != edit.Y ||
                result.Saved.Width != edit.Width || result.Saved.Height != edit.Height)
                throw new InvalidDataException("SIM ROI persisted readback does not match request.");
        }
        return result;
    }
}
