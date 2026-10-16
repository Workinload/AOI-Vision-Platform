using AOI.Domain;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>Bounded, read-only loopback PNG client; cannot request arbitrary file paths.</summary>
public sealed class FramePreviewClient(int port)
{
    public async Task<byte[]?> FetchAsync(Guid workpieceId, string channelId,
        CancellationToken cancellationToken = default)
    {
        if (workpieceId == Guid.Empty) return null;
        _ = ChannelCatalog.Require(channelId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes($"{workpieceId:D}|{channelId}\n");
        await stream.WriteAsync(request, timeout.Token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == 0) return null;
        if (length < 0 || length > 2 * 1024 * 1024)
            throw new InvalidDataException("Invalid preview size; no unbounded allocations.");
        byte[] png = new byte[length];
        await stream.ReadExactlyAsync(png, timeout.Token);
        if (length < 8 ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Expected verified PNG in preview IPC.");
        return png;
    }
}
