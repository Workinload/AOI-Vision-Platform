using AOI.Domain;
using AOI.Solutions;
using OpenCvSharp;

namespace AOI.OfflineCompare;

/// <summary>
/// Deterministic offline fixtures. The database is a brand-new A6 store published
/// through the real A6 repository API; the PNGs are real encoded images with real
/// pixel content. Neither is a field product photo or a field-verified recipe.
/// </summary>
public static class AbFixtureSolution
{
    public const string SolutionId = "SOL_AB_COMPARE";
    public const string ProfileId = "SIM_MACHINE_01";
    public const string Actor = "dshb-offline-compare";
    public const string UnsupportedAlgorithmId = "vendor.unknown.v1";

    public sealed record FixtureReleases(string ReleaseA, string ReleaseB, string? UnsupportedRelease);

    public static IReadOnlyList<string> Stations() => ChannelCatalog.All
        .Select(c => c.StationId).Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Creates TWO immutable persisted releases that differ only by ROI (CH06) and
    /// threshold (CH02). Refuses to touch an existing database file.</summary>
    public static FixtureReleases ScaffoldDatabase(string databasePath, bool includeUnsupportedRelease = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var full = Path.GetFullPath(databasePath);
        if (File.Exists(full))
            throw new InvalidOperationException("FIXTURE_DB_ALREADY_EXISTS: " + full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var repository = new SolutionRepository(full);
        repository.RegisterProfile(MachineProfile.SimBaseline(ProfileId), Actor);
        repository.CreateSolution(new ProductSolution(SolutionId, ProfileId, "DSH-B offline A/B fixture"), Actor);
        foreach (var station in Stations())
            repository.SaveDraft(SolutionId, Recipe(station, "A"), 0, Actor);
        var releaseA = repository.Publish(SolutionId, Actor);

        foreach (var station in Stations())
        {
            var draft = repository.GetDraft(SolutionId, station)
                ?? throw new InvalidOperationException("Fixture draft missing for " + station);
            repository.SaveDraft(SolutionId, Recipe(station, "B"), draft.Revision, Actor);
        }
        var releaseB = repository.Publish(SolutionId, Actor);

        string? unsupported = null;
        if (includeUnsupportedRelease)
        {
            var draft = repository.GetDraft(SolutionId, "ST06")
                ?? throw new InvalidOperationException("Fixture draft missing for ST06");
            repository.SaveDraft(SolutionId, Recipe("ST06", "UNSUPPORTED"), draft.Revision, Actor);
            unsupported = repository.Publish(SolutionId, Actor).ReleaseId;
        }

        if (releaseA.ReleaseId == releaseB.ReleaseId)
            throw new InvalidOperationException("Fixture variants did not produce two distinct releases.");
        return new(releaseA.ReleaseId, releaseB.ReleaseId, unsupported);
    }

    private static StationRecipe Recipe(string stationId, string variant)
    {
        var channels = ChannelCatalog.All
            .Where(c => c.StationId == stationId)
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal)
            .Select(c => c.ChannelId).ToArray();
        var tunables = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var targets = new List<InspectionAlgorithmTarget>(channels.Length);
        foreach (var channelId in channels)
        {
            var roi = Roi(channelId, variant);
            tunables["roi." + channelId + ".x"] = roi.X;
            tunables["roi." + channelId + ".y"] = roi.Y;
            tunables["roi." + channelId + ".width"] = roi.Width;
            tunables["roi." + channelId + ".height"] = roi.Height;
            tunables["roi." + channelId + ".revision"] = 1;
            var algorithm = variant == "UNSUPPORTED" && channelId == "CH07"
                ? UnsupportedAlgorithmId
                : PublishedRuntimeParameterResolver.OpenCvContourId;
            var threshold = channelId == "CH02" ? (variant == "A" ? 180d : 90d) : 127d;
            targets.Add(new InspectionAlgorithmTarget(channelId, algorithm,
                new SortedDictionary<string, double>(StringComparer.Ordinal)
                {
                    ["binaryThreshold"] = threshold,
                    ["minimumContourAreaPixels"] = 40d,
                    ["findBrightDefects"] = 1d,
                    ["maximumDetections"] = 64d
                },
                new SortedDictionary<string, string>(StringComparer.Ordinal)));
        }
        return new StationRecipe(stationId, channels, tunables) { InspectionTargets = targets };
    }

    private static NormalizedInspectionRoi Roi(string channelId, string variant) =>
        channelId == "CH06" && variant == "A"
            ? new NormalizedInspectionRoi(0, 0, 0.5, 1)
            : NormalizedInspectionRoi.FullFrame;
}

/// <summary>Nine deterministic PNGs: 7 area frames and 2 shared line-scan parents.</summary>
public static class AbFixtureImages
{
    public static IReadOnlyList<string> Files { get; } =
    [
        "CH01.png", "CH02.png", "CH03.png", "CH04.png", "CH05.png",
        "CH06.png", "CH07.png", "LINE-S-8.png", "LINE-M-2.png"
    ];

    public static void Write(string imageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRoot);
        var root = Path.GetFullPath(imageRoot);
        Directory.CreateDirectory(root);
        if (Files.Any(f => File.Exists(Path.Combine(root, f))))
            throw new InvalidOperationException("FIXTURE_IMAGES_ALREADY_EXIST: " + root);

        WriteMono(root, "CH01", 900, 600, mat =>
        {
            Cv2.Rectangle(mat, new Rect(200, 150, 300, 200), new Scalar(70), -1);
            Cv2.Rectangle(mat, new Rect(450, 280, 24, 18), new Scalar(255), -1);
        });
        WriteMono(root, "CH02", 800, 600, mat =>
        {
            Cv2.Rectangle(mat, new Rect(120, 90, 260, 180), new Scalar(70), -1);
            Cv2.Rectangle(mat, new Rect(600, 380, 28, 22), new Scalar(150), -1);
        });
        WriteMono(root, "CH03", 800, 600, mat =>
            Cv2.Rectangle(mat, new Rect(120, 90, 260, 180), new Scalar(70), -1));
        WriteMono(root, "CH04", 800, 600, mat =>
        {
            Cv2.Rectangle(mat, new Rect(120, 90, 260, 180), new Scalar(70), -1);
            Cv2.Rectangle(mat, new Rect(100, 100, 20, 20), new Scalar(255), -1);
        });
        WriteMono(root, "CH05", 800, 600, mat =>
            Cv2.Rectangle(mat, new Rect(120, 90, 260, 180), new Scalar(70), -1));
        WriteColor(root, "CH06", 1200, 800, mat =>
        {
            Cv2.Rectangle(mat, new Rect(420, 220, 380, 300), new Scalar(70, 74, 78), -1);
            Cv2.Rectangle(mat, new Rect(900, 500, 40, 30), new Scalar(255, 255, 255), -1);
        });
        WriteMono(root, "CH07", 800, 600, mat =>
            Cv2.Rectangle(mat, new Rect(120, 90, 260, 180), new Scalar(70), -1));

        // One parent frame, two logical children: only ODD parent rows carry the
        // CH09 defect and only EVEN rows carry the CH10 defect. A rectangular block
        // would leak into both parities and could not prove 2-to-1 ownership.
        WriteMono(root, "LINE-S-8", 600, 1200, mat =>
            Rows(mat, 100, 179, 101, 105, 255));
        WriteMono(root, "LINE-M-2", 600, 1000, mat =>
            Rows(mat, 300, 379, 200, 204, 255));
    }

    private static void Rows(Mat mat, int x1, int x2, int firstRow, int lastRow, byte value)
    {
        for (var y = firstRow; y <= lastRow; y += 2)
            Cv2.Line(mat, new Point(x1, y), new Point(x2, y), new Scalar(value), 1);
    }

    private static void WriteMono(string root, string name, int width, int height,
        Action<Mat> decorate) =>
        Write(root, name, new Mat(height, width, MatType.CV_8UC1, new Scalar(30)), decorate,
            new Scalar(45));

    private static void WriteColor(string root, string name, int width, int height,
        Action<Mat> decorate) =>
        Write(root, name, new Mat(height, width, MatType.CV_8UC3, new Scalar(30, 32, 34)),
            decorate, new Scalar(50, 52, 54));

    private static void Write(string root, string name, Mat image, Action<Mat> decorate,
        Scalar texture)
    {
        using (image)
        {
            int rows = image.Rows;
            int cols = image.Cols;
            for (var y = 24; y < rows; y += 96)
                Cv2.Line(image, new Point(0, y), new Point(cols - 1, y), texture, 1);
            decorate(image);
            var path = Path.Combine(root, name + ".png");
            var pending = path + ".pending.png";
            if (!Cv2.ImWrite(pending, image))
                throw new IOException("OpenCV could not encode fixture PNG " + name);
            File.Move(pending, path, overwrite: false);
        }
    }
}
