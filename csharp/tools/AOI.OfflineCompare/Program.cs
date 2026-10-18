using AOI.OfflineCompare;
using AOI.Runtime;
using System.Globalization;

// Deliberately an offline-only tool: no PLC, camera SDK, Core WAL or production write.
try
{
    return Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine("OFFLINE_COMPARE_ERROR " + error.Message);
    return 2;
}

static int Run(string[] args)
{
    if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            AOI.OfflineCompare - OFFLINE_ONLY / NOT_PRODUCTION read-only A/B re-inspection.

              --compare --db <solutions.sqlite> --images <fixture-dir> --out <new-report-dir>
                        --release-a <ReleaseIdA> --release-b <ReleaseIdB>
                        [--epoch 1] [--line-mapping demo|legacy]

              --scaffold-fixture-db <new .sqlite path> [--with-unsupported-release]
              --make-fixture-images <new image dir>
              --all-in-one <new scratch dir>

            Fixture images: CH01.png..CH07.png (7 area) and LINE-S-8.png / LINE-M-2.png
            (2 shared line-scan parents, one parent per two logical channels).

            The tool opens the A6 database READ ONLY, never writes images outside the
            report folder, and reports OK/NG/ERROR/MISSING/POSITIONING/UNSUPPORTED per
            channel. An unknown plugin or a bad ROI is never rendered as OK.
            """);
        return 0;
    }

    var mode = args[0];
    var (positional, options) = ParseOptions(args, 1);
    switch (mode)
    {
        case "--make-fixture-images":
            return MakeFixtureImages(positional ?? Need(options, "--dir"));
        case "--scaffold-fixture-db":
            return ScaffoldFixtureDatabase(positional ?? Need(options, "--db"),
                options.ContainsKey("--with-unsupported-release"));
        case "--compare":
            return Compare(options);
        case "--all-in-one":
            return AllInOne(positional ?? Need(options, "--dir"));
        default:
            throw new ArgumentException("Unknown mode " + mode + ". Use --help.");
    }
}

static int MakeFixtureImages(string directory)
{
    AbFixtureImages.Write(directory);
    var root = Path.GetFullPath(directory);
    Console.WriteLine("OFFLINE_ONLY fixture images written: " + root);
    foreach (var file in AbFixtureImages.Files)
        Console.WriteLine("  " + file);
    return 0;
}

static int ScaffoldFixtureDatabase(string databasePath, bool withUnsupported)
{
    var releases = AbFixtureSolution.ScaffoldDatabase(databasePath, withUnsupported);
    Console.WriteLine("A6 fixture database created: " + Path.GetFullPath(databasePath));
    Console.WriteLine("  release A: " + releases.ReleaseA);
    Console.WriteLine("  release B: " + releases.ReleaseB);
    if (releases.UnsupportedRelease is not null)
        Console.WriteLine("  unsupported-plugin release: " + releases.UnsupportedRelease);
    return 0;
}

static int Compare(Dictionary<string, string> options)
{
    var request = new AbCompareRequest(
        Need(options, "--db"),
        Need(options, "--images"),
        Need(options, "--out"),
        Need(options, "--release-a"),
        Need(options, "--release-b"),
        options.TryGetValue("--epoch", out var epoch)
            ? long.Parse(epoch, CultureInfo.InvariantCulture) : 1,
        options.TryGetValue("--line-mapping", out var mapping) &&
            mapping.Equals("legacy", StringComparison.OrdinalIgnoreCase)
            ? LineScanRowMapping.LegacySkipFirstRow : LineScanRowMapping.DemoEvenOdd);
    var manifest = new ReleaseAbComparer().Compare(request);
    Print(manifest);
    return 0;
}

static int AllInOne(string scratchRoot)
{
    var root = Path.GetFullPath(scratchRoot);
    var images = Path.Combine(root, "images");
    var database = Path.Combine(root, "fixture-solutions.sqlite");
    var report = Path.Combine(root, "report");
    AbFixtureImages.Write(images);
    var releases = AbFixtureSolution.ScaffoldDatabase(database);
    Console.WriteLine("OFFLINE_ONLY fixtures: " + root);
    var manifest = new ReleaseAbComparer().Compare(new AbCompareRequest(
        database, images, report, releases.ReleaseA, releases.ReleaseB));
    Print(manifest);
    return 0;
}

static void Print(AbCompareManifest manifest)
{
    Console.WriteLine("OFFLINE_ONLY / NOT_PRODUCTION  OpenCV " + manifest.OpenCvVersion);
    Console.WriteLine("A " + manifest.ReleaseA.ReleaseId + " sha256=" + manifest.ReleaseA.RecomputedContentSha256);
    Console.WriteLine("B " + manifest.ReleaseB.ReleaseId + " sha256=" + manifest.ReleaseB.RecomputedContentSha256);
    Console.WriteLine("database unchanged=" + manifest.DatabaseUnchanged +
        "  input images unchanged=" + manifest.InputImagesUnchanged);
    Console.WriteLine();
    Console.WriteLine("channel  device        A            B            changed  detail");
    foreach (var channel in manifest.Channels)
    {
        Console.WriteLine(
            channel.ChannelId.PadRight(8) + " " +
            channel.PhysicalDeviceId.PadRight(13) + " " +
            channel.A.Status.ToString().PadRight(12) + " " +
            channel.B.Status.ToString().PadRight(12) + " " +
            (channel.Changed ? "YES" : "no ").PadRight(8) + " " +
            channel.Difference);
    }
    Console.WriteLine();
    Console.WriteLine("changed channels: " + (manifest.ChangedChannels.Count == 0
        ? "(none)" : string.Join(",", manifest.ChangedChannels)));
    Console.WriteLine("report: " + manifest.OutputRoot);
}

static (string? Positional, Dictionary<string, string> Options) ParseOptions(
    string[] args, int start)
{
    string? positional = null;
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = start; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (positional is not null)
                throw new ArgumentException("Only one positional path is accepted.");
            positional = args[i];
            continue;
        }
        if (options.ContainsKey(args[i]))
            throw new ArgumentException("Duplicate option " + args[i]);
        if (args[i] == "--with-unsupported-release")
        {
            options[args[i]] = "true";
            continue;
        }
        if (++i >= args.Length)
            throw new ArgumentException("Option " + args[i - 1] + " requires a value.");
        options[args[i - 1]] = args[i];
    }
    return (positional, options);
}

static string Need(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing required option " + key);
