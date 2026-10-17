using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace AOI.Ui.Avalonia;

/// <summary>Station navigation reflects the canonical ChannelCatalog, not a simulated motion API.</summary>
public partial class MapStationTile : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string Channels { get; }
    public string Devices { get; }
    [ObservableProperty] private string status = "等待 Core";
    [ObservableProperty] private bool isFocused;

    public MapStationTile(OperatorStation station)
    {
        Id = station.StationId;
        Name = station.DisplayName;
        Channels = string.Join(" · ", station.ChannelIds);
        Devices = string.Join(" / ", station.DeviceIds);
    }
}

/// <summary>Each PNG comes from the SHA-verified, workpiece-scoped Core frame-preview IPC.</summary>
public partial class GalleryChannelTile : ObservableObject, IDisposable
{
    public string ChannelId { get; }
    public string Location { get; }
    public string Owner { get; }
    public IRelayCommand OpenCommand { get; }

    [ObservableProperty] private Bitmap? preview;
    [ObservableProperty] private string imageStatus = "等待选择工件";
    [ObservableProperty] private string channelState = "NO_DATA";

    public GalleryChannelTile(ChannelDefinition channel, Action<string> open)
    {
        ChannelId = channel.ChannelId;
        Location = channel.StationId + " / " + channel.PhysicalDeviceId;
        Owner = channel.OwnerRole.ToString().ToUpperInvariant();
        OpenCommand = new RelayCommand(() => open(ChannelId));
    }

    public void Show(Bitmap? image, string status)
    {
        var old = Preview;
        Preview = image;
        if (!ReferenceEquals(old, image)) old?.Dispose();
        ImageStatus = status;
    }

    public void Dispose() => Show(null, "已清理");
}

public sealed record LiveConveyorTile(string Workpiece, string Stage,
    string Progress, string State, string Sensor);

public partial class MainWindowViewModel
{
    private Guid? _pinnedWorkpieceId;
    private Guid? _galleryLoadedWorkpieceId;
    private int _galleryGeneration;
    private CancellationTokenSource? _galleryCancellation;
    private bool _synchronizingHistory;
    private bool _suppressHistoryGalleryNavigation;
    private bool _synchronizingPlcSelection;

    private void SyncPlcSignalFromChannel(ChannelTile? channel)
    {
        if (channel is null || PlcSignals.Count == 0) return;
        var row = PlcSignals.FirstOrDefault(signal =>
            string.Equals(signal.DmAddress, channel.PlcAddress, StringComparison.Ordinal));
        if (row is null || ReferenceEquals(row, SelectedPlcSignal)) return;
        _synchronizingPlcSelection = true;
        try { SelectedPlcSignal = row; }
        finally { _synchronizingPlcSelection = false; }
    }

    [ObservableProperty] private int selectedWorkspaceIndex;
    [ObservableProperty] private WorkpieceHistoryTile? selectedHistoryItem;
    [ObservableProperty] private string viewedWorkpieceLabel = "实时跟随 · 等待 WorkpieceId";
    [ObservableProperty] private string selectedMapStation = "LOADING";
    [ObservableProperty] private string mapSelectionSummary = "选择机台工位，跳转对应通道、图像和 ROI";
    [ObservableProperty] private string gallerySummary = "选择最近工件或实时跟随，加载由 Core 校验的 11 路 PNG";
    [ObservableProperty] private string conveyorSummary = "等待 Master SIM_FULL 逐件运动状态";
    public ObservableCollection<LiveConveyorTile> LiveConveyor { get; } = new();

    public IReadOnlyList<string> WorkspacePages { get; } =
        ["生产总览", "整机设备地图", "11 路图集", "历史与报警", "PLC 工程", "方案与 ROI", "生产报表"];

    public ObservableCollection<MapStationTile> MapStations { get; } =
        new(OperatorWorkspaceTopology.Stations.Select(s => new MapStationTile(s)));

    public ObservableCollection<GalleryChannelTile> GalleryChannels { get; } = new();

    public IRelayCommand<string> NavigateWorkspaceCommand { get; private set; } = null!;
    public IRelayCommand<string> FocusStationCommand { get; private set; } = null!;
    public IRelayCommand FollowLiveCommand { get; private set; } = null!;

    private void InitializeOperatorWorkspace()
    {
        NavigateWorkspaceCommand = new RelayCommand<string>(page =>
        {
            if (int.TryParse(page, out int index) && index >= 0 && index < WorkspacePages.Count)
                SelectedWorkspaceIndex = index;
        });
        FocusStationCommand = new RelayCommand<string>(FocusStation);
        FollowLiveCommand = new RelayCommand(FollowLatest);
        foreach (var channel in ChannelCatalog.All)
            GalleryChannels.Add(new GalleryChannelTile(channel, OpenGalleryChannel));
        FocusStation("LOADING");
    }

    private void FocusStation(string? stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId)) return;
        OperatorStation station;
        try { station = OperatorWorkspaceTopology.Require(stationId); }
        catch (ArgumentException) { return; }
        SelectedMapStation = station.StationId;
        foreach (var node in MapStations) node.IsFocused = node.Id == stationId;
        var existing = SelectedChannel;
        if (existing is null || !station.ChannelIds.Contains(existing.ChannelId, StringComparer.Ordinal))
            SelectedChannel = Channels.First(c => c.ChannelId == station.ChannelIds[0]);
        MapSelectionSummary = station.DisplayName + " · " + string.Join(", ", station.ChannelIds) +
            " · 设备 " + string.Join(", ", station.DeviceIds);
    }

    private void SyncStationFromChannel(ChannelTile? selected)
    {
        if (selected is null) return;
        var station = OperatorWorkspaceTopology.Stations.First(x => x.ChannelIds.Contains(
            selected.ChannelId, StringComparer.Ordinal));
        SelectedMapStation = station.StationId;
        foreach (var node in MapStations) node.IsFocused = node.Id == station.StationId;
        MapSelectionSummary = station.DisplayName + " · " + selected.ChannelId + " · " +
            selected.Device + " · PLC " + selected.PlcAddress + " · " + selected.State;
    }

    private void OpenGalleryChannel(string channelId)
    {
        SelectedChannel = Channels.First(c => c.ChannelId == channelId);
        SelectedWorkspaceIndex = 1; // Map details include large verified preview + ROI editing.
    }

    private void FollowLatest()
    {
        _pinnedWorkpieceId = null;
        _synchronizingHistory = true;
        SelectedHistoryItem = null;
        _synchronizingHistory = false;
        RefreshViewedWorkpiece();
    }

    partial void OnSelectedHistoryItemChanged(WorkpieceHistoryTile? value)
    {
        if (_synchronizingHistory) return;
        _pinnedWorkpieceId = value?.WorkpieceId;
        RefreshViewedWorkpiece();
        if (value is not null && !_suppressHistoryGalleryNavigation) SelectedWorkspaceIndex = 2;
    }

    partial void OnSelectedWorkspaceIndexChanged(int value)
    {
        if (value == 2) RefreshGallery();
        if (value == 6) _ = RefreshReportsAsync();
    }

    private Guid? ViewedWorkpieceId =>
        _pinnedWorkpieceId ?? _lastProjection?.Workpiece?.WorkpieceId;

    private void RefreshViewedWorkpiece()
    {
        var id = ViewedWorkpieceId;
        ViewedWorkpieceLabel = id is null ? "无可用工件" :
            _pinnedWorkpieceId is not null
                ? "历史工件 · " + id.Value.ToString("D") + " · 已固定"
                : "实时跟随 · " + id.Value.ToString("D");
        if (_lastProjection is { } projection)
            RequestPreview(projection);
        RefreshGallery();
        RefreshCaptureDebugSelection();
    }

    /// <summary>
    /// Keep a selected WAL WorkpieceId stable through 350ms Core updates.
    /// Repeated barcodes are deliberately not used as keys.
    /// </summary>
    private void UpdateHistorySelection(ProductionOverview history)
    {
        var tiles = history.Recent.Select(x => new WorkpieceHistoryTile(x.WorkpieceId,
            x.WorkpieceId.ToString("N")[..8], x.Barcode,
            "Epoch " + x.Epoch, x.CapturedChannels + "/11",
            x.Final switch
            {
                FinalDisposition.Good => "GOOD",
                FinalDisposition.Reject => "NG",
                _ => "WAIT / HOLD"
            },
            x.CreatedUtc.ToLocalTime().ToString("HH:mm:ss"))).ToArray();
        if (RecentWorkpieces.SequenceEqual(tiles)) return;
        _synchronizingHistory = true;
        try
        {
            RecentWorkpieces.Clear();
            foreach (var tile in tiles) RecentWorkpieces.Add(tile);
            if (_pinnedWorkpieceId is { } pinned)
            {
                var match = tiles.FirstOrDefault(x => x.WorkpieceId == pinned);
                if (match is null) _pinnedWorkpieceId = null; // No untrusted stale history display.
                SelectedHistoryItem = match;
            }
        }
        finally { _synchronizingHistory = false; }
        RefreshViewedWorkpiece();
    }

    /// <summary>Called after a new trusted Core snapshot has updated Channels and history.</summary>
    private void UpdateOperatorWorkspace(MonitorProjection projection)
    {
        var conveyor = projection.Conveyor;
        LiveConveyor.Clear();
        if (conveyor is not null)
        {
            ConveyorSummary = $"逐料运动 Tick {conveyor.Tick} · 上料 {conveyor.LoadedCount} · " +
                $"已出料 {conveyor.ExitedCount} · 在途 {conveyor.Active.Count}" +
                (conveyor.StuckSensors.Count == 0 ? "" :
                    " · 卡料模拟 " + string.Join(",", conveyor.StuckSensors));
            foreach (var part in conveyor.Active.OrderBy(p => p.Token))
                LiveConveyor.Add(new LiveConveyorTile(
                    part.WorkpieceId.ToString("N")[..8] + " · #" + part.Token,
                    part.StationId, part.CompletedStations + "/7 站 · " +
                        part.CapturedChannels + "/11 通道",
                    part.Phase, part.SensorTriggered ? "传感器已触发" : "等待传感器"));
        }
        else
            ConveyorSummary = "Master SIM_FULL 的机械在途数据尚未接入本机 Core";
        foreach (var station in MapStations)
        {
            var parts = conveyor?.Active.Where(p => p.StationId == station.Id).ToArray();
            if (parts is { Length: > 0 })
                station.Status = "工件 #" + parts[0].Token +
                    " · " + parts[0].CapturedChannels + "/11";
            else if (conveyor is not null)
                station.Status = "空闲 · 机械实时仿真";
            else
            {
                var relevant = station.Channels.Split(" · ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => Channels.First(c => c.ChannelId == id)).ToArray();
                var captured = relevant.Count(c =>
                    c.State.StartsWith("SIM_OK", StringComparison.Ordinal) ||
                    c.State.StartsWith("SIM_NG", StringComparison.Ordinal));
                station.Status = captured == 0 ? "等待采集" :
                    captured + "/" + relevant.Length + " 已检测";
            }
        }
        SyncStationFromChannel(SelectedChannel);
        RefreshViewedWorkpiece();
    }

    private void ClearOperatorWorkspace()
    {
        _pinnedWorkpieceId = null;
        _synchronizingHistory = true;
        SelectedHistoryItem = null;
        _synchronizingHistory = false;
        _galleryLoadedWorkpieceId = null;
        _galleryGeneration++;
        _galleryCancellation?.Cancel();
        _galleryCancellation?.Dispose();
        _galleryCancellation = null;
        foreach (var tile in GalleryChannels) tile.Show(null, "Core IPC 离线");
        foreach (var station in MapStations) station.Status = "Core 离线";
        LiveConveyor.Clear();
        ConveyorSummary = "机械状态 IPC 离线";
        GallerySummary = "Core 已断开，图库不可用";
        ViewedWorkpieceLabel = "无可用工件";
    }

    private void RefreshGallery()
    {
        if (SelectedWorkspaceIndex != 2) return;
        var workpieceId = ViewedWorkpieceId;
        if (_galleryLoadedWorkpieceId == workpieceId) return;
        _galleryLoadedWorkpieceId = workpieceId;
        int generation = ++_galleryGeneration;
        _galleryCancellation?.Cancel();
        _galleryCancellation?.Dispose();
        _galleryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        foreach (var tile in GalleryChannels)
        {
            tile.Show(null, workpieceId is null ? "尚无工件" : "正在从 Core 读取 PNG...");
            // A historical PNG has a verified WorkpieceId, but v0.1 IPC has no
            // per-channel historical outcome contract: never bind latest item's status.
            tile.ChannelState = _lastProjection?.Workpiece?.WorkpieceId == workpieceId
                ? Channels.First(c => c.ChannelId == tile.ChannelId).State
                : "历史 PNG · 本件逐通道判定未开放 IPC";
        }
        if (workpieceId is null) return;
        GallerySummary = "WorkpieceId " + workpieceId.Value.ToString("D") +
            " · 逐通道向 Master/Slave 本机 IPC 请求 SHA256 校验 PNG";
        _ = LoadGalleryAsync(workpieceId.Value, generation, _galleryCancellation.Token);
    }

    private async Task LoadGalleryAsync(Guid workpieceId, int generation, CancellationToken ct)
    {
        // Local SIM_FULL runs both Core endpoints on 127.0.0.1. A remote-only
        // physical node stays unavailable rather than substituting stale images.
        using var throttler = new SemaphoreSlim(3);
        var jobs = GalleryChannels.Select(async tile =>
        {
            try
            {
                await throttler.WaitAsync(ct);
                try
                {
                    var data = await new FramePreviewClient(
                        OperatorWorkspaceTopology.FramePreviewPort(tile.ChannelId))
                        .FetchAsync(workpieceId, tile.ChannelId, ct);
                    Bitmap? bitmap = null;
                    if (data is not null)
                    {
                        using var memory = new MemoryStream(data, writable: false);
                        bitmap = new Bitmap(memory);
                    }
                    var status = data is null ? "尚无已校验 PNG / 对端不可用"
                        : "真实 Core PNG · " + data.Length + " bytes";
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation != _galleryGeneration || _shutdown.IsCancellationRequested)
                        {
                            bitmap?.Dispose();
                            return;
                        }
                        tile.Show(bitmap, status);
                    });
                }
                finally { throttler.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or
                ArgumentException or OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _galleryGeneration && !_shutdown.IsCancellationRequested)
                        tile.Show(null, "PNG 读取失败：" + ex.GetType().Name);
                });
            }
        }).ToArray();
        try { await Task.WhenAll(jobs); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void StopOperatorWorkspace()
    {
        _galleryGeneration++;
        _galleryCancellation?.Cancel();
        _galleryCancellation?.Dispose();
        _galleryCancellation = null;
        foreach (var gallery in GalleryChannels) gallery.Dispose();
    }
}
