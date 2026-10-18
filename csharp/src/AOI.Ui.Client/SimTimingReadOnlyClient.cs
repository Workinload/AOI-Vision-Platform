using AOI.Contracts;
using AOI.Domain;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>
/// UI-only read shape matching A0 PR #131's additive SimTimingReply.
/// Kept outside AOI.Contracts so this branch can build against migration HEAD
/// while the independently owned A0 DTO/Host PR is still Draft.
/// </summary>
public sealed record OperatorTimingMilestone(
    long Sequence, string NodeId, string Origin, string Stage,
    DateTimeOffset OccurredUtc, Guid WorkpieceId, Guid TraceId,
    long Epoch, string ReleaseId, Guid? ProcessRunId,
    long? MonotonicTick, Guid? CaptureId, string? PhysicalDeviceId,
    string? ChannelId, int? AttemptNo, Guid? FrameId,
    Guid? OperationId, Guid? MessageId, string? PlcDmAddress,
    ushort? PlcValue, byte? FinsSid, string? Status);

public sealed record OperatorTimingFinding(string Code, string Severity, string Detail);

/// <summary>Only the Core response can assert a milestone. No simulated ACK synthesis.</summary>
public sealed record OperatorTimingReply(
    bool Accepted, string Error, Guid WorkpieceId, bool IsSimulation,
    bool Truncated, IReadOnlyList<OperatorTimingMilestone> Events,
    IReadOnlyList<OperatorTimingFinding> Findings)
{
    public void Validate(Guid requested)
    {
        if (requested == Guid.Empty || WorkpieceId != requested || !IsSimulation ||
            !Accepted || Error != "SIM_ONLY_READ_ONLY" ||
            Events is null || Findings is null ||
            Events.Count > 120 || Findings.Count > 120 ||
            Events.Any(e => e.WorkpieceId != requested || e.TraceId == Guid.Empty ||
                e.Epoch < 0 || e.OccurredUtc == default || e.Sequence < 0 ||
                string.IsNullOrWhiteSpace(e.Stage) ||
                e.NodeId is not ("master" or "slave") ||
                e.Origin is not ("DIRECT_OBSERVATION" or "CORE_WAL_LEGACY") ||
                (e.CaptureId is null && e.AttemptNo is not null) ||
                e.CaptureId == Guid.Empty || e.FrameId == Guid.Empty ||
                e.OperationId == Guid.Empty ||
                (e.ChannelId is not null &&
                    (!ChannelCatalog.All.Any(c => c.ChannelId == e.ChannelId &&
                        c.PhysicalDeviceId == e.PhysicalDeviceId))) ||
                (e.PhysicalDeviceId is not null &&
                    !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == e.PhysicalDeviceId)) ||
                (e.PlcDmAddress is not null &&
                    (e.PlcDmAddress.Length is < 2 or > 12 ||
                     e.PlcDmAddress[0] != 'D' ||
                     !ushort.TryParse(e.PlcDmAddress.AsSpan(1), out _)))) ||
            Findings.Any(x => x is null || string.IsNullOrWhiteSpace(x.Code) ||
                string.IsNullOrWhiteSpace(x.Severity) || x.Detail is null))
            throw new InvalidDataException("Rejected inconsistent, unsafe or cross-workpiece Core timing IPC.");
    }
}

/// <summary>
/// A0 Master Core TIMING|UUID read-only IPC, loopback only.
/// Never reads SQLite directly and never issues a PLC or camera command.
/// </summary>
public sealed class SimTimingReadOnlyClient(int port = 19652)
{
    private const int MaxReplyBytes = 262144;

    public async Task<OperatorTimingReply> ReadAsync(Guid workpieceId,
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

        var reply = WireFormat.Parse<OperatorTimingReply>(
            Encoding.UTF8.GetString(replyBytes.ToArray()));
        if (!reply.Accepted)
            throw new InvalidOperationException("Core timing query refused: " +
                (reply.Error ?? "unknown"));
        reply.Validate(workpieceId);
        return reply;
    }
}
