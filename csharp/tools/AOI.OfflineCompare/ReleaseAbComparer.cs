using AOI.Contracts;
using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using AOI.Solutions;
using OpenCvSharp;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AOI.OfflineCompare;

/// <summary>
/// Read-only A/B re-inspection of ONE immutable image set against TWO persisted
/// A6 ReleaseIds. It opens the given solutions.sqlite read-only, replays the same
/// decoded pixels through both published plans, and writes evidence only beneath
/// the caller's isolated output root. No PLC, camera, Core WAL or production DB
/// is opened for writing and no unsupported algorithm silently becomes OK.
/// </summary>
public sealed class ReleaseAbComparer
{
    public const string ToolName = "AOI.OfflineCompare";
    public const string ModeName = "OFFLINE_ONLY_NOT_PRODUCTION";

    private const long MaximumPngBytes = 256L * 1024 * 1024;
    private const long MaximumPixels = 200_000_000;

    private readonly OpenCvInspectionProcessor _contour = new();

    public AbCompareManifest Compare(AbCompareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ReleaseIdA) || string.IsNullOrWhiteSpace(request.ReleaseIdB))
            throw new ArgumentException("Both a release A and a release B are required.", nameof(request));
        if (string.Equals(request.ReleaseIdA, request.ReleaseIdB, StringComparison.Ordinal))
            throw new ArgumentException("A/B must name two different published releases.", nameof(request));
        if (request.Epoch < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "Epoch must be at least 1.");
        if (!Enum.IsDefined(request.LineMapping))
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown line-scan row mapping.");

        var databasePath = Path.GetFullPath(request.DatabasePath);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("A6 database not found.", databasePath);
        var imageRoot = Path.GetFullPath(request.ImageRoot);
        if (!Directory.Exists(imageRoot))
            throw new DirectoryNotFoundException("Fixture image folder not found: " + imageRoot);
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        RequireIsolatedOutput(outputRoot, imageRoot, databasePath);
        if (Directory.Exists(outputRoot))
        {
            if (Directory.EnumerateFileSystemEntries(outputRoot).Any())
                throw new InvalidOperationException("OUTPUT_ROOT_NOT_EMPTY: " + outputRoot);
        }
        else
        {
            Directory.CreateDirectory(outputRoot);
        }

        // Never open the caller's database itself: a read-only open of a WAL store
        // still makes SQLite create -shm/-wal sidecars next to it. A byte-verified
        // snapshot inside the report folder keeps the source directory truly untouched.
        var databaseShaBefore = HashFile(databasePath);
        var snapshotPath = SnapshotDatabase(databasePath, outputRoot, databaseShaBefore);
        var releaseA = LoadRelease(snapshotPath, request.ReleaseIdA);
        var releaseB = LoadRelease(snapshotPath, request.ReleaseIdB);

        var snapshotA = new PublishedParameterSnapshot(releaseA, request.Epoch);
        var snapshotB = new PublishedParameterSnapshot(releaseB, request.Epoch);
        var boundA = snapshotA.Bind(WorkpieceIdentity.New("OFFLINE_AB_INPUT", request.Epoch, releaseA.ReleaseId));
        var boundB = snapshotB.Bind(WorkpieceIdentity.New("OFFLINE_AB_INPUT", request.Epoch, releaseB.ReleaseId));

        var devices = new List<AbPhysicalDevice>();
        var channels = new List<AbChannelResult>();
        var inputHashes = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var group in ChannelCatalog.All
            .GroupBy(c => c.PhysicalDeviceId, StringComparer.Ordinal)
            .OrderBy(g => g.Min(c => c.ChannelId), StringComparer.Ordinal))
        {
            var definitions = group.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
            var isLine = definitions.Length == 2;
            var inputFile = (isLine ? definitions[0].PhysicalDeviceId : definitions[0].ChannelId) + ".png";
            var inputPath = Path.Combine(imageRoot, inputFile);
            string? inputSha = File.Exists(inputPath) ? HashFile(inputPath) : null;
            inputHashes[inputFile] = inputSha;

            var sides = new Dictionary<string, (AbChannelSide A, AbChannelSide B)>(StringComparer.Ordinal);
            var decoded = false;
            var width = 0;
            var height = 0;
            var decodeStatus = AbChannelStatus.Error;
            string? decodeReason = null;
            Mat? parent = null;
            Mat[]? children = null;
            (int Width, int Height)[]? pixels = null;
            try
            {
                if (!File.Exists(inputPath))
                {
                    decodeStatus = AbChannelStatus.Missing;
                    decodeReason = "MISSING_IMAGE";
                }
                else
                {
                    try
                    {
                        parent = DecodePng(inputPath, isLine);
                        width = parent.Cols;
                        height = parent.Rows;
                        children = isLine
                            ? [
                                VisionPixelOps.ExtractInterleavedRows(parent, 0, request.LineMapping),
                                VisionPixelOps.ExtractInterleavedRows(parent, 1, request.LineMapping)
                            ]
                            : [parent];
                        pixels = children.Select(c => (c.Cols, c.Rows)).ToArray();
                        decoded = true;
                        decodeStatus = AbChannelStatus.Ok;
                    }
                    catch (Exception ex) when (IsPolicyFailure(ex))
                    {
                        decodeStatus = AbChannelStatus.Error;
                        decodeReason = "INVALID_IMAGE: " + ex.Message;
                    }
                }

                for (var i = 0; i < definitions.Length; i++)
                {
                    var definition = definitions[i];
                    if (!decoded)
                    {
                        var only = AbChannelSide.Unavailable(decodeStatus, decodeReason ?? "IMAGE_UNAVAILABLE");
                        sides[definition.ChannelId] = (only, only);
                        continue;
                    }

                    var sideA = InspectForRelease(boundA, definition, children![i], outputRoot, "A");
                    var sideB = InspectForRelease(boundB, definition, children![i], outputRoot, "B");
                    sides[definition.ChannelId] = (sideA, sideB);
                }
            }
            finally
            {
                if (isLine && children is not null)
                    foreach (var child in children) child.Dispose();
                parent?.Dispose();
            }

            var rows = new List<AbChannelResult>(definitions.Length);
            foreach (var definition in definitions)
            {
                var (sideA, sideB) = sides[definition.ChannelId];
                var difference = Describe(sideA, sideB);
                var index = Array.FindIndex(definitions, d => d.ChannelId == definition.ChannelId);
                var row = new AbChannelResult(definition.ChannelId, definition.StationId,
                    definition.PhysicalDeviceId, definition.OwnerRole.ToString(),
                    definition.IsPositioning, definition.SharedFrameGroup, inputFile, inputSha,
                    isLine ? inputSha : null, width, height,
                    pixels is null ? 0 : pixels[index].Width,
                    pixels is null ? 0 : pixels[index].Height,
                    sideA, sideB, difference != "IDENTICAL", difference);
                rows.Add(row);
                channels.Add(row);
            }

            devices.Add(new AbPhysicalDevice(definitions[0].PhysicalDeviceId,
                isLine ? "LINE_SCAN_SHARED_PARENT" : "AREA_CAMERA",
                definitions.Select(d => d.ChannelId).ToArray(), inputFile, inputSha,
                width, height, decodeStatus, decodeReason, rows));
        }

        var databaseShaAfter = HashFile(databasePath);
        var inputsUnchanged = inputHashes.All(x =>
            string.Equals(x.Value, File.Exists(Path.Combine(imageRoot, x.Key))
                ? HashFile(Path.Combine(imageRoot, x.Key)) : null, StringComparison.Ordinal));

        // Reports are committed as public evidence: keep machine names and absolute
        // developer paths out by default. Operators can opt in for local-only runs.
        string shownDatabase = request.IncludeAbsolutePaths
            ? databasePath : Path.GetFileName(databasePath);
        string shownImages = request.IncludeAbsolutePaths
            ? imageRoot : Path.GetFileName(imageRoot);
        string shownOutput = request.IncludeAbsolutePaths ? outputRoot : ".";

        var manifest = new AbCompareManifest(
            ToolName,
            ModeName,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Cv2.GetVersionString() ?? "unknown",
            Environment.Version.ToString(),
            shownDatabase,
            databaseShaBefore,
            databaseShaAfter,
            HashFile(snapshotPath),
            string.Equals(databaseShaBefore, databaseShaAfter, StringComparison.Ordinal),
            shownImages,
            shownOutput,
            request.LineMapping.ToString(),
            request.Epoch,
            DescribeRelease(releaseA),
            DescribeRelease(releaseB),
            devices,
            channels.Where(c => c.Changed).Select(c => c.ChannelId).ToArray(),
            channels.Where(c => !c.Changed).Select(c => c.ChannelId).ToArray(),
            channels,
            inputsUnchanged,
            request.IncludeAbsolutePaths,
            NotVerified(request.LineMapping));

        WriteEvidence(manifest, outputRoot, inputHashes);

        if (!manifest.DatabaseUnchanged || !manifest.InputImagesUnchanged)
            throw new InvalidOperationException(
                "INPUT_MUTATED_DURING_COMPARISON: the database or an input PNG changed while comparing.");
        return manifest;
    }

    private AbChannelSide InspectForRelease(WorkpieceParameterBinding bound,
        ChannelDefinition definition, Mat pixels, string outputRoot, string label)
    {
        ResolvedChannelInspectionSettings settings;
        try
        {
            var plan = PublishedRuntimeParameterResolver.Resolve(bound, definition.PhysicalDeviceId);
            settings = plan.Inspections.Single(x => x.ChannelId == definition.ChannelId);
        }
        catch (Exception ex) when (IsPolicyFailure(ex))
        {
            var status = ex is NotSupportedException
                ? AbChannelStatus.Unsupported : AbChannelStatus.Error;
            return AbChannelSide.Unavailable(status, "PUBLISHED_PLAN_REJECTED: " + ex.Message);
        }

        AbChannelSide side;
        byte[]? png;
        try
        {
            (side, png) = Evaluate(definition, settings, pixels);
        }
        catch (Exception ex) when (IsPolicyFailure(ex))
        {
            // Native decode/encode or ROI failures must degrade to a reported ERROR,
            // never abort the whole comparison and never become a silent OK.
            return AbChannelSide.Unavailable(AbChannelStatus.Error,
                "INSPECTION_IMAGE_ERROR: " + ex.GetType().Name + ": " + ex.Message);
        }
        if (png is null) return side;
        var relative = label + "/" + definition.PhysicalDeviceId + "/" + definition.ChannelId + ".png";
        var absolute = Path.Combine(outputRoot, label, definition.PhysicalDeviceId,
            definition.ChannelId + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, png);
        return side with { AnnotationRelativePath = relative };
    }

    private (AbChannelSide Side, byte[]? Png) Evaluate(ChannelDefinition channel,
        ResolvedChannelInspectionSettings settings, Mat pixels)
    {
        PixelInspectionRoi roi;
        try
        {
            roi = settings.PixelRoi(pixels.Cols, pixels.Rows);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or OverflowException)
        {
            return (new AbChannelSide(AbChannelStatus.Error, "PUBLISHED_ROI_REJECTED: " + ex.Message,
                settings.AlgorithmId, settings.BinaryThreshold, settings.MinimumContourAreaPixels,
                settings.FindBrightDefects, settings.MaximumDetections, settings.MinimumBrightFraction,
                0, 0, 0, 0, 0, Array.Empty<AbDefect>(), null), null);
        }

        var region = new ImageRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (channel.IsPositioning)
        {
            // CH01 establishes position; it is never an OK/NG product verdict.
            var placeholder = new ContourInspection(ChannelOutcome.Uncertain,
                "POSITIONING_CHANNEL_NOT_QUALITY_VERDICT", region, Array.Empty<DefectBox>());
            using var annotated = ContourDefectDetector.Annotate(pixels, placeholder);
            return (Side(AbChannelStatus.Positioning, placeholder.Reason, settings, roi, 0,
                Array.Empty<AbDefect>()), Encode(annotated));
        }

        if (settings.Kind == PublishedInspectionKind.OpenCvContour)
        {
            if (settings.MinimumContourAreaPixels is not { } minimumArea ||
                settings.FindBrightDefects is not { } findBright ||
                settings.MaximumDetections is not { } maxDetections)
                return (Side(AbChannelStatus.Error, "PUBLISHED_CONTOUR_PARAMETERS_MISSING", settings,
                    roi, 0, Array.Empty<AbDefect>()), null);
            var inspection = _contour.Inspect(channel, pixels, InspectionSettings.OpenCvContour(
                new ContourDefectRule(settings.BinaryThreshold, minimumArea, findBright, maxDetections),
                region));
            var defects = inspection.Defects
                .Select(d => new AbDefect(d.X, d.Y, d.Width, d.Height, d.AreaPixels)).ToArray();
            return (Side(Map(inspection.Outcome), inspection.Reason, settings, roi,
                inspection.ElapsedMilliseconds, defects), inspection.AnnotationPng);
        }

        if (settings.Kind == PublishedInspectionKind.LegacyBrightness)
        {
            if (settings.MinimumBrightFraction is not { } minimum ||
                !double.IsFinite(minimum) || minimum is < 0 or > 1)
                return (Side(AbChannelStatus.Error, "PUBLISHED_LEGACY_BRIGHTNESS_PARAMETERS_MISSING",
                    settings, roi, 0, Array.Empty<AbDefect>()), null);
            var began = Stopwatch.GetTimestamp();
            // Same measured-fraction semantics as the A0 published brightness rule;
            // it detects NO contours, so no defect box is invented here.
            using var gray = ToGray(pixels);
            using var crop = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
            using var binary = new Mat();
            Cv2.Threshold(crop, binary, settings.BinaryThreshold, 255, ThresholdTypes.Binary);
            double fraction = Cv2.CountNonZero(binary) / ((double)roi.Width * roi.Height);
            bool good = fraction >= minimum;
            var outcome = good ? ChannelOutcome.Ok : ChannelOutcome.Ng;
            var reason = "SIM_LEGACY_BRIGHTNESS_" + (good ? "OK_" : "NG_") +
                fraction.ToString("0.000000", CultureInfo.InvariantCulture);
            var placeholder = new ContourInspection(outcome, reason, region, Array.Empty<DefectBox>());
            using var annotated = ContourDefectDetector.Annotate(pixels, placeholder);
            return (Side(Map(outcome), reason, settings, roi,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds, Array.Empty<AbDefect>()),
                Encode(annotated));
        }

        return (Side(AbChannelStatus.Unsupported,
            "PUBLISHED_INSPECTION_PLUGIN_NOT_INSTALLED: " + settings.AlgorithmId, settings, roi, 0,
            Array.Empty<AbDefect>()), null);
    }

    private static AbChannelSide Side(AbChannelStatus status, string reason,
        ResolvedChannelInspectionSettings settings, PixelInspectionRoi roi, double elapsedMs,
        IReadOnlyList<AbDefect> defects) =>
        new(status, reason, settings.AlgorithmId, settings.BinaryThreshold,
            settings.MinimumContourAreaPixels, settings.FindBrightDefects, settings.MaximumDetections,
            settings.MinimumBrightFraction, roi.X, roi.Y, roi.Width, roi.Height, elapsedMs, defects, null);

    private static AbChannelStatus Map(ChannelOutcome outcome) => outcome switch
    {
        ChannelOutcome.Ok => AbChannelStatus.Ok,
        ChannelOutcome.Ng => AbChannelStatus.Ng,
        ChannelOutcome.Missing => AbChannelStatus.Missing,
        ChannelOutcome.Error => AbChannelStatus.Error,
        _ => AbChannelStatus.Unsupported
    };

    private static Mat ToGray(Mat pixels)
    {
        VisionPixelOps.RequireSupported(pixels, lineScan: false);
        if (pixels.Type() == MatType.CV_8UC1) return pixels.Clone();
        var gray = new Mat();
        try
        {
            Cv2.CvtColor(pixels, gray, pixels.Type() == MatType.CV_8UC3
                ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY);
            return gray;
        }
        catch
        {
            gray.Dispose();
            throw;
        }
    }

    private static byte[] Encode(Mat image)
    {
        if (!Cv2.ImEncode(".png", image, out var buffer) || buffer.Length == 0)
            throw new IOException("OpenCV could not encode the annotated PNG.");
        return buffer;
    }

    private static Mat DecodePng(string path, bool lineScan)
    {
        using (var file = File.OpenRead(path))
        {
            if (file.Length is < 16 or > MaximumPngBytes)
                throw new InvalidDataException("PNG is empty or beyond the offline input limit.");
            Span<byte> signature = stackalloc byte[8];
            file.ReadExactly(signature);
            ReadOnlySpan<byte> pngMagic = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!signature.SequenceEqual(pngMagic))
                throw new InvalidDataException("Input is not PNG image data.");
        }
        var pixels = Cv2.ImRead(path, ImreadModes.Unchanged);
        try
        {
            VisionPixelOps.RequireSupported(pixels, lineScan);
            if ((long)pixels.Rows * pixels.Cols > MaximumPixels)
                throw new InvalidDataException("Decoded PNG exceeds the offline pixel budget.");
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static string SnapshotDatabase(string databasePath, string outputRoot, string expectedSha)
    {
        // A live WAL (or its -shm) is NOT a certified offline snapshot: copying the
        // .db file alone would silently drop committed-but-not-checkpointed rows, and
        // copying .db/-wal/-shm as three files is not one consistent transaction. Fail
        // closed and require a checkpointed export instead. The source is never touched.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
                throw new InvalidOperationException(
                    "SOURCE_WAL_PRESENT: " + suffix + " exists next to the A6 database, so it is not a " +
                    "stable offline snapshot. Checkpoint/export a copy (clean shutdown or SQLite backup) " +
                    "and compare that file. This tool never deletes or rewrites WAL/SHM.");
        }
        var folder = Path.Combine(outputRoot, "_db-snapshot");
        Directory.CreateDirectory(folder);
        var snapshot = Path.Combine(folder, Path.GetFileName(databasePath));
        File.Copy(databasePath, snapshot, overwrite: false);
        var actual = HashFile(snapshot);
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DATABASE_SNAPSHOT_MISMATCH: the copied database is not byte-identical, so the source " +
                "was written while copying. Re-run against a stable offline snapshot.");
        return snapshot;
    }

    private static PublishedRelease LoadRelease(string databasePath, string releaseId)
    {
        try
        {
            return new SolutionRepository(databasePath, readOnly: true).RequireRelease(releaseId);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException("UNKNOWN_RELEASE_ID: " + releaseId, ex);
        }
    }

    private static AbReleaseInfo DescribeRelease(PublishedRelease release)
    {
        var recomputed = SolutionSafety.Sha256(WireFormat.Serialize(release.Manifest.Canonical()));
        var stations = release.Manifest.Stations;
        var channels = stations.SelectMany(s => s.ChannelIds).Distinct(StringComparer.Ordinal).Count();
        return new AbReleaseInfo(release.ReleaseId, release.ContentSha256, recomputed,
            string.Equals(recomputed, release.ContentSha256, StringComparison.Ordinal),
            release.Manifest.SolutionId, release.Manifest.ProfileId, release.Manifest.ProfileVersion,
            release.PublishedAtUtc, release.Actor, stations.Count, channels);
    }

    private static string Describe(AbChannelSide a, AbChannelSide b)
    {
        var parts = new List<string>();
        if (a.Status != b.Status) parts.Add("status " + a.Status + "->" + b.Status);
        if (a.Defects.Count != b.Defects.Count) parts.Add("defects " + a.Defects.Count + "->" + b.Defects.Count);
        if (a.RoiX != b.RoiX || a.RoiY != b.RoiY || a.RoiWidth != b.RoiWidth || a.RoiHeight != b.RoiHeight)
            parts.Add("roi " + a.RoiWidth + "x" + a.RoiHeight + "@" + a.RoiX + "," + a.RoiY +
                "->" + b.RoiWidth + "x" + b.RoiHeight + "@" + b.RoiX + "," + b.RoiY);
        if (a.BinaryThreshold != b.BinaryThreshold)
            parts.Add("threshold " + a.BinaryThreshold + "->" + b.BinaryThreshold);
        if (a.AlgorithmId != b.AlgorithmId)
            parts.Add("algorithm " + a.AlgorithmId + "->" + b.AlgorithmId);
        return parts.Count == 0 ? "IDENTICAL" : string.Join("; ", parts);
    }

    private static void RequireIsolatedOutput(string outputRoot, string imageRoot, string databasePath)
    {
        // Resolve real targets (symlink hops and `..` aliases) before comparing, so a
        // benign-looking report path cannot land in the image fixture or the A6 run folder.
        var realOutput = ResolveRealPath(outputRoot);
        var realImages = ResolveRealPath(imageRoot);
        var realDatabase = ResolveRealPath(databasePath);
        if (IsSameOrInside(realOutput, realImages) || IsSameOrInside(realImages, realOutput))
            throw new InvalidOperationException(
                "OUTPUT_ROOT_INSIDE_IMAGE_TREE: reports must never be written into the image fixture.");
        if (IsSameOrInside(realDatabase, realOutput))
            throw new InvalidOperationException(
                "DATABASE_INSIDE_OUTPUT_ROOT: never host the A6 database in a report folder.");
        var runRoot = Path.GetDirectoryName(realDatabase);
        if (!string.IsNullOrEmpty(runRoot) &&
            !string.Equals(runRoot, Path.GetPathRoot(realDatabase), StringComparison.OrdinalIgnoreCase) &&
            IsSameOrInside(realOutput, runRoot))
            throw new InvalidOperationException(
                "OUTPUT_ROOT_INSIDE_DATABASE_RUN_ROOT: reports must not be written into the A6 run folder (" +
                runRoot + "); export the report to a separate directory.");
    }

    /// <summary>Resolves an existing path through symlinks; a not-yet-created tail is
    /// appended to the resolved real ancestor so alias escapes are still detected.</summary>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var tail = new List<string>();
        var current = full;
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) return full;
            tail.Insert(0, Path.GetFileName(current));
            current = parent;
        }
        string resolved;
        if (Directory.Exists(current))
            resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? current;
        else
            resolved = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        foreach (var part in tail) resolved = Path.Combine(resolved, part);
        return Path.GetFullPath(resolved);
    }

    private static bool IsSameOrInside(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsPolicyFailure(Exception ex) =>
        ex is NotSupportedException or InvalidDataException or InvalidOperationException
            or ArgumentException or IOException or OverflowException or OpenCVException;

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NotVerified(LineScanRowMapping mapping) =>
    [
        "SIM_ONLY contour/brightness rules are not a field-certified defect classifier; accuracy is NOT_VERIFIED.",
        "Line-scan row parity uses " + mapping + ": physical light-path/orientation remains FIELD_PENDING (FG-07/FG-09).",
        "Only the current host OS/OpenCV build is evidenced; other platforms are NOT_VERIFIED.",
        "No PLC, camera SDK, CamDone, production statistics or field acceptance is produced by this tool."
    ];

    private static void WriteEvidence(AbCompareManifest manifest, string outputRoot,
        IReadOnlyDictionary<string, string?> inputHashes)
    {
        File.WriteAllText(Path.Combine(outputRoot, "ab-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, AbCompareRequest.Json),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var inputs = new StringBuilder();
        inputs.AppendLine("# OFFLINE_ONLY / NOT_PRODUCTION input evidence");
        inputs.AppendLine("database " + manifest.DatabaseSha256Before + "  " + manifest.DatabasePath);
        foreach (var (file, sha) in inputHashes.OrderBy(x => x.Key, StringComparer.Ordinal))
            inputs.AppendLine(file + " " + (sha ?? "MISSING"));
        File.WriteAllText(Path.Combine(outputRoot, "inputs.sha256.txt"), inputs.ToString());

        var csv = new StringBuilder();
        csv.AppendLine("channelId,stationId,physicalDeviceId,ownerRole,isPositioning,inputFile," +
            "inputSha256,aStatus,aReason,aDefects,bStatus,bReason,bDefects,changed,difference");
        foreach (var c in manifest.Channels)
            csv.AppendLine(string.Join(",",
                Quote(c.ChannelId), Quote(c.StationId), Quote(c.PhysicalDeviceId), Quote(c.OwnerRole),
                c.IsPositioning ? "true" : "false", Quote(c.InputFile), Quote(c.InputSha256),
                Quote(c.A.Status.ToString()), Quote(c.A.Reason), c.A.Defects.Count.ToString(CultureInfo.InvariantCulture),
                Quote(c.B.Status.ToString()), Quote(c.B.Reason), c.B.Defects.Count.ToString(CultureInfo.InvariantCulture),
                c.Changed ? "true" : "false", Quote(c.Difference)));
        File.WriteAllText(Path.Combine(outputRoot, "channel-results.csv"), csv.ToString());

        var text = new StringBuilder();
        text.AppendLine("AOI.OfflineCompare - OFFLINE_ONLY / NOT_PRODUCTION");
        text.AppendLine("A " + manifest.ReleaseA.ReleaseId + "  sha256=" + manifest.ReleaseA.RecomputedContentSha256);
        text.AppendLine("B " + manifest.ReleaseB.ReleaseId + "  sha256=" + manifest.ReleaseB.RecomputedContentSha256);
        text.AppendLine("images " + manifest.ImageRoot + "  (unchanged=" + manifest.InputImagesUnchanged + ")");
        text.AppendLine("database sha256 unchanged=" + manifest.DatabaseUnchanged);
        text.AppendLine();
        text.AppendLine("channel  device       A           B           changed  detail");
        foreach (var c in manifest.Channels)
            text.AppendLine(
                c.ChannelId.PadRight(8) + " " + c.PhysicalDeviceId.PadRight(12) + " " +
                c.A.Status.ToString().PadRight(11) + " " + c.B.Status.ToString().PadRight(11) + " " +
                (c.Changed ? "YES" : "no ").PadRight(8) + " " + c.Difference);
        text.AppendLine();
        text.AppendLine("changed: " + (manifest.ChangedChannels.Count == 0
            ? "(none)" : string.Join(",", manifest.ChangedChannels)));
        File.WriteAllText(Path.Combine(outputRoot, "summary.txt"), text.ToString());
    }

    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal)
            ? '"' + text.Replace("\"", "\"\"", StringComparison.Ordinal) + '"' : text;
    }
}
