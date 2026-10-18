using AOI.Contracts;
using System.Globalization;
using System.Text;

namespace AOI.Ui.Client;

/// <summary>
/// Operator production report CSV. Header text, column order, statistics semantics and the
/// spreadsheet formula-injection guard are byte-for-byte the same as the original operator
/// export; only the destination selection moved out to <see cref="IReportSavePicker"/>.
/// </summary>
public static class ProductionReportCsv
{
    public static string SuggestedFileName(DateTimeOffset now) =>
        "AOI-SIM-Production-" +
        now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".csv";

    public static string Build(SimProductionReports data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var csv = new StringBuilder();
        csv.AppendLine("Period,FromUTC,UntilUTC,Total,GOOD,NG,HOLD,InProgress,GoodRate");
        foreach (var period in new[] { data.Today, data.ThisWeek })
        {
            string[] fields = [
                period.Label, period.FromUtc.ToString("O"), period.UntilUtc.ToString("O"),
                period.Total.ToString(CultureInfo.InvariantCulture),
                period.Good.ToString(CultureInfo.InvariantCulture),
                period.Ng.ToString(CultureInfo.InvariantCulture),
                period.Hold.ToString(CultureInfo.InvariantCulture),
                period.InProgress.ToString(CultureInfo.InvariantCulture),
                period.GoodRate?.ToString("G17", CultureInfo.InvariantCulture) ?? "" ];
            csv.AppendLine(string.Join(",", fields.Select(Field)));
        }
        csv.AppendLine();
        csv.AppendLine("WorkpieceId,Barcode,ReleaseId,RegisteredUTC,Disposition,Channels,HoldReason");
        foreach (var row in data.Recent)
        {
            string[] fields = [row.WorkpieceId.ToString("D"), row.Barcode, row.ReleaseId,
                row.RegisteredUtc.ToUniversalTime().ToString("O"),
                row.Disposition, row.CapturedChannels.ToString(CultureInfo.InvariantCulture),
                row.HoldReason ?? ""];
            csv.AppendLine(string.Join(",", fields.Select(Field)));
        }
        return csv.ToString();
    }

    internal static string Field(string? value)
    {
        string safe = value ?? "";
        string trimmed = safe.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
            safe = "'" + safe; // prevent imported barcode as spreadsheet formula
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
