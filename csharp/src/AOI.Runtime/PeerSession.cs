using AOI.Contracts;
using System.Net.Sockets;
using System.Text;

namespace AOI.Runtime;

public sealed class PeerSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writerLock = new(1, 1);

    public PeerSession(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
        _writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public async Task SendAsync(PeerPacket message, CancellationToken ct)
    {
        var payload = WireFormat.Serialize(message);
        if (payload.Length > 512 * 1024) throw new InvalidDataException("Peer metadata exceeds 512KiB; image buffers must be out of band.");
        await _writerLock.WaitAsync(ct);
        try { await _writer.WriteLineAsync(payload.AsMemory(), ct); }
        finally { _writerLock.Release(); }
    }

    public async Task PumpOutboxAsync(ProductionStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var pending in store.PendingOutbox())
                await SendAsync(new PeerPacket("message", pending, null), ct);
            await Task.Delay(500, ct);
        }
    }

    public async Task ReadAsync(string nodeId, string otherNodeId, ProductionStore store,
        Func<PeerEnvelope, bool, CancellationToken, Task> onPersisted,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) throw new EndOfStreamException("Peer closed TCP connection.");
            if (line.Length > 512 * 1024) throw new InvalidDataException("Peer line size limit exceeded.");
            var packet = WireFormat.Parse<PeerPacket>(line);
            if (packet.Kind == "ack" && packet.Ack is not null)
            {
                if (packet.Ack.SenderNodeId != otherNodeId)
                    throw new InvalidDataException("Spoofed PERSISTED_ACK sender.");
                store.PersistAcknowledgement(packet.Ack);
                Console.WriteLine($"PERSISTED_ACK_RECEIVED {nodeId} {packet.Ack.MessageId}");
            }
            else if (packet.Kind == "message" && packet.Envelope is not null)
            {
                var message = packet.Envelope;
                if (message.SenderNodeId != otherNodeId)
                    throw new InvalidDataException("Wrong peer sender identity.");
                // The receiver's SQLite transaction commits BEFORE this acknowledgement is put on TCP.
                bool duplicate = store.AcceptIncoming(message, nodeId);
                await SendAsync(new PeerPacket("ack", null,
                    new PersistedAck(message.MessageId, nodeId, message.PayloadHash, DateTimeOffset.UtcNow)), ct);
                Console.WriteLine($"PERSISTED_ACK_SENT {nodeId} {message.MessageId} duplicate={duplicate}");
                await onPersisted(message, duplicate, ct);
            }
            else throw new InvalidDataException("Unknown PeerPacket variant.");
        }
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _writerLock.Dispose();
        _reader.Dispose();
        _writer.Dispose();
    }
}
