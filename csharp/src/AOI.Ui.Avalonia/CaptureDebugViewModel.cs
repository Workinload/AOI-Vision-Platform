using AOI.Ui.Client;
using AOI.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace AOI.Ui.Avalonia;

/// <summary>
/// A4 read-only capture workbench. A0/A1/A2 must publish the actual FINS/FrameReady
/// event feed before any request or CamDone timestamps can truthfully be displayed.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly CaptureSnapshotObserver _captureObserver = new();
    private readonly Dictionary<string, CapturePngEvidence> _debugPngProofs =
        new(StringComparer.Ordinal);
    private Guid? _debugProofWorkpieceId;
    private CancellationTokenSource? _debugProofCancellation;
    private int _debugProofGeneration;
    private bool _changingDebugSelection;
    private bool _changingDebugWorkpiece;
    [ObservableProperty] private WorkpieceHistoryTile? captureDebugPickedWorkpiece;

    partial void OnCaptureDebugPickedWorkpieceChanged(WorkpieceHistoryTile? value)
    {
        if (_changingDebugWorkpiece) return;
        _suppressHistoryGalleryNavigation = true;
        try
        {
            if (value is null) FollowLiveCommand.Execute(null);
            else SelectedHistoryItem = value;
        }
        finally { _suppressHistoryGalleryNavigation = false; }
        SelectedWorkspaceIndex = 4; // stay in the diagnostic PLC page
    }

    public ObservableCollection<CaptureDebugRow> CaptureDebugChannels { get; } = new();
    public ObservableCollection<CaptureDebugEvent> CaptureDebugTimeline { get; } = new();

    [ObservableProperty] private CaptureDebugRow? selectedCaptureDebugRow;
    [ObservableProperty] private string captureDebugIdentity = "等待来自 Core 的 WorkpieceId";
    [ObservableProperty] private string captureDebugNotice =
        "PLC请求、硬触发FrameReady及FINS CamDone尚无只读事件IPC，不以检测OK倒推握手";
    [ObservableProperty] private string captureDebugLastSnapshot = "等待 Core 快照";
    [ObservableProperty] private string captureDebugError = "尚无可信异常记录";
    [ObservableProperty] private bool isCapturePngChecking;

    public IRelayCommand VerifyCapturePngCommand { get; private set; } = null!;

    private void InitializeCaptureDebug()
    {
        VerifyCapturePngCommand = new AsyncRelayCommand(VerifyCapturePngAsync,
            () => !IsCapturePngChecking);
        RebuildCaptureDebug(MonitorProjection.Offline(
            _role == "slave" ? MachineRole.Slave : MachineRole.Master));
    }

    partial void OnSelectedCaptureDebugRowChanged(CaptureDebugRow? value)
    {
        if (_changingDebugSelection || value is null) return;
        var channel = Channels.FirstOrDefault(x => x.ChannelId == value.ChannelId);
        if (channel is not null) SelectedChannel = channel;
    }

    private void ObserveCaptureDebug(MonitorProjection snapshot)
    {
        _captureObserver.Observe(snapshot, DateTimeOffset.UtcNow);
        RebuildCaptureDebug(snapshot);
    }

    private void RefreshCaptureDebugSelection()
    {
        if (_lastProjection is { } projection) RebuildCaptureDebug(projection);
        else RebuildCaptureDebug(MonitorProjection.Offline(
            _role == "slave" ? MachineRole.Slave : MachineRole.Master));
    }

    private void RebuildCaptureDebug(MonitorProjection snapshot)
    {
        var selected = ViewedWorkpieceId;
        if (_debugProofWorkpieceId != selected)
        {
            _debugProofGeneration++;
            _debugProofCancellation?.Cancel();
            _debugProofCancellation?.Dispose();
            _debugProofCancellation = null;
            _debugProofWorkpieceId = selected;
            _debugPngProofs.Clear();
            IsCapturePngChecking = false;
            CaptureDebugNotice = "点击“核验本件PNG”从 Core 逐通道读回 SHA256 校验图像；不等于实际硬触发/ACK 证据";
        }
        var latest = snapshot.Workpiece?.WorkpieceId == selected;
        var historyRow = selected is null ? null : RecentWorkpieces.FirstOrDefault(
            x => x.WorkpieceId == selected.Value);
        if (!ReferenceEquals(CaptureDebugPickedWorkpiece, historyRow))
        {
            _changingDebugWorkpiece = true;
            try { CaptureDebugPickedWorkpiece = historyRow; }
            finally { _changingDebugWorkpiece = false; }
        }
        CaptureDebugIdentity = selected is null
            ? "等待上料工件（无 WorkpieceId）"
            : (latest ? "实时工件： " : "历史工件： ") + selected.Value.ToString("D") +
              (latest ? " / " + snapshot.Workpiece!.ReleaseId : " / 不复用最新工件通道结果");

        CaptureDebugLastSnapshot = snapshot.IsOnline
            ? "Core " + snapshot.NodeId + " 快照 UTC " +
              snapshot.UpdatedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") +
              " · 本窗口仅观察状态，不拥有 PLC 写入权限"
            : "Core IPC 离线；任何旧结果均已失效";

        var hold = selected is null ? null
            : snapshot.Holds?.Recent.FirstOrDefault(x => x.WorkpieceId == selected.Value);
        CaptureDebugError = hold is null
            ? "本件无最近 HOLD 证据（不代表过程已验证）"
            : hold.ReasonCode + " · " + hold.Detail + " · " +
              hold.RaisedUtc.ToUniversalTime().ToString("HH:mm:ss.fff 'UTC'");

        // These records are immutable so source/change comparison is deterministic.
        var rows = CaptureDebugProjection.ForWorkpiece(snapshot, selected, _debugPngProofs);
        if (!CaptureDebugChannels.SequenceEqual(rows))
        {
            var previous = SelectedCaptureDebugRow?.ChannelId;
            _changingDebugSelection = true;
            try
            {
                CaptureDebugChannels.Clear();
                foreach (var row in rows) CaptureDebugChannels.Add(row);
                SelectedCaptureDebugRow = CaptureDebugChannels.FirstOrDefault(
                    x => x.ChannelId == previous) ??
                    CaptureDebugChannels.FirstOrDefault(x => x.ChannelId == SelectedChannel?.ChannelId);
            }
            finally { _changingDebugSelection = false; }
        }

        var events = _captureObserver.Recent(selected);
        if (!CaptureDebugTimeline.SequenceEqual(events))
        {
            CaptureDebugTimeline.Clear();
            foreach (var item in events.Take(75)) CaptureDebugTimeline.Add(item);
        }
    }

    private async Task VerifyCapturePngAsync()
    {
        Guid? id = ViewedWorkpieceId;
        if (_shutdown.IsCancellationRequested || id is null ||
            _lastProjection?.IsOnline != true || IsCapturePngChecking)
        {
            CaptureDebugNotice = "必须在线且选定一个 WorkpieceId 才能核验 PNG";
            return;
        }
        _debugProofGeneration++;
        var generation = _debugProofGeneration;
        _debugProofCancellation?.Cancel();
        _debugProofCancellation?.Dispose();
        _debugProofCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var cancellation = _debugProofCancellation.Token;
        IsCapturePngChecking = true;
        VerifyCapturePngCommand.NotifyCanExecuteChanged();
        int verified = 0, failed = 0;
        CaptureDebugNotice = "正在按 WorkpieceId 从 Master/Slave Core 核验11路真实PNG...";
        try
        {
            // The local SIM_FULL launcher exposes both loopback endpoints.
            // For physically separated nodes, unavailable Core stays unavailable.
            foreach (var def in ChannelCatalog.All)
            {
                cancellation.ThrowIfCancellationRequested();
                bool ok = false;
                string status;
                int length = 0;
                try
                {
                    var png = await new FramePreviewClient(
                        OperatorWorkspaceTopology.FramePreviewPort(def.ChannelId))
                        .FetchAsync(id.Value, def.ChannelId, cancellation);
                    ok = png is not null;
                    length = png?.Length ?? 0;
                    status = ok ? "Core验证成功" : "此工件图像缺失或完整性未通过";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
                    or ArgumentException or OperationCanceledException)
                {
                    status = "只读PNG IPC：" + ex.GetType().Name;
                }

                if (generation != _debugProofGeneration ||
                    _shutdown.IsCancellationRequested || ViewedWorkpieceId != id)
                    break;
                if (ok) verified++; else failed++;
                var evidence = new CapturePngEvidence(
                    id.Value, def.ChannelId, ok, length, status, DateTimeOffset.UtcNow);
                // Avalonia-bound collections are only edited on the UI thread.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _debugProofGeneration || ViewedWorkpieceId != id)
                        return;
                    _debugPngProofs[def.ChannelId] = evidence;
                    if (_lastProjection is { } projection)
                        RebuildCaptureDebug(projection);
                    CaptureDebugNotice = "已核验 " + (verified + failed) +
                        "/11 · PNG有效 " + verified + " · 不可用 " + failed +
                        "；CamDone/PLC request 仍需真实事件 IPC";
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _debugProofGeneration) return;
                IsCapturePngChecking = false;
                VerifyCapturePngCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void ClearCaptureDebug()
    {
        _debugProofGeneration++;
        _debugProofCancellation?.Cancel();
        _debugProofCancellation?.Dispose();
        _debugProofCancellation = null;
        _debugProofWorkpieceId = null;
        _debugPngProofs.Clear();
        _captureObserver.Clear();
        IsCapturePngChecking = false;
        CaptureDebugChannels.Clear();
        CaptureDebugTimeline.Clear();
        CaptureDebugIdentity = "Core IPC 离线";
        CaptureDebugLastSnapshot = "断线后旧快照无效";
        CaptureDebugError = "未连接，无法读取错误详情";
        CaptureDebugNotice = "等待 Core 重新连接";
    }
}
