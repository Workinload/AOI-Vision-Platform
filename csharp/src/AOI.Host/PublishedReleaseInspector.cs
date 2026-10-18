using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using AOI.Solutions;
using OpenCvSharp;
using System.Diagnostics;
using System.Globalization;

namespace AOI.Host;

/// <summary>
/// A0 per-physical-capture adapter from an immutable, WorkpieceId-bound A6
/// published plan to A3's real OpenCV inspection processor. The instance is
/// created for ONE physical trigger: no mutable global/current recipe, no
/// cross-workpiece algorithm borrowing, no camera/PLC write permission.
/// </summary>
internal sealed class PublishedReleaseInspector : IInspectionProcessor
{
    private readonly IReadOnlyDictionary<string, ResolvedChannelInspectionSettings> _channels;
    private readonly OpenCvInspectionProcessor _contour = new();

    public PublishedReleaseInspector(ResolvedPhysicalInspectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _channels = plan.Inspections.ToDictionary(x => x.ChannelId, StringComparer.Ordinal);
        if (_channels.Count != plan.Capture.ChannelIds.Count ||
            !plan.Capture.ChannelIds.SequenceEqual(
                plan.Inspections.Select(x => x.ChannelId), StringComparer.Ordinal) ||
            plan.Inspections.Any(p => p.WorkpieceId != plan.Capture.WorkpieceId ||
                p.ReleaseId != plan.Capture.ReleaseId || p.Epoch != plan.Capture.Epoch ||
                p.PhysicalDeviceId != plan.Capture.PhysicalDeviceId))
            throw new InvalidDataException("PUBLISHED_CAPTURE_PLAN_IDENTITY_MISMATCH");
    }

    public PixelInspection Inspect(ChannelDefinition channel, Mat pixels,
        InspectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(settings);
        if (!_channels.TryGetValue(channel.ChannelId, out var selected) ||
            channel.PhysicalDeviceId != selected.PhysicalDeviceId ||
            settings.AlgorithmId != selected.AlgorithmId)
            return Unavailable("PUBLISHED_INSPECTOR_CHANNEL_OR_ALGORITHM_MISMATCH");
        var roi = selected.PixelRoi(pixels.Cols, pixels.Rows);
        var area = new ImageRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (selected.Kind == PublishedInspectionKind.OpenCvContour)
        {
            if (selected.MinimumContourAreaPixels is not { } minimumArea ||
                selected.FindBrightDefects is not { } findBright ||
                selected.MaximumDetections is not { } maxDetections)
                return Unavailable("PUBLISHED_CONTOUR_PARAMETERS_MISSING");
            return _contour.Inspect(channel, pixels,
                InspectionSettings.OpenCvContour(
                    new ContourDefectRule(selected.BinaryThreshold, minimumArea,
                        findBright, maxDetections), area));
        }
        if (selected.Kind == PublishedInspectionKind.LegacyBrightness)
        {
            if (selected.MinimumBrightFraction is not { } minimum ||
                !double.IsFinite(minimum) || minimum is < 0 or > 1)
                return Unavailable("PUBLISHED_LEGACY_BRIGHTNESS_PARAMETERS_MISSING");
            // The old SIM_ONLY brightness rule is a measured pixel fraction,
            // NOT a contour count. Use the same native grayscale threshold
            // semantics as A6 RoiPixelPreview.Measure. No defect box is
            // invented by an algorithm that did not detect contours.
            long began = Stopwatch.GetTimestamp();
            using var gray = new Mat();
            if (pixels.Type() == MatType.CV_8UC1)
                pixels.CopyTo(gray);
            else if (pixels.Type() == MatType.CV_8UC3)
                Cv2.CvtColor(pixels, gray, ColorConversionCodes.BGR2GRAY);
            else if (pixels.Type() == MatType.CV_8UC4)
                Cv2.CvtColor(pixels, gray, ColorConversionCodes.BGRA2GRAY);
            else
                return Unavailable("PUBLISHED_UNSUPPORTED_IMAGE_PIXELS");
            using var crop = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
            using var binary = new Mat();
            Cv2.Threshold(crop, binary, selected.BinaryThreshold, 255, ThresholdTypes.Binary);
            double fraction = Cv2.CountNonZero(binary) / ((double)roi.Width * roi.Height);
            bool good = fraction >= minimum;
            return new PixelInspection(good ? ChannelOutcome.Ok : ChannelOutcome.Ng,
                "SIM_LEGACY_BRIGHTNESS_" + (good ? "OK_" : "NG_") +
                fraction.ToString("0.000000", CultureInfo.InvariantCulture),
                Array.Empty<DefectBox>(),
                Stopwatch.GetElapsedTime(began).TotalMilliseconds, null);
        }
        return Unavailable("PUBLISHED_INSPECTION_PLUGIN_NOT_INSTALLED");
    }

    private static PixelInspection Unavailable(string reason) =>
        new(ChannelOutcome.Error, reason, Array.Empty<DefectBox>(), 0, null);
}
