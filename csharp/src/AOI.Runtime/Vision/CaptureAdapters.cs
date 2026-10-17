using System.Diagnostics;
using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime.Vision;

/// <summary>
/// A1 supplies this immutable correlation BEFORE a physical capture is expected.
/// It is never inferred from barcode, frame order, or the last active workpiece.
/// For a real hard-trigger camera, A1 must resolve the hardware trigger token to this
/// ticket before offering the frame; A3 does not guess an association.
/// </summary>
public sealed record CaptureTicket(
    WorkpieceIdentity Workpiece, string PhysicalDeviceId, Guid CaptureId, int AttemptNo)
{
    public IReadOnlyList<ChannelDefinition> Channels
    {
        get
        {
            Validate();
            return ChannelCatalog.All.Where(c => c.PhysicalDeviceId == PhysicalDeviceId)
                .OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        }
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Workpiece);
        Workpiece.Validate();
        if (CaptureId == Guid.Empty || AttemptNo < 1 ||
            string.IsNullOrWhiteSpace(PhysicalDeviceId) ||
            !ChannelCatalog.All.Any(c => c.PhysicalDeviceId == PhysicalDeviceId))
            throw new ArgumentException("Invalid device, capture ID, attempt, or workpiece correlation.");
    }
}

/// <summary>
/// Owns one decoded Mat. TakeOwnership consumes the caller's Mat; CopyFrom clones vendor
/// callback memory before it can be reused. The receiver MUST dispose every acquired frame.
/// FrameId identifies the raw area frame or the unchanged physical line-scan parent.
/// </summary>
public sealed class CameraFrame : IDisposable
{
    private Mat? _pixels;
    public CaptureTicket Ticket { get; }
    public Guid FrameId { get; }
    public DateTimeOffset FrameReadyUtc { get; }
    public long MonotonicTimestamp { get; }
    public long AllocatedBytes { get; }

    private CameraFrame(CaptureTicket ticket, Guid frameId, Mat pixels, DateTimeOffset utc)
    {
        Ticket = ticket;
        FrameId = frameId;
        _pixels = pixels;
        FrameReadyUtc = utc;
        MonotonicTimestamp = Stopwatch.GetTimestamp();
        AllocatedBytes = checked((long)pixels.Rows * pixels.Cols * pixels.ElemSize());
    }

    public Mat Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CameraFrame));

    public static CameraFrame TakeOwnership(CaptureTicket ticket, Guid frameId, Mat pixels,
        DateTimeOffset? frameReadyUtc = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.Validate();
        ArgumentNullException.ThrowIfNull(pixels);
        if (frameId == Guid.Empty) throw new ArgumentException("FrameId cannot be empty.", nameof(frameId));
        bool lineScan = ticket.Channels.Count == 2;
        VisionPixelOps.RequireSupported(pixels, lineScan);
        return new CameraFrame(ticket, frameId, pixels, frameReadyUtc ?? DateTimeOffset.UtcNow);
    }

    public static CameraFrame CopyFrom(CaptureTicket ticket, Guid frameId, Mat vendorBuffer,
        DateTimeOffset? frameReadyUtc = null)
    {
        ArgumentNullException.ThrowIfNull(vendorBuffer);
        var copy = vendorBuffer.Clone();
        try { return TakeOwnership(ticket, frameId, copy, frameReadyUtc); }
        catch { copy.Dispose(); throw; }
    }

    public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.Dispose();
}

/// <summary>One device acquisition, before CamDone. Failure has no fabricated ChannelResult.</summary>
public sealed record FrameRead(ChannelOutcome Status, string Reason, CameraFrame? Frame)
{
    public static FrameRead Ready(CameraFrame frame) => new(ChannelOutcome.Ok, "FRAME_READY", frame);
    public static FrameRead Missing(string why) => new(ChannelOutcome.Missing, why, null);
    public static FrameRead Error(string why) => new(ChannelOutcome.Error, why, null);
}

/// <summary>
/// Read does not mean software trigger. An actual camera adapter can push hard-triggered
/// frames to BoundedCameraFrameInbox independently of observed PLC requests.
/// </summary>
public interface IFrameSource
{
    Task<FrameRead> ReadAsync(CaptureTicket ticket, TimeSpan deadline,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded exact-CaptureId join. Supports FRAME_THEN_REQUEST and REQUEST_THEN_FRAME
/// without treating PLC=1 as a software trigger. Prevents a late or duplicate frame from
/// filling another workpiece's capture. All ownership transfers are explicit.
/// </summary>
public sealed class BoundedCameraFrameInbox : IFrameSource, IDisposable
{
    private sealed class Slot(CaptureTicket ticket)
    {
        public CaptureTicket Ticket { get; } = ticket;
        public CameraFrame? Frame { get; set; }
        public TaskCompletionSource<FrameRead>? Waiter { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = new();
    private readonly HashSet<Guid> _completed = new();
    private readonly Queue<Guid> _completionOrder = new();
    private readonly int _maxPending;
    private readonly long _maxBytes;
    private long _bufferedBytes;
    private bool _disposed;

    public long BufferedBytes { get { lock (_gate) return _bufferedBytes; } }
    public int PendingCaptures { get { lock (_gate) return _slots.Count; } }

    public BoundedCameraFrameInbox(int maxPending = 16, long maxBufferedBytes = 256L * 1024 * 1024)
    {
        if (maxPending is < 1 or > 4096 || maxBufferedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPending));
        _maxPending = maxPending;
        _maxBytes = maxBufferedBytes;
    }

    /// <summary>
    /// The caller relinquishes ownership regardless of the return value.
    /// False indicates duplicate, finished, over-budget, or wrong identity.
    /// </summary>
    public bool TryOffer(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_disposed || frame.AllocatedBytes > _maxBytes ||
                _completed.Contains(frame.Ticket.CaptureId))
            {
                frame.Dispose();
                return false;
            }
            if (!_slots.TryGetValue(frame.Ticket.CaptureId, out var slot))
            {
                if (_slots.Count >= _maxPending || frame.AllocatedBytes > _maxBytes - _bufferedBytes)
                {
                    frame.Dispose();
                    return false;
                }
                slot = new Slot(frame.Ticket) { Frame = frame };
                _slots.Add(frame.Ticket.CaptureId, slot);
                _bufferedBytes += frame.AllocatedBytes;
                return true;
            }
            if (slot.Ticket != frame.Ticket || slot.Frame is not null)
            {
                frame.Dispose();
                return false;
            }
            if (slot.Waiter is not null)
            {
                _slots.Remove(frame.Ticket.CaptureId);
                RememberCompleted(frame.Ticket.CaptureId);
                slot.Waiter.TrySetResult(FrameRead.Ready(frame));
                return true;
            }
            if (frame.AllocatedBytes > _maxBytes - _bufferedBytes)
            {
                frame.Dispose();
                return false;
            }
            slot.Frame = frame;
            _bufferedBytes += frame.AllocatedBytes;
            return true;
        }
    }

    public async Task<FrameRead> ReadAsync(CaptureTicket ticket, TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.Validate();
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<FrameRead> waiter;
        lock (_gate)
        {
            if (_disposed) return FrameRead.Error("FRAME_SOURCE_CLOSED");
            if (_completed.Contains(ticket.CaptureId))
                return FrameRead.Error("CAPTURE_ALREADY_CONSUMED_OR_TIMED_OUT");
            if (_slots.TryGetValue(ticket.CaptureId, out var slot))
            {
                if (slot.Ticket != ticket) return FrameRead.Error("CAPTURE_IDENTITY_MISMATCH");
                if (slot.Frame is { } ready)
                {
                    slot.Frame = null;
                    _bufferedBytes -= ready.AllocatedBytes;
                    _slots.Remove(ticket.CaptureId);
                    RememberCompleted(ticket.CaptureId);
                    return FrameRead.Ready(ready);
                }
                if (slot.Waiter is not null) return FrameRead.Error("DUPLICATE_CAPTURE_WAIT");
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                slot.Waiter = waiter;
            }
            else
            {
                if (_slots.Count >= _maxPending) return FrameRead.Error("FRAME_QUEUE_FULL");
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _slots.Add(ticket.CaptureId, new Slot(ticket) { Waiter = waiter });
            }
        }
        try
        {
            return await waiter.Task.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            DropWaiter(ticket, waiter);
            return FrameRead.Missing("FRAME_TIMEOUT");
        }
        catch (OperationCanceledException)
        {
            DropWaiter(ticket, waiter);
            throw;
        }
    }

    private void DropWaiter(CaptureTicket ticket, TaskCompletionSource<FrameRead> waiter)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(ticket.CaptureId, out var slot) &&
                ReferenceEquals(slot.Waiter, waiter))
            {
                _slots.Remove(ticket.CaptureId);
                RememberCompleted(ticket.CaptureId);
            }
            // A concurrent offer can complete the waiter immediately before cancellation.
            // In that case ownership never transfers to the caller; dispose its Mat.
            else if (waiter.Task.IsCompletedSuccessfully)
                waiter.Task.Result.Frame?.Dispose();
        }
    }

    private void RememberCompleted(Guid captureId)
    {
        _completed.Add(captureId);
        _completionOrder.Enqueue(captureId);
        while (_completionOrder.Count > 4096) _completed.Remove(_completionOrder.Dequeue());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var slot in _slots.Values)
            {
                slot.Frame?.Dispose();
                slot.Waiter?.TrySetResult(FrameRead.Error("FRAME_SOURCE_CLOSED"));
            }
            _slots.Clear();
            _bufferedBytes = 0;
        }
    }
}

/// <summary>
/// File adapter; reads only after the caller's station capture event, never at load time.
/// Each item has its own WorkpieceId folder unless explicitly running flat offline lab.
/// </summary>
public sealed class PngFileFrameSource : IFrameSource
{
    private readonly string _root;
    private readonly bool _flatLab;
    private readonly long _maxFileBytes;

    public PngFileFrameSource(string imageRoot, bool flatLab = false,
        long maxFileBytes = 256L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRoot);
        if (maxFileBytes is < 16 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        _root = Path.GetFullPath(imageRoot);
        _flatLab = flatLab;
        _maxFileBytes = maxFileBytes;
    }

    public Task<FrameRead> ReadAsync(CaptureTicket ticket, TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        ticket.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        var channels = ticket.Channels;
        string fileName = channels.Count == 2
            ? ticket.PhysicalDeviceId + ".png"
            : channels.Single().ChannelId + ".png";
        string directory = _flatLab ? _root :
            Path.Combine(_root, ticket.Workpiece.WorkpieceId.ToString("N"));
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return Task.FromResult(FrameRead.Missing("PNG_MISSING:" + fileName));
        try
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length is < 16 || stream.Length > _maxFileBytes)
                    return Task.FromResult(FrameRead.Error("PNG_SIZE_INVALID:" + fileName));
                Span<byte> header = stackalloc byte[8];
                stream.ReadExactly(header);
                ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
                if (!header.SequenceEqual(signature))
                    return Task.FromResult(FrameRead.Error("PNG_SIGNATURE_INVALID:" + fileName));
            }
            var pixels = Cv2.ImRead(path, ImreadModes.Unchanged);
            try
            {
                VisionPixelOps.RequireSupported(pixels, lineScan: channels.Count == 2);
                if ((long)pixels.Rows * pixels.Cols > 200_000_000)
                    throw new InvalidDataException("Decoded PNG pixel limit exceeded.");
                return Task.FromResult(FrameRead.Ready(CameraFrame.TakeOwnership(
                    ticket, Guid.NewGuid(), pixels)));
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or OpenCVException)
        {
            return Task.FromResult(FrameRead.Error("PNG_DECODE_FAILED:" +
                ex.GetType().Name));
        }
    }
}
