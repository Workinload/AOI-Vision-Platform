using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AOI.Ui.Avalonia;

public partial class ChannelTile : ObservableObject
{
    public string ChannelId { get; }
    public string StationDetails { get; }
    public string PlcAddress { get; }
    public string Owner { get; }
    public string Device { get; }
    public string Group { get; }
    [ObservableProperty] private string state = "NO_DATA";
    [ObservableProperty] private string frameLabel = "无图像数据";
    [ObservableProperty] private string details = "等待 Core IPC";
    [ObservableProperty] private bool isSelected;

    public ChannelTile(ChannelDefinition definition)
    {
        ChannelId = definition.ChannelId;
        Device = definition.PhysicalDeviceId;
        PlcAddress = definition.PlcSignalAddress;
        Owner = definition.OwnerRole.ToString().ToUpperInvariant();
        Group = definition.SharedFrameGroup ?? "AREA";
        StationDetails = definition.StationId + " / " + definition.PhysicalDeviceId;
    }

    public void Update(ChannelMonitor channel)
    {
        State = channel.Status;
        FrameLabel = channel.IsCurrentWorkpiece && channel.IsSimulated
            ? "OpenCV 仿真采集（无真实帧预览）" : "无可用真实帧";
        Details = channel.Detail;
    }

    public void Reset()
    {
        State = "IPC_OFFLINE";
        FrameLabel = "实时数据不可用";
        Details = "IPC 中断，状态已失效";
    }
}

/// <summary>Human-readable read-only row generated solely from Core IPC.</summary>
public sealed record WorkpieceHistoryTile(
    string ShortId, string Barcode, string Epoch, string Channels, string Decision, string LocalTime);

/// <summary>Engineering mapping only; no claim of live PLC signal reads.</summary>
public sealed record PlcSignalTile(
    string Id, string DmAddress, string Station, string Host, string Direction,
    string DataType, string Purpose, string Validation, string Conflict);

public sealed record FieldGateTile(string GateId, string Topic, string Status, string RequiredEvidence);
public sealed record HoldTile(string ShortId, string ReasonCode, string Description, string RaisedUtc);
public sealed record ProductStationTile(
    string StationId, string Channels, string BrightThreshold, string MinimumBrightFraction);

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _role;
    private readonly bool _once;
    private Task? _subscribeTask;

    public ObservableCollection<ChannelTile> Channels { get; } =
        new(ChannelCatalog.All.Select(x => new ChannelTile(x)));
    public ObservableCollection<WorkpieceHistoryTile> RecentWorkpieces { get; } = new();
    public ObservableCollection<PlcSignalTile> PlcSignals { get; } = new();
    public ObservableCollection<FieldGateTile> PlcGates { get; } = new();
    public ObservableCollection<HoldTile> HeldWorkpieces { get; } = new();
    public ObservableCollection<ProductStationTile> ProductStations { get; } = new();
    [ObservableProperty] private string activeProductLabel = "SIM_LIGHT 默认仿真产品 · 无已发布 A6 方案";
    [ObservableProperty] private string activeProductRelease = "无独立方案发布 / 未通过双机换型";
    [ObservableProperty] private string holdNotice = "等待 Core 的持久化异常记录";
    [ObservableProperty] private string engineeringRegistryState = "待接收 Core 工程映射 · REAL 写入禁止";
    [ObservableProperty] private PlcSignalTile? selectedPlcSignal;
    [ObservableProperty] private FieldGateTile? selectedFieldGate;
    [ObservableProperty] private string productionCounters = "生产统计：等待 Core IPC";
    [ObservableProperty] private string historyNote = "暂无可核验的生产历史";

    public string NodeLabel => _role == "slave" ? "SLAVE · 从机" : "MASTER · 主机";
    public string ChannelCount => "11 逻辑通道 / 9 物理相机 / +1 整机";
    [ObservableProperty] private string connectionState = "等待 Core IPC 连接...";
    [ObservableProperty] private string productionState = "SIM_ONLY · 尚未连接";
    [ObservableProperty] private string latestIdentity = "WorkpieceId：等待上料事件";
    [ObservableProperty] private string safetyStatus = "SIM_ONLY · 真实硬件写入禁止";
    [ObservableProperty] private string selectedDescription = "点击设备卡或 PLC 表行选择通道";
    [ObservableProperty] private ChannelTile? selectedChannel;
    [ObservableProperty] private string roiX = "0.1";
    [ObservableProperty] private string roiY = "0.1";
    [ObservableProperty] private string roiWidth = "0.8";
    [ObservableProperty] private string roiHeight = "0.8";
    [ObservableProperty] private string roiFeedback = "ROI 仅会话草稿，不会保存、不下发";
    [ObservableProperty] private Bitmap? selectedPreview;
    [ObservableProperty] private string previewNotice = "请选择本机已采集通道查看无损 PNG";
    private MonitorProjection? _lastProjection;
    private string? _lastPreviewKey;
    private int _previewGeneration;
    private long _roiRevision;
    private bool _roiSnapshotLoaded;
    private readonly Dictionary<string, RoiDraftState> _savedRoiDrafts = new(StringComparer.Ordinal);
    public string SelectedChannelName => SelectedChannel?.ChannelId ?? "未选择通道";

    public MainWindowViewModel(string role, bool once)
    {
        _role = role;
        _once = once;
        SelectChannelCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<ChannelTile?>(SelectChannel);
        ValidateRoiCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ValidateRoi);
        SelectedChannel = Channels[0];
    }

    public CommunityToolkit.Mvvm.Input.IRelayCommand<ChannelTile?> SelectChannelCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand ValidateRoiCommand { get; }

    partial void OnSelectedChannelChanged(ChannelTile? value)
    {
        foreach (var channel in Channels) channel.IsSelected = ReferenceEquals(channel, value);
        SelectedDescription = value is null ? "未选择通道" :
            $"{value.ChannelId} · {value.StationDetails} · {value.Owner} · PLC {value.PlcAddress} · {value.Group}";
        OnPropertyChanged(nameof(SelectedChannelName));
        LoadRoiForSelection();
        if (_lastProjection is not null) RequestPreview(_lastProjection);
    }

    partial void OnSelectedPlcSignalChanged(PlcSignalTile? value)
    {
        if (value is null) return;
        // Exact DM mapping, never fuzzy match by barcode, name or station.
        var channel = Channels.FirstOrDefault(c =>
            string.Equals(c.PlcAddress, value.DmAddress, StringComparison.Ordinal));
        if (channel is not null && !ReferenceEquals(SelectedChannel, channel))
            SelectedChannel = channel;
        SelectedDescription = $"{value.Id}: {value.Purpose} | {value.DmAddress} | " +
            $"工位 {value.Station} | {value.Validation} | {value.Conflict} | 工程声明，非现场实时值";
    }

    private void SelectChannel(ChannelTile? value) => SelectedChannel = value;

    private void LoadRoiForSelection()
    {
        if (SelectedChannel is null) return;
        var id = SelectedChannel.ChannelId;
        if (_savedRoiDrafts.TryGetValue(id, out var draft))
        {
            _roiRevision = draft.Revision;
            var format = System.Globalization.CultureInfo.InvariantCulture;
            RoiX = draft.X.ToString(format);
            RoiY = draft.Y.ToString(format);
            RoiWidth = draft.Width.ToString(format);
            RoiHeight = draft.Height.ToString(format);
            RoiFeedback = $"已加载 Core WAL 草稿 rev{draft.Revision} · {id} · 仍未发布或生效";
        }
        else
        {
            _roiRevision = 0;
            RoiX = "0.1"; RoiY = "0.1"; RoiWidth = "0.8"; RoiHeight = "0.8";
            RoiFeedback = $"无已保存草稿 · {id} · rev0";
        }
    }

    private void ValidateRoi()
    {
        var selected = SelectedChannel;
        if (selected is null) { RoiFeedback = "请先选择通道"; return; }
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(RoiX, System.Globalization.NumberStyles.Float, culture, out var x) ||
            !double.TryParse(RoiY, System.Globalization.NumberStyles.Float, culture, out var y) ||
            !double.TryParse(RoiWidth, System.Globalization.NumberStyles.Float, culture, out var width) ||
            !double.TryParse(RoiHeight, System.Globalization.NumberStyles.Float, culture, out var height))
        {
            RoiFeedback = "ROI 坐标必须是小数（英文小数点）"; return;
        }
        try
        {
            var role = _role == "slave" ? MachineRole.Slave : MachineRole.Master;
            if (ChannelCatalog.Require(selected.ChannelId).OwnerRole != role)
            {
                RoiFeedback = "禁止保存：此通道属于对端 Core"; return;
            }
            new RoiDraft(selected.ChannelId, x, y, width, height).Validate();
            var edit = new RoiDraftEdit(selected.ChannelId, x, y, width, height,
                _roiRevision, "sim_operator");
            edit.Validate();
            var token = Environment.GetEnvironmentVariable("AOI_SIM_CONFIG_TOKEN");
            if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
            {
                RoiFeedback = "本地 ROI 校验通过，但未配置 SIM_ONLY 编辑会话；没有保存。";
                return;
            }
            RoiFeedback = $"正在保存 ROI 草稿 · {selected.ChannelId} rev{_roiRevision}...";
            _ = SaveRoiAsync(edit, token, role);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            RoiFeedback = "ROI 无效：" + ex.Message;
        }
    }

    private async Task SaveRoiAsync(RoiDraftEdit edit, string token, MachineRole role)
    {
        RoiDraftSaveReply? reply = null;
        string? error = null;
        try
        {
            reply = await new RoiDraftConfigClient(role == MachineRole.Master ? 19641 : 19642)
                .SaveAsync(edit, token, _shutdown.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
            or InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            error = ex.Message;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdown.IsCancellationRequested) return;
            if (reply is { Accepted: true, Saved: not null })
            {
                _savedRoiDrafts[edit.ChannelId] = reply.Saved;
                if (SelectedChannel?.ChannelId == edit.ChannelId)
                {
                    _roiRevision = reply.Saved.Revision;
                    RoiFeedback = $"SIM_ONLY ROI 草稿已持久保存 · rev{_roiRevision} · 尚未发布、不会改变当前检测";
                }
            }
            else if (SelectedChannel?.ChannelId == edit.ChannelId)
                RoiFeedback = "草稿未保存：" + (reply?.Error ?? error ?? "Core IPC 拒绝");
        });
    }

    public void Start()
    {
        if (_subscribeTask is not null) return;
        _subscribeTask = Task.Run(async () =>
        {
            var role = _role == "slave" ? MachineRole.Slave : MachineRole.Master;
            var client = new SnapshotClient(role == MachineRole.Slave ? 19622 : 19621);
            try
            {
                await foreach (var snapshot in client.SubscribeAsync(_shutdown.Token))
                {
                    MonitorProjection projection;
                    try
                    {
                        projection = MonitorProjection.FromSnapshot(snapshot, role, DateTimeOffset.UtcNow);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                    {
                        Dispatcher.UIThread.Post(() => MarkOffline("IPC 数据拒绝：" + ex.Message));
                        continue;
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        _lastAcceptedUtc = DateTimeOffset.UtcNow;
                        ConnectionState = $"Core IPC 已连接 · {projection.UpdatedUtc:HH:mm:ss} UTC";
                        SafetyStatus = projection.SafetyNotice;
                        ProductionState = projection.MachineState + (projection.Final is null
                            ? " · 等待结果" : $" · {projection.Final.Disposition} ({projection.Final.Proof})");
                        LatestIdentity = projection.Workpiece is null ? "尚无上料工件" :
                            $"WorkpieceId：{projection.Workpiece.WorkpieceId} · Barcode：{projection.Workpiece.Barcode}";
                        foreach (var info in projection.Channels)
                            Channels.First(x => x.ChannelId == info.Definition.ChannelId).Update(info);
                        RequestPreview(projection);
                        if (projection.RoiDrafts is { } saved)
                        {
                            foreach (var roi in saved)
                                if (!_savedRoiDrafts.TryGetValue(roi.ChannelId, out var oldRoi) ||
                                    oldRoi.Revision < roi.Revision)
                                    _savedRoiDrafts[roi.ChannelId] = roi;
                            if (!_roiSnapshotLoaded)
                            {
                                _roiSnapshotLoaded = true;
                                LoadRoiForSelection();
                            }
                        }
                        if (projection.ActiveProduct is { } product)
                        {
                            ActiveProductLabel = $"SIM_ONLY · {product.SolutionId} · Epoch {product.Epoch}";
                            ActiveProductRelease = $"双机离线已提交 / 持续安全 HOLD · {product.ReleaseId} · SHA256 {product.ContentSha256[..12]}…";
                            ProductStations.Clear();
                            foreach (var station in product.Stations.OrderBy(s => s.StationId, StringComparer.Ordinal))
                                ProductStations.Add(new ProductStationTile(station.StationId,
                                    string.Join(", ", station.ChannelIds),
                                    station.BrightThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    station.MinimumBrightFraction.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
                        }
                        else
                        {
                            ActiveProductLabel = "SIM_LIGHT 默认仿真产品 · 无已发布 A6 方案";
                            ActiveProductRelease = "未执行 A6 双机离线方案准备和安全提交";
                            ProductStations.Clear();
                        }
                        if (projection.Holds is { } holds)
                        {
                            HoldNotice = holds.ActiveCount == 0
                                ? "当前无持久化 HOLD（仅 SIM_ONLY 数据）"
                                : $"持久化 HOLD：{holds.ActiveCount} 件 · 未授权清除 / 不允许自动放行";
                            HeldWorkpieces.Clear();
                            foreach (var hold in holds.Recent)
                                HeldWorkpieces.Add(new HoldTile(
                                    hold.WorkpieceId.ToString("N")[..8],
                                    hold.ReasonCode, hold.Detail,
                                    hold.RaisedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                            if (holds.ActiveCount > 0)
                                ProductionState += $" · HOLD {holds.ActiveCount}";
                        }
                        else
                        {
                            HoldNotice = "Core 未提供可信异常状态";
                            HeldWorkpieces.Clear();
                        }
                        if (projection.PlcEngineering is { } engineering)
                        {
                            EngineeringRegistryState =
                                $"静态工程台账 · {engineering.Signals.Count} 条信号 / {engineering.Gates.Count} 项 FG · 非现场读值";
                            if (PlcSignals.Count == 0)
                            {
                                foreach (var signal in engineering.Signals)
                                    PlcSignals.Add(new PlcSignalTile(
                                        signal.SignalId, signal.DmAddress, signal.StationId,
                                        signal.HostRole, signal.Direction, signal.DataType,
                                        signal.Description, signal.ValidationState, signal.ConflictGroup));
                                foreach (var gate in engineering.Gates)
                                    PlcGates.Add(new FieldGateTile(
                                        gate.GateId, gate.Topic, gate.Status, gate.RequiredEvidence));
                            }
                        }
                        else
                        {
                            EngineeringRegistryState = "Core 未提供 PLC 工程台账（不推断为现场有效）";
                            PlcSignals.Clear();
                            PlcGates.Clear();
                        }
                        if (projection.Production is { } history)
                        {
                            ProductionCounters =
                                $"总数 {history.Total}  /  GOOD {history.Good}  /  NG {history.Reject}  /  待完成 {history.Unfinished}";
                            HistoryNote = $"最近 {history.Recent.Count} 件 · 只读 WAL · WorkpieceId 不等于 Barcode";
                            RecentWorkpieces.Clear();
                            foreach (var item in history.Recent)
                            {
                                var state = item.Final switch
                                {
                                    FinalDisposition.Good => "GOOD",
                                    FinalDisposition.Reject => "NG",
                                    _ => "WAIT / HOLD"
                                };
                                RecentWorkpieces.Add(new WorkpieceHistoryTile(
                                    item.WorkpieceId.ToString("N")[..8], item.Barcode,
                                    $"Epoch {item.Epoch}", $"{item.CapturedChannels}/11",
                                    state, item.CreatedUtc.ToLocalTime().ToString("HH:mm:ss")));
                            }
                        }
                        if (_once)
                        {
                            if (projection.PlcEngineering is { } catalog)
                                Console.WriteLine($"UI_PLC_ENGINEERING_OK role={_role} signals={catalog.Signals.Count} gates={catalog.Gates.Count} live={catalog.IsFieldLive}");
                            if (projection.ActiveProduct is { } published)
                                Console.WriteLine($"UI_SIM_PRODUCT_OK role={_role} epoch={published.Epoch} release={published.ReleaseId} stations={published.Stations.Count}");
                            Console.WriteLine($"UI_HEADLESS_SNAPSHOT_OK role={_role} channels={projection.Channels.Count} state={projection.MachineState}");
                            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                                desktop.Shutdown();
                        }
                    });
                    if (_once) break;
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException)
            {
                Dispatcher.UIThread.Post(() => MarkOffline("IPC 故障：" + ex.Message));
            }
        }, _shutdown.Token);
        if (!_once) _ = WatchFreshnessAsync();
    }

    private void ClearPreview(string reason)
    {
        _previewGeneration++;
        _lastPreviewKey = null;
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        PreviewNotice = reason;
    }

    private void RequestPreview(MonitorProjection projection)
    {
        _lastProjection = projection;
        var selected = SelectedChannel;
        if (selected is null || projection.Workpiece is null)
        {
            ClearPreview("暂无工件或未选择通道");
            return;
        }
        var item = projection.Channels.First(c => c.Definition.ChannelId == selected.ChannelId);
        if (item.Definition.OwnerRole != projection.Role)
        {
            ClearPreview("图像属于对端 Core，请在另一台设备的窗口查看");
            return;
        }
        if (!item.IsCurrentWorkpiece || !item.IsSimulated)
        {
            ClearPreview("本工件尚未采集此通道的 OpenCV 图像");
            return;
        }
        string key = $"{projection.Workpiece.WorkpieceId:D}|{selected.ChannelId}|{item.UpdatedUtc:O}";
        if (key == _lastPreviewKey) return;
        _lastPreviewKey = key;
        int generation = ++_previewGeneration;
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        PreviewNotice = "正在经本机 Core 校验图像 SHA256...";
        _ = FetchPreviewAsync(projection.Workpiece.WorkpieceId, selected.ChannelId,
            projection.Role, generation);
    }

    private async Task FetchPreviewAsync(Guid workpieceId, string channelId,
        MachineRole role, int generation)
    {
        Bitmap? bitmap = null;
        string notice;
        try
        {
            byte[]? png = await new FramePreviewClient(role == MachineRole.Master ? 19631 : 19632)
                .FetchAsync(workpieceId, channelId, _shutdown.Token);
            if (png is null)
                notice = "图像不可用／尚未入库或完整性校验失败（不显示旧图）";
            else
            {
                using var stream = new MemoryStream(png, writable: false);
                bitmap = new Bitmap(stream);
                notice = $"已验证无损 PNG · {channelId} · {png.Length} bytes · SIM_ONLY";
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
            or OperationCanceledException or ArgumentException)
        {
            notice = "本机只读图像 IPC 不可用：" + ex.Message;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _previewGeneration || _shutdown.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }
            SelectedPreview?.Dispose();
            SelectedPreview = bitmap;
            PreviewNotice = notice;
        });
    }

    private async Task WatchFreshnessAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try { await Task.Delay(1200, _shutdown.Token); }
            catch (OperationCanceledException) { break; }
            Dispatcher.UIThread.Post(() =>
            {
                // The client may be reconnecting; never leave a stale successful snapshot on-screen.
                if (_lastAcceptedUtc != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - _lastAcceptedUtc > TimeSpan.FromSeconds(5))
                    MarkOffline("IPC 快照超时（>5s）");
            });
        }
    }

    private DateTimeOffset _lastAcceptedUtc = DateTimeOffset.MinValue;
    private void MarkOffline(string reason)
    {
        ConnectionState = reason;
        ProductionState = "IPC_OFFLINE · 未知生产状态";
        SafetyStatus = "禁止真实硬件写入 · 请核查 Core";
        LatestIdentity = "实时工件数据不可用";
        foreach (var channel in Channels) channel.Reset();
        ProductionCounters = "生产统计：IPC 离线，数据已失效";
        HistoryNote = "仅可查看在线 Core 的真实持久化数据";
        RecentWorkpieces.Clear();
        PlcSignals.Clear();
        PlcGates.Clear();
        SelectedPlcSignal = null;
        SelectedFieldGate = null;
        HeldWorkpieces.Clear();
        ProductStations.Clear();
        ActiveProductLabel = "产品方案 IPC 离线";
        ActiveProductRelease = "未知激活状态；禁止自动换型或出料";
        HoldNotice = "HOLD 状态离线 · 不允许基于旧数据放行";
        EngineeringRegistryState = "PLC 工程映射离线 · 无真实现场读值";
        _lastProjection = null;
        _roiSnapshotLoaded = false;
        _savedRoiDrafts.Clear();
        _roiRevision = 0;
        RoiFeedback = "IPC 离线，SIM_ONLY ROI 草稿保存状态未知；禁止复用旧版号";
        ClearPreview("IPC 中断，图像已失效");
        _lastAcceptedUtc = DateTimeOffset.MinValue;
    }

    public void Stop()
    {
        _shutdown.Cancel();
        ClearPreview("窗口已关闭");
    }
}
