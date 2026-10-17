using AOI.Diagnostics;
using System.Globalization;

static int Usage()
{
    Console.Error.WriteLine("""
        SIM_ONLY / READ_ONLY
        AOI.Diagnostics.Cli timeline ROOT --workpiece UUID [--capture UUID] [--device ID] [--channel CH06] [--attempt 1] [--limit 2000] [--csv FILE] [--json FILE] [--expect EXPECTATIONS.json] [--comparison-json FILE]
        AOI.Diagnostics.Cli history ROOT [--workpiece UUID] [--barcode TEXT] [--station ST03] [--from UTC] [--until UTC] [--limit 100] [--offset 0] [--csv FILE] [--json FILE]
        AOI.Diagnostics.Cli report ROOT day|week YYYY-MM-DD [--timezone Asia/Singapore] [--csv FILE] [--json FILE]
        AOI.Diagnostics.Cli report ROOT range --from UTC --until UTC [--station ST03] [--csv FILE] [--json FILE]
        AOI.Diagnostics.Cli events ROOT [--workpiece UUID] [--node master|slave] [--kind EVENT] [--text TEXT] [--station ST03] [--from UTC] [--until UTC] [--limit 100] [--csv FILE] [--json FILE]
        AOI.Diagnostics.Cli trace ROOT WORKPIECE_UUID [--json FILE] [--events-csv FILE]
        AOI.Diagnostics.Cli capture ROOT JOURNAL
        AOI.Diagnostics.Cli watch ROOT JOURNAL [seconds]
        AOI.Diagnostics.Cli replay JOURNAL
        AOI.Diagnostics.Cli bundle JOURNAL OUTPUT.zip
        AOI.Diagnostics.Cli verify OUTPUT.zip
        UTC range is half-open [from,until). Report denominator = distinct Master WorkpieceId.
        """);
    return 2;
}
static Dictionary<string, string> Options(string[] arguments, int start)
{
    if ((arguments.Length - start) % 2 != 0)
        throw new ArgumentException("Each --option requires one value.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = start; i < arguments.Length; i += 2)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal) ||
            !options.TryAdd(arguments[i], arguments[i + 1]))
            throw new ArgumentException("Invalid or duplicate option.");
    }
    return options;
}
static string? Get(Dictionary<string, string> options, string key) =>
    options.GetValueOrDefault(key);
static DateTimeOffset? Time(Dictionary<string, string> o, string key) =>
    Get(o, key) is string s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind).ToUniversalTime() : null;
static Guid? Id(Dictionary<string, string> o, string key) =>
    Get(o, key) is string s ? Guid.Parse(s) : null;
static int Number(Dictionary<string, string> o, string key, int fallback) =>
    Get(o, key) is string s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;
static void WriteJsonOrCsv<T>(T value, Dictionary<string, string> o,
    Action<T, string> saveCsv)
{
    if (Get(o, "--json") is string json) ProductionReportExports.SaveJson(value, json);
    if (Get(o, "--csv") is string csv) saveCsv(value, csv);
}
try
{
    if (args.Length < 2) return Usage();
    const int staleSeconds = 30;
    switch (args[0])
    {
        case "timeline" when args.Length >= 4:
        {
            var options = Options(args, 2);
            var id = Id(options, "--workpiece")
                ?? throw new ArgumentException("timeline requires --workpiece UUID");
            var query = new TimingFilter(id,
                CaptureId: Id(options, "--capture"),
                PhysicalDeviceId: Get(options, "--device"),
                ChannelId: Get(options, "--channel"),
                AttemptNo: Get(options, "--attempt") is null ? null : Number(options, "--attempt", 1),
                Limit: Number(options, "--limit", 2000));
            var timeline = new ProductionTimelineReader(args[1]).Query(query);
            Console.WriteLine(DiagnosticsFormat.Serialize(timeline));
            if (Get(options, "--csv") is string csv)
                ProductionTimelineExports.SaveCsv(timeline, csv);
            if (Get(options, "--json") is string json)
                ProductionTimelineExports.SaveJson(timeline, json);
            if (Get(options, "--expect") is string expectedFile)
            {
                var expected = DiagnosticsFormat.Parse<TimingExpectation[]>(File.ReadAllText(expectedFile));
                var comparison = ProductionTimingAnalyzer.Compare(id, expected, timeline.Events);
                if (Get(options, "--comparison-json") is string output)
                    ProductionTimelineExports.SaveComparisonJson(comparison, output);
                Console.Error.WriteLine(DiagnosticsFormat.Serialize(comparison));
                return comparison.IsMatch && !timeline.Truncated ? 0 : 3;
            }
            return timeline.DiagnosticDifferences.Any(d => d.Severity == "ERROR") ? 3 : 0;
        }
        case "history" when args.Length >= 2:
        {
            var options = Options(args, 2);
            var reports = new ProductionReports(args[1]);
            var filter = new ProductionSearch(
                WorkpieceId: Id(options, "--workpiece"),
                Barcode: Get(options, "--barcode"), StationId: Get(options, "--station"),
                FromUtc: Time(options, "--from"), UntilUtc: Time(options, "--until"),
                Limit: Number(options, "--limit", 100), Offset: Number(options, "--offset", 0));
            var page = reports.Search(filter);
            Console.WriteLine(DiagnosticsFormat.Serialize(page));
            WriteJsonOrCsv(page, options, ProductionReportExports.SaveHistoryCsv);
            return 0;
        }
        case "report" when args.Length >= 3:
        {
            var reports = new ProductionReports(args[1]);
            ProductionPeriodSummary period;
            Dictionary<string, string> options;
            if (args[2] is "day" or "week" && args.Length >= 4)
            {
                var date = DateOnly.ParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                options = Options(args, 4);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(Get(options, "--timezone") ?? "UTC");
                period = args[2] == "day" ? reports.Daily(date, zone) : reports.Weekly(date, zone);
            }
            else if (args[2] == "range")
            {
                options = Options(args, 3);
                period = reports.Summarize(new ProductionSearch(
                    StationId: Get(options, "--station"), FromUtc: Time(options, "--from"),
                    UntilUtc: Time(options, "--until")));
            }
            else return Usage();
            Console.WriteLine(DiagnosticsFormat.Serialize(period));
            WriteJsonOrCsv(period, options, ProductionReportExports.SavePeriodCsv);
            return 0;
        }
        case "events" when args.Length >= 2:
        {
            var options = Options(args, 2);
            var reports = new ProductionReports(args[1]);
            var events = reports.SearchEvents(new ProductionEventSearch(
                WorkpieceId: Id(options, "--workpiece"), NodeId: Get(options, "--node"),
                Kind: Get(options, "--kind"), StationId: Get(options, "--station"),
                ContainsText: Get(options, "--text"), FromUtc: Time(options, "--from"),
                UntilUtc: Time(options, "--until"), Limit: Number(options, "--limit", 100)));
            Console.WriteLine(DiagnosticsFormat.Serialize(events));
            WriteJsonOrCsv(events, options, ProductionReportExports.SaveEventCsv);
            return 0;
        }
        case "trace" when args.Length >= 3:
        {
            var options = Options(args, 3);
            var report = new ProductionReports(args[1]).Trace(Guid.Parse(args[2]), verifyImages: true);
            Console.WriteLine(DiagnosticsFormat.Serialize(report));
            if (Get(options, "--json") is string json)
                ProductionReportExports.SaveJson(report, json);
            if (Get(options, "--events-csv") is string csv)
                ProductionReportExports.SaveEventCsv(report.Events, csv);
            return 0;
        }
        case "capture" when args.Length == 3:
        {
            var entry = FlightRecorder.Append(args[2], ReadOnlyCapture.Capture(args[1]));
            Console.WriteLine($"SIM_ONLY_CAPTURE seq={entry.Sequence} sha256={entry.EntrySha256}");
            return 0;
        }
        case "watch" when args.Length is 3 or 4:
        {
            int seconds = args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
            if (seconds < 1 || seconds > 3600) return Usage();
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            string? lastState = null;
            while (!cancel.IsCancellationRequested)
            {
                var snapshot = ReadOnlyCapture.Capture(args[1]);
                var hash = DiagnosticsFormat.StateHash(snapshot);
                var entry = FlightRecorder.Append(args[2], snapshot);
                if (lastState != hash)
                {
                    Console.WriteLine($"SIM_ONLY_STATE_CHANGED seq={entry.Sequence} hash={hash}");
                    lastState = hash;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancel.Token); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            }
            return 0;
        }
        case "replay" when args.Length == 2:
        {
            var assessments = DiagnosticAnalyzer.Replay(args[1], TimeSpan.FromSeconds(staleSeconds));
            Console.WriteLine(DiagnosticsFormat.Serialize(assessments));
            return assessments.Any(a => a.Findings.Any(f => f.Severity is "ERROR" or "CRITICAL")) ? 3 : 0;
        }
        case "bundle" when args.Length == 3:
            DiagnosticBundle.Export(args[1], args[2], TimeSpan.FromSeconds(staleSeconds));
            Console.WriteLine("SIM_ONLY_BUNDLE_CREATED " + Path.GetFullPath(args[2]));
            return 0;
        case "verify" when args.Length == 2:
            DiagnosticBundle.Verify(args[1]);
            Console.WriteLine("SIM_ONLY_BUNDLE_VERIFIED");
            return 0;
        default: return Usage();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"DIAGNOSTICS_FAILED {ex.GetType().Name}: {ex.Message}");
    return 1;
}
