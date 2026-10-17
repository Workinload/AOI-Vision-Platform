using System.Globalization;
using System.Text;

namespace AOI.Diagnostics;

/// <summary>Portable operator/A5 timeline evidence exports; no Core database mutations.</summary>
public static class ProductionTimelineExports
{
    public static void SaveJson(TimingTimeline timeline, string filename) =>
        ProductionReportExports.SaveJson(timeline, filename);

    public static void SaveComparisonJson(TimingComparison comparison, string filename) =>
        ProductionReportExports.SaveJson(comparison, filename);

    public static void SaveCsv(TimingTimeline timeline, string filename)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var csv = new StringBuilder();
        Row(csv, "Utc", "Node", "Origin", "Sequence", "Stage", "WorkpieceId",
            "TraceId", "Epoch", "ReleaseId", "CaptureId", "Device", "Channel",
            "AttemptNo", "FrameId", "FinsOperationId", "MessageId", "DmAddress",
            "PlcValue", "FinsSid", "ProcessRunId", "StopwatchTick", "StopwatchFrequency",
            "Status", "Details");
        foreach (var e in timeline.Events)
            Row(csv, e.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                e.NodeId, e.Origin, e.Sequence.ToString(CultureInfo.InvariantCulture),
                e.Stage.ToString(), e.WorkpieceId.ToString("D"), e.TraceId.ToString("D"),
                e.Epoch.ToString(CultureInfo.InvariantCulture), e.ReleaseId,
                e.CaptureId?.ToString("D"), e.PhysicalDeviceId, e.ChannelId,
                e.AttemptNo?.ToString(CultureInfo.InvariantCulture), e.FrameId?.ToString("D"),
                e.OperationId?.ToString("D"), e.MessageId?.ToString("D"), e.PlcDmAddress,
                e.PlcValue?.ToString(CultureInfo.InvariantCulture),
                e.FinsSid?.ToString(CultureInfo.InvariantCulture), e.ProcessRunId?.ToString("D"),
                e.MonotonicTick?.ToString(CultureInfo.InvariantCulture),
                e.TickFrequency?.ToString(CultureInfo.InvariantCulture), e.Status, e.Details);
        Write(filename, csv.ToString());
    }

    private static void Row(StringBuilder sb, params string?[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i != 0) sb.Append(',');
            string value = cells[i] ?? "";
            if (value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@')
                value = "'" + value;
            sb.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        sb.Append("\r\n");
    }

    private static void Write(string filename, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        string full = Path.GetFullPath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
