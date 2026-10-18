using AOI.Contracts;
using System.Text;

namespace AOI.Ui.Client;

public enum ReportExportOutcome
{
    Saved,
    Cancelled,
    Unavailable,
    Failed
}

public sealed record ReportExportResult(ReportExportOutcome Outcome, string? Path, string Message);

/// <summary>
/// Operator CSV export flow: pick destination -> write. Cancelling the dialog writes nothing,
/// and a write failure reports the real reason instead of silently succeeding.
/// </summary>
public static class ReportExportService
{
    public const string UnavailableMessage = "报表导出不可用：保存对话框尚未初始化。";
    public const string CancelledMessage = "已取消导出，未写入任何文件。";

    public static async Task<ReportExportResult> ExportCsvAsync(
        IReportSavePicker? picker,
        SimProductionReports data,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (picker is null)
            return new ReportExportResult(ReportExportOutcome.Unavailable, null, UnavailableMessage);

        string? path;
        try
        {
            path = await picker.PickAsync(ProductionReportCsv.SuggestedFileName(now), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ReportExportResult(ReportExportOutcome.Cancelled, null, CancelledMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new ReportExportResult(ReportExportOutcome.Failed, null,
                "报表导出失败：无法打开保存对话框 —— " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(path))
            return new ReportExportResult(ReportExportOutcome.Cancelled, null, CancelledMessage);

        try
        {
            string csv = ProductionReportCsv.Build(data);
            await File.WriteAllTextAsync(path, csv,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
                .ConfigureAwait(false);
            return new ReportExportResult(ReportExportOutcome.Saved, path,
                "SIM_ONLY 报表 CSV 已保存：" + path);
        }
        catch (OperationCanceledException)
        {
            return new ReportExportResult(ReportExportOutcome.Cancelled, path, CancelledMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new ReportExportResult(ReportExportOutcome.Failed, path,
                "报表导出失败：" + path + " —— " + ex.Message);
        }
    }
}
