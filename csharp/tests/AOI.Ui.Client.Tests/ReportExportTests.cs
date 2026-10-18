using AOI.Contracts;
using AOI.Ui.Client;
using NUnit.Framework;

namespace AOI.Ui.Client.Tests;

[TestFixture]
public sealed class ReportExportTests
{
    private static SimProductionReports Example()
    {
        var start = new DateTimeOffset(2026, 10, 18, 1, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var day = new SimReportPeriod("day", start, end, 3, 1, 1, 1, 0, 0.5);
        var week = day with { Label = "week" };
        return new SimProductionReports(true, end, day, week, 3, [
            new SimReportWorkpiece(Guid.NewGuid(), "=cmd|calc", "SIMREL-1", start, "Good", 11, null),
            new SimReportWorkpiece(Guid.NewGuid(), "A\"B", "SIMREL-1", start, "Hold", 6, "BARCODE_MISMATCH")
        ]);
    }

    private sealed class FakePicker(string? path, Exception? error = null) : IReportSavePicker
    {
        public string? SeenSuggestion { get; private set; }
        public Task<string?> PickAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            SeenSuggestion = suggestedFileName;
            if (error is not null) throw error;
            return Task.FromResult(path);
        }
    }

    [Test]
    public void CsvKeepsHeaderColumnOrderAndFormulaInjectionGuard()
    {
        var csv = ProductionReportCsv.Build(Example());
        // StringBuilder.AppendLine uses Environment.NewLine: CRLF on Windows, LF on Linux CI.
        Assert.That(csv, Does.Contain(Environment.NewLine));
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.None);
        Assert.That(lines[0], Is.EqualTo("Period,FromUTC,UntilUTC,Total,GOOD,NG,HOLD,InProgress,GoodRate"));
        Assert.That(lines[1], Does.StartWith("\"day\","));
        Assert.That(lines[2], Does.StartWith("\"week\","));
        Assert.That(lines[3], Is.Empty);
        Assert.That(lines[4], Is.EqualTo("WorkpieceId,Barcode,ReleaseId,RegisteredUTC,Disposition,Channels,HoldReason"));
        Assert.That(lines[5], Does.Contain("'=cmd|calc"));
        Assert.That(lines[6], Does.Contain("\"A\"\"B\""));
        Assert.That(lines[6], Does.Contain("\"BARCODE_MISMATCH\""));
    }

    [Test]
    public async Task CancelledDialogWritesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aoi-export-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var picker = new FakePicker(null);
            var result = await ReportExportService.ExportCsvAsync(picker, Example(), DateTimeOffset.UtcNow);
            Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Cancelled));
            Assert.That(result.Path, Is.Null);
            Assert.That(Directory.GetFiles(dir), Is.Empty);
            Assert.That(picker.SeenSuggestion, Does.EndWith(".csv"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task EmptyPathIsTreatedAsCancelled()
    {
        var result = await ReportExportService.ExportCsvAsync(
            new FakePicker("   "), Example(), DateTimeOffset.UtcNow);
        Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Cancelled));
    }

    [Test]
    public async Task WriteFailureReportsTheRealReason()
    {
        string missing = Path.Combine(Path.GetTempPath(),
            "aoi-missing-" + Guid.NewGuid().ToString("N"), "report.csv");
        var result = await ReportExportService.ExportCsvAsync(
            new FakePicker(missing), Example(), DateTimeOffset.UtcNow);
        Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Failed));
        Assert.That(result.Message, Does.Contain("报表导出失败"));
        Assert.That(File.Exists(missing), Is.False);
    }

    [Test]
    public async Task PickerFailureIsReportedNotSwallowed()
    {
        var result = await ReportExportService.ExportCsvAsync(
            new FakePicker(null, new UnauthorizedAccessException("dialog denied")),
            Example(), DateTimeOffset.UtcNow);
        Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Failed));
        Assert.That(result.Message, Does.Contain("dialog denied"));
    }

    [Test]
    public async Task NullPickerIsExplicitlyUnavailable()
    {
        var result = await ReportExportService.ExportCsvAsync(null, Example(), DateTimeOffset.UtcNow);
        Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Unavailable));
    }

    [Test]
    public async Task SavedFileMatchesBuilderExactly()
    {
        string path = Path.Combine(Path.GetTempPath(), "aoi-export-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var data = Example();
            var result = await ReportExportService.ExportCsvAsync(
                new FakePicker(path), data, DateTimeOffset.UtcNow);
            Assert.That(result.Outcome, Is.EqualTo(ReportExportOutcome.Saved));
            string written = await File.ReadAllTextAsync(path);
            Assert.That(written, Is.EqualTo(ProductionReportCsv.Build(data)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
