using AOI.Ui.Client;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace AOI.Ui.Avalonia;

/// <summary>
/// Explicit operator-driven inspection of A0's real persisted #131 Core timeline.
/// Never sends machine commands. Never assumes a channel result proves FINS ACK.
/// </summary>
public partial class MainWindowViewModel
{
    private Guid? _captureTimingWorkpiece;
    private OperatorTimingReply? _captureTimingReply;
    private CancellationTokenSource? _captureTimingCancellation;
    private int _captureTimingGeneration;

    public ObservableCollection<OperatorTimingTile> ActualCaptureTimingEvents { get; } = new();
    public ObservableCollection<OperatorTimingFindingTile> ActualCaptureTimingFindings { get; } = new();

    [ObservableProperty] private string captureTimingSummary =
        "请选择工件后点击“读取真实时序”，查询 Master Core 只读端口 19652";
    [ObservableProperty] private string captureTimingFindingsMessage = "尚未查询 A7 持久化故障发现";
    [ObservableProperty] private bool isCaptureTimingLoading;
    [ObservableProperty] private OperatorTimingTile? selectedCaptureTimingEvent;

    public IAsyncRelayCommand ReadCaptureTimingCommand { get; private set; } = null!;

    private void InitializeCaptureTiming()
    {
        ReadCaptureTimingCommand = new AsyncRelayCommand(ReadCaptureTimingAsync,
            () => !IsCaptureTimingLoading);
    }

    partial void OnSelectedCaptureTimingEventChanged(OperatorTimingTile? value)
    {
        // Do not guess which split channel owns a physical parent-frame event.
        if (value?.ChannelId is not { } channelId) return;
        var channel = Channels.FirstOrDefault(x => x.ChannelId == channelId);
        if (channel is not null) SelectedChannel = channel;
    }

    private void CaptureTimingSelectionChanged(Guid? workpieceId)
    {
        if (_captureTimingWorkpiece == workpieceId) return;
        _captureTimingWorkpiece = workpieceId;
        _captureTimingGeneration++;
        _captureTimingCancellation?.Cancel();
        _captureTimingCancellation?.Dispose();
        _captureTimingCancellation = null;
        _captureTimingReply = null;
        ActualCaptureTimingEvents.Clear();
        ActualCaptureTimingFindings.Clear();
        SelectedCaptureTimingEvent = null;
        IsCaptureTimingLoading = false;
        ReadCaptureTimingCommand.NotifyCanExecuteChanged();
        CaptureTimingFindingsMessage = "尚未查询本件持久化故障发现";
        CaptureTimingSummary = workpieceId is null
            ? "无有效 WorkpieceId；请先选择最新或历史工件"
            : "本件 " + workpieceId.Value.ToString("D") +
              " 尚未查询；没有 ACK 证据不等于 PLC 已确认";
    }

    private async Task ReadCaptureTimingAsync()
    {
        var id = ViewedWorkpieceId;
        if (id is null || id == Guid.Empty || _lastProjection?.IsOnline != true ||
            _shutdown.IsCancellationRequested || IsCaptureTimingLoading)
        {
            CaptureTimingSummary = "只允许对在线 Core 提供的有效 WorkpieceId 发起只读查询";
            return;
        }
        // An explicit re-query invalidates the prior result until the new response is verified.
        _captureTimingGeneration++;
        int generation = _captureTimingGeneration;
        _captureTimingCancellation?.Cancel();
        _captureTimingCancellation?.Dispose();
        _captureTimingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var ct = _captureTimingCancellation.Token;
        _captureTimingReply = null;
        ActualCaptureTimingEvents.Clear();
        ActualCaptureTimingFindings.Clear();
        SelectedCaptureTimingEvent = null;
        IsCaptureTimingLoading = true;
        ReadCaptureTimingCommand.NotifyCanExecuteChanged();
        CaptureTimingSummary = "正在从 127.0.0.1:19652 查询本件 A7 持久化事件...";

        try
        {
            var reply = await new SimTimingReadOnlyClient().ReadAsync(id.Value, ct);
            // UI state must only be mutated on Avalonia's UI dispatcher.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _captureTimingGeneration || ViewedWorkpieceId != id ||
                    _lastProjection?.IsOnline != true || _shutdown.IsCancellationRequested)
                    return;
                var display = OperatorTimingProjection.Display(reply);
                _captureTimingReply = reply;
                foreach (var item in display.Events) ActualCaptureTimingEvents.Add(item);
                foreach (var finding in display.Findings) ActualCaptureTimingFindings.Add(finding);
                CaptureTimingSummary = display.Summary;
                CaptureTimingFindingsMessage = reply.Findings.Count == 0
                    ? "本件无已报告 A7 差异（不证明不存在故障或 ACK 已收到）"
                    : "A7 已报告 " + reply.Findings.Count + " 项时序/归属异常";
                if (_lastProjection is { } projection)
                    RebuildCaptureDebug(projection);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException
            or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _captureTimingGeneration || ViewedWorkpieceId != id) return;
                CaptureTimingSummary = "真实时间线不可用（未确认任何 ACK）：" +
                    ex.GetType().Name + " · " + ex.Message;
                CaptureTimingFindingsMessage = "Core 只读端点未提供可核验的故障结果";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _captureTimingGeneration) return;
                IsCaptureTimingLoading = false;
                ReadCaptureTimingCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void ClearCaptureTiming()
    {
        _captureTimingGeneration++;
        _captureTimingCancellation?.Cancel();
        _captureTimingCancellation?.Dispose();
        _captureTimingCancellation = null;
        _captureTimingWorkpiece = null;
        _captureTimingReply = null;
        ActualCaptureTimingEvents.Clear();
        ActualCaptureTimingFindings.Clear();
        SelectedCaptureTimingEvent = null;
        IsCaptureTimingLoading = false;
        if (ReadCaptureTimingCommand is not null)
            ReadCaptureTimingCommand.NotifyCanExecuteChanged();
        CaptureTimingSummary = "Core IPC 已离线；旧事件已清空，不能继续显示为实时 ACK";
        CaptureTimingFindingsMessage = "无法读取 A7 持久化故障发现";
    }
}
