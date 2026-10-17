using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AOI.Diagnostics;

/// <summary>
/// Operator-selected, local CSV/JSON exports. Exports never modify Core WAL or image files.
/// CSV uses RFC-4180 quoting and escapes spreadsheet formula prefixes in untrusted barcodes/logs.
/// </summary>
public static class ProductionReportExports
{
    public static void SaveHistoryCsv(ProductionHistoryPage page, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(page);
        var csv = new StringBuilder();
        Row(csv, "WorkpieceId", "Barcode", "TraceId", "Epoch", "ReleaseId",
            "RegisteredUtc", "Disposition", "CapturedChannels", "HoldReason");
        foreach (var r in page.Rows)
            Row(csv, r.WorkpieceId.ToString("D"), r.Barcode, r.TraceId.ToString("D"),
                r.Epoch.ToString(CultureInfo.InvariantCulture), r.ReleaseId,
                r.RegisteredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                r.Disposition.ToString().ToUpperInvariant(),
                r.CapturedChannels.ToString(CultureInfo.InvariantCulture), r.HoldReason);
        SaveText(outputFile, csv.ToString());
    }

    public static void SavePeriodCsv(ProductionPeriodSummary summary, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var csv = new StringBuilder();
        Row(csv, "Label", "FromUtc", "UntilUtc", "RegisteredTotal",
            "GoodUnits", "NgUnits", "HoldUnits", "InProgressUnits", "CompletedUnits",
            "GoodRateCompletedDenominator", "GoodShareRegisteredDenominator");
        Row(csv, summary.Label, summary.FromUtc.ToString("O", CultureInfo.InvariantCulture),
            summary.UntilUtc.ToString("O", CultureInfo.InvariantCulture),
            summary.Total.ToString(CultureInfo.InvariantCulture),
            summary.Good.ToString(CultureInfo.InvariantCulture),
            summary.Ng.ToString(CultureInfo.InvariantCulture),
            summary.Hold.ToString(CultureInfo.InvariantCulture),
            summary.InProgress.ToString(CultureInfo.InvariantCulture),
            summary.Completed.ToString(CultureInfo.InvariantCulture),
            summary.GoodRate?.ToString("G17", CultureInfo.InvariantCulture),
            summary.GoodShareOfRegistered?.ToString("G17", CultureInfo.InvariantCulture));
        SaveText(outputFile, csv.ToString());
    }

    public static void SaveEventCsv(IEnumerable<ProductionEventRow> events, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(events);
        var csv = new StringBuilder();
        Row(csv, "NodeId", "EventId", "Utc", "Kind", "WorkpieceId", "MessageId", "Details");
        foreach (var e in events)
            Row(csv, e.NodeId, e.EventId.ToString("D"),
                e.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                e.Kind, e.WorkpieceId?.ToString("D"), e.MessageId?.ToString("D"), e.Details);
        SaveText(outputFile, csv.ToString());
    }

    public static void SaveJson<T>(T value, string outputFile)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(DiagnosticsFormat.Json)
        {
            WriteIndented = true
        });
        SaveText(outputFile, json);
    }

    private static void Row(StringBuilder csv, params string?[] columns)
    {
        for (int index = 0; index < columns.Length; index++)
        {
            if (index > 0) csv.Append(',');
            string value = columns[index] ?? "";
            // Prevent CSV formulas, including leading-whitespace obfuscation.
            if (value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@')
                value = "'" + value;
            csv.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        csv.Append("\r\n");
    }

    private static void SaveText(string filename, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        string path = Path.GetFullPath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
