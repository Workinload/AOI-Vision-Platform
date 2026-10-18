using AOI.Contracts;
using AOI.Ui.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace AOI.Ui.Avalonia;

public sealed record ReportWorkpieceTile(
    string ShortId, string Barcode, string Verdict, string Channels, string Time, string Reason);

public partial class MainWindowViewModel
{
    [ObservableProperty] private string dailyReportText = "今日报告：尚未读取 Master WAL";
    [ObservableProperty] private string weeklyReportText = "本周报告：尚未读取 Master WAL";
    [ObservableProperty] private string reportStatus = "点击刷新，读取 Agent07 真实 Master/Slave 追溯数据（只读）";
    public ObservableCollection<ReportWorkpieceTile> ReportHistory { get; } = new();

    private SimProductionReports? _lastReport;

    [RelayCommand]
    private async Task RefreshReportsAsync()
    {
        ReportStatus = "正在查询 Master Core 中的真实生产记录...";
        try
        {
            var data = await new SimReportClient().ReadAsync(_shutdown.Token);
            if (_shutdown.IsCancellationRequested) return;
            _lastReport = data;
            DailyReportText = PeriodText("今日", data.Today);
            WeeklyReportText = PeriodText("本周", data.ThisWeek);
            ReportHistory.Clear();
            foreach (var item in data.Recent)
                ReportHistory.Add(new ReportWorkpieceTile(
                    item.WorkpieceId.ToString("N")[..8],
                    item.Barcode, item.Disposition,
                    item.CapturedChannels + "/11",
                    item.RegisteredUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                    item.HoldReason ?? "—"));
            ReportStatus = $"已从 Master SQLite WAL 加载 {data.Recent.Count} 件 / " +
                $"总记录 {data.AllTimeMatches} · {data.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · SIM_ONLY";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or
            InvalidDataException or InvalidOperationException or ArgumentException or
            OperationCanceledException)
        {
            _lastReport = null;
            ReportHistory.Clear();
            DailyReportText = "今日报告：不可用";
            WeeklyReportText = "本周报告：不可用";
            ReportStatus = "Master 只读报表服务暂不可用：" + ex.Message;
        }
    }

    private static string PeriodText(string label, SimReportPeriod data)
    {
        string rate = data.GoodRate is null ? "—" :
            data.GoodRate.Value.ToString("P1", CultureInfo.CurrentCulture);
        return $"{label} · 工件 {data.Total} | GOOD {data.Good} | " +
            $"NG {data.Ng} | HOLD {data.Hold} | 待完成 {data.InProgress} | " +
            $"已完成良率 {rate}";
    }

    /// <summary>Native save-dialog seam, wired by MainWindow. Null in headless contexts.</summary>
    public AOI.Ui.Client.IReportSavePicker? ReportSavePicker { get; set; }

    [RelayCommand]
    private async Task ExportReportCsvAsync()
    {
        if (_lastReport is null)
        {
            await RefreshReportsAsync();
            if (_lastReport is null) return;
        }
        var result = await ReportExportService.ExportCsvAsync(
            ReportSavePicker, _lastReport, DateTimeOffset.Now, _shutdown.Token);
        if (result.Outcome == ReportExportOutcome.Cancelled && _shutdown.IsCancellationRequested)
            return;
        ReportStatus = result.Message;
    }

    private void ResetReportWorkspace()
    {
        _lastReport = null;
        ReportHistory.Clear();
        DailyReportText = "今日报告：Core 离线";
        WeeklyReportText = "本周报告：Core 离线";
        ReportStatus = "Core IPC 离线，请重新连接后刷新报告";
    }
}
