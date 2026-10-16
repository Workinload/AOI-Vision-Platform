using AOI.Contracts;
using AOI.Domain;
using AOI.Ui.Client;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AOI.Ui.Avalonia;

public partial class ChannelTile : ObservableObject
{
    public string ChannelId { get; }
    public string StationDetails { get; }

    [ObservableProperty] private string state = "等待数据";
    [ObservableProperty] private string frameLabel = "未连接至真实帧采集";
    public ChannelTile(ChannelDefinition channel)
    {
        ChannelId = channel.ChannelId;
        StationDetails = $"{channel.StationId} · {channel.PhysicalDeviceId}";
    }
    public void Update(ChannelSnapshot channel)
    {
        State = channel.Status;
        FrameLabel = channel.IsSimulated ? "OpenCV 合成像素 · 仿真" : "未接入真实图像";
    }
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _role;
    private readonly bool _once;
    private Task? _subscribeTask;

    public ObservableCollection<ChannelTile> Channels { get; } =
        new(ChannelCatalog.All.Select(x => new ChannelTile(x)));

    public string NodeLabel => _role == "slave" ? "SLAVE · 从机" : "MASTER · 主机";
    [ObservableProperty] private string connectionState = "等待 Core IPC 连接...";
    [ObservableProperty] private string productionState = "SIM_LIGHT · 尚未连接";
    [ObservableProperty] private string latestIdentity = "WorkpieceId：等待上料事件";

    public MainWindowViewModel(string role, bool once)
    {
        _role = role;
        _once = once;
    }

    public void Start()
    {
        if (_subscribeTask is not null) return;
        _subscribeTask = Task.Run(async () =>
        {
            var client = new SnapshotClient(_role == "slave" ? 19622 : 19621);
            try
            {
                await foreach (var snapshot in client.SubscribeAsync(_shutdown.Token))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ConnectionState = $"Core IPC 已连接 · {snapshot.UpdatedUtc:HH:mm:ss} UTC";
                        ProductionState = snapshot.MachineState + (snapshot.LatestFinal is null
                            ? " · 等待结果" : $" · {snapshot.LatestFinal.Disposition} ({snapshot.LatestFinal.Proof})");
                        LatestIdentity = snapshot.LatestWorkpiece is null
                            ? "尚无上料工件" : $"WorkpieceId：{snapshot.LatestWorkpiece.WorkpieceId} · Barcode：{snapshot.LatestWorkpiece.Barcode}";
                        foreach (var info in snapshot.Channels)
                        {
                            var tile = Channels.FirstOrDefault(x => x.ChannelId == info.ChannelId);
                            tile?.Update(info);
                        }
                        if (_once)
                        {
                            Console.WriteLine($"UI_HEADLESS_SNAPSHOT_OK role={_role} channels={snapshot.Channels.Count} state={snapshot.MachineState}");
                            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                                desktop.Shutdown();
                        }
                    });
                    if (_once) break;
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }, _shutdown.Token);
    }

    public void Stop() => _shutdown.Cancel();
}
