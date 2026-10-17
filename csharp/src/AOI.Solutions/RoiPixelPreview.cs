using AOI.Domain;
using OpenCvSharp;

namespace AOI.Solutions;

/// <summary>
/// Reproducible native OpenCvSharp5 preview of the SAME source PNG with two
/// station recipes. Returns actual pixel measurements, not a mock verdict.
/// Brightness is only a SIM_ONLY surrogate; never claim field defect accuracy.
/// </summary>
public sealed record RoiPixelMeasurement(
    string ChannelId, PixelInspectionRoi PixelRegion, byte BrightThreshold,
    double MinimumBrightFraction, double BrightFraction,
    ChannelOutcome Outcome, int SourceWidth, int SourceHeight);

public sealed record RoiPixelComparison(
    RoiPixelMeasurement Before, RoiPixelMeasurement After)
{
    public bool Changed => Before.Outcome != After.Outcome;
}

public static class RoiPixelPreview
{
    public static RoiPixelMeasurement Measure(
        string pngPath, StationRecipe station, string channelId)
    {
        if (!File.Exists(pngPath)) throw new FileNotFoundException("Source PNG missing.", pngPath);
        if (!string.Equals(Path.GetExtension(pngPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ROI preview accepts lossless PNG only.");
        using var image = Cv2.ImRead(pngPath, ImreadModes.Unchanged);
        if (image.Empty() || (image.Type() != MatType.CV_8UC1 &&
            image.Type() != MatType.CV_8UC3 && image.Type() != MatType.CV_8UC4))
            throw new InvalidDataException("Expected non-empty 8-bit PNG pixels.");
        return Measure(image, station, channelId);
    }

    public static RoiPixelMeasurement Measure(Mat source, StationRecipe station, string channelId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        var roi = RoiReleaseWorkflow.ReadStationRoi(station, channelId)?.Bounds
            ?? NormalizedInspectionRoi.FullFrame;
        roi.Validate(channelId);
        var rect = roi.ToPixels(source.Cols, source.Rows);
        (byte threshold, double minimum) = ReadRule(station);
        using var gray = new Mat();
        if (source.Type() == MatType.CV_8UC1)
            source.CopyTo(gray);
        else
            Cv2.CvtColor(source, gray, source.Type() == MatType.CV_8UC3
                ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY);
        using var roiPixels = new Mat(gray, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        using var binary = new Mat();
        Cv2.Threshold(roiPixels, binary, threshold, 255, ThresholdTypes.Binary);
        double fraction = Cv2.CountNonZero(binary) / ((double)rect.Width * rect.Height);
        return new(channelId, rect, threshold, minimum, fraction,
            fraction >= minimum ? ChannelOutcome.Ok : ChannelOutcome.Ng,
            source.Cols, source.Rows);
    }

    public static RoiPixelComparison Compare(
        string pngPath, StationRecipe before, StationRecipe after, string channelId)
    {
        if (!File.Exists(pngPath)) throw new FileNotFoundException("Source PNG missing.", pngPath);
        if (!string.Equals(Path.GetExtension(pngPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ROI preview accepts lossless PNG only.");
        using var pixels = Cv2.ImRead(pngPath, ImreadModes.Unchanged);
        ValidateSource(pixels);
        return Compare(pixels, before, after, channelId);
    }

    public static RoiPixelComparison Compare(
        Mat source, StationRecipe before, StationRecipe after, string channelId)
    {
        ValidateSource(source);
        return new(Measure(source, before, channelId), Measure(source, after, channelId));
    }

    /// <summary>The A0 SIM_FULL geometry in native OpenCV (CH06 area fixture).</summary>
    public static void WriteSimAreaExample(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Example must be a PNG.", nameof(path));
        using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(8, 8, 48, 48), Scalar.White, -1);
        if (!Cv2.ImEncode(".png", image, out var png) || png.Length == 0)
            throw new IOException("Native OpenCV image encoding failed.");
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, png);
    }

    public static (byte BrightThreshold, double MinimumFraction) ReadRule(StationRecipe station)
    {
        station.Validate();
        double rawThreshold = station.Tunables.TryGetValue("brightThreshold", out var t) ? t : 127;
        double rawFraction = station.Tunables.TryGetValue("minimumBrightFraction", out var f) ? f : 0.1;
        if (!double.IsFinite(rawThreshold) || rawThreshold != Math.Floor(rawThreshold) ||
            rawThreshold is < 1 or > 254 ||
            !double.IsFinite(rawFraction) || rawFraction is < 0 or > 1)
            throw new InvalidDataException("Unsupported SIM_ONLY brightness rule.");
        return (checked((byte)rawThreshold), rawFraction);
    }

    private static void ValidateSource(Mat source)
    {
        if (source is null || source.Empty() || source.Rows <= 0 || source.Cols <= 0 ||
            (source.Type() != MatType.CV_8UC1 &&
             source.Type() != MatType.CV_8UC3 && source.Type() != MatType.CV_8UC4))
            throw new InvalidDataException("Expected a nonempty 8-bit mono/BGR/BGRA source frame.");
    }
}
