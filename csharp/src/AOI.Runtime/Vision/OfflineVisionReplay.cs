using AOI.Domain;
using OpenCvSharp;

namespace AOI.Runtime.Vision;

/// <summary>Explicit SIM_ONLY contour recipe; this is not a trained or field-certified classifier.</summary>
public sealed record ContourDefectRule(byte BinaryThreshold, double MinimumContourAreaPixels,
    bool FindBrightDefects = true, int MaximumDetections = 128)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumContourAreaPixels) || MinimumContourAreaPixels <= 0 ||
            MaximumDetections is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(MinimumContourAreaPixels),
                "Contour area must be positive and the defect count bounded.");
    }
}

public sealed record DefectBox(int X, int Y, int Width, int Height, double AreaPixels);
public sealed record ContourInspection(ChannelOutcome Outcome, string Reason, ImageRegion Roi,
    IReadOnlyList<DefectBox> Boxes);

/// <summary>Pixel-based ROI/contour detection. Bounding boxes use source image coordinates.</summary>
public static class ContourDefectDetector
{
    public static ContourInspection Inspect(Mat source, ContourDefectRule rule, ImageRegion? configuredRoi = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();
        VisionPixelOps.RequireSupported(source, lineScan: false);
        var roi = configuredRoi ?? new ImageRegion(0, 0, source.Cols, source.Rows);
        roi.ValidateWithin(source.Cols, source.Rows);
        using var patch = new Mat(source, roi.ToRect());
        using var gray = new Mat();
        if (patch.Type() == MatType.CV_8UC1)
            patch.CopyTo(gray);
        else
            Cv2.CvtColor(patch, gray, patch.Type() == MatType.CV_8UC3
                ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, rule.BinaryThreshold, 255,
            rule.FindBrightDefects ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var defects = new List<DefectBox>();
        foreach (var contour in contours)
        {
            double area = Cv2.ContourArea(contour);
            if (area < rule.MinimumContourAreaPixels) continue;
            var rect = Cv2.BoundingRect(contour);
            defects.Add(new DefectBox(roi.X + rect.X, roi.Y + rect.Y,
                rect.Width, rect.Height, area));
            if (defects.Count > rule.MaximumDetections)
                throw new InvalidDataException("Too many contour defects for configured bounded result.");
        }
        defects.Sort((a, b) => b.AreaPixels.CompareTo(a.AreaPixels));
        return new ContourInspection(defects.Count == 0 ? ChannelOutcome.Ok : ChannelOutcome.Ng,
            defects.Count == 0 ? "NO_CONTOUR_DEFECT" : $"CONTOUR_DEFECTS:{defects.Count}",
            roi, defects);
    }

    /// <summary>Draw green ROI and red contour boxes directly on a copy of the actual input.</summary>
    public static Mat Annotate(Mat source, ContourInspection inspection)
    {
        VisionPixelOps.RequireSupported(source, lineScan: false);
        ArgumentNullException.ThrowIfNull(inspection);
        inspection.Roi.ValidateWithin(source.Cols, source.Rows);
        var overlay = new Mat();
        try
        {
            if (source.Type() == MatType.CV_8UC1)
                Cv2.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else if (source.Type() == MatType.CV_8UC4)
                Cv2.CvtColor(source, overlay, ColorConversionCodes.BGRA2BGR);
            else
                source.CopyTo(overlay);
            Cv2.Rectangle(overlay, inspection.Roi.ToRect(), new Scalar(0, 180, 0), thickness: 1);
            foreach (var d in inspection.Boxes)
                Cv2.Rectangle(overlay, new Rect(d.X, d.Y, d.Width, d.Height),
                    new Scalar(0, 0, 255), thickness: 2);
            return overlay;
        }
        catch
        {
            overlay.Dispose();
            throw;
        }
    }
}

public sealed record ReplayChannel(string ChannelId, ChannelOutcome Outcome, string Reason,
    ChannelResult? Observation, ImageRegion? Roi, string? SourcePath, string? OverlayPath,
    IReadOnlyList<DefectBox> Defects);

public sealed record ReplayBatch(Guid WorkpieceId, MachineRole OwnerRole,
    IReadOnlyList<ReplayChannel> Channels)
{
    public int Captured => Channels.Count(c => c.Observation is not null);
    public int Missing => Channels.Count(c => c.Outcome == ChannelOutcome.Missing);
    public int Defective => Channels.Count(c => c.Outcome == ChannelOutcome.Ng);
}

/// <summary>
/// Import operator-supplied PNGs from a workpiece-ID folder, or an explicitly opted-in
/// flat demo folder. No camera connection, PLC control or implicit image synthesis.
/// Required names: CH01.png ... CH07.png and LINE-S-8.png / LINE-M-2.png.
/// Files are attached to caller's immutable WorkpieceIdentity in VisionAssetLedger.
/// </summary>
public sealed class OfflineVisionReplay
{
    private const long MaximumPngBytes = 256L * 1024 * 1024;
    private readonly VisionAssetLedger _ledger;

    public OfflineVisionReplay(VisionAssetLedger ledger)
        => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <param name="flatDemoFolder">False requires imageRoot/WorkpieceId(N)/ files.
    /// True explicitly reuses files in imageRoot across workpieces for offline comparison.</param>
    public ReplayBatch CaptureOwned(WorkpieceIdentity item, MachineRole owner,
        string imageRoot, string annotationRoot, ContourDefectRule rule,
        IReadOnlyDictionary<string, ImageRegion>? roiByChannel = null,
        int attemptNo = 1, bool flatDemoFolder = false,
        Func<string, int, int, ImageRegion?>? releasedRoiForImage = null,
        Func<string, ContourDefectRule>? releasedRuleForChannel = null,
        string? deferPhysicalDeviceId = null)
    {
        item.Validate();
        rule.Validate();
        if (attemptNo < 1) throw new ArgumentOutOfRangeException(nameof(attemptNo));
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationRoot);
        var directory = Path.GetFullPath(flatDemoFolder
            ? imageRoot : Path.Combine(imageRoot, item.WorkpieceId.ToString("N")));
        var output = Path.GetFullPath(annotationRoot);
        var definitions = ChannelCatalog.All.Where(c => c.OwnerRole == owner &&
            (deferPhysicalDeviceId is null || c.PhysicalDeviceId != deferPhysicalDeviceId))
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        // An accidental ROI for another node's channel is never applied to this node's image.
        if (roiByChannel is not null && roiByChannel.Keys.Any(k =>
            !definitions.Any(c => c.ChannelId == k)))
            throw new ArgumentException("ROI map contains an unknown or foreign-node channel.", nameof(roiByChannel));
        var records = new List<ReplayChannel>(definitions.Length);
        foreach (var channel in definitions.Where(c => c.SharedFrameGroup is null))
        {
            var path = Path.Combine(directory, channel.ChannelId + ".png");
            if (!File.Exists(path))
            {
                records.Add(Failure(channel.ChannelId, ChannelOutcome.Missing, "MISSING_IMAGE", path));
                continue;
            }
            Mat image;
            try { image = DecodePng(path, lineScan: false); }
            catch (Exception ex) when (ex is IOException or ArgumentException or OpenCVException or InvalidDataException)
            {
                records.Add(Failure(channel.ChannelId, ChannelOutcome.Error, "INVALID_IMAGE:" + ex.Message, path));
                continue;
            }
            using (image)
            {
                var roi = SelectRoi(roiByChannel, channel.ChannelId) ??
                    releasedRoiForImage?.Invoke(channel.ChannelId, image.Cols, image.Rows);
                var inspection = ContourDefectDetector.Inspect(image,
                    releasedRuleForChannel?.Invoke(channel.ChannelId) ?? rule, roi);
                var capture = _ledger.CaptureAreaDetected(item, channel.ChannelId,
                    image, inspection.Outcome, attemptNo);
                records.Add(Complete(channel.ChannelId, path, output, item, capture, image, inspection));
            }
        }
        foreach (var group in definitions.Where(c => c.SharedFrameGroup is not null)
            .GroupBy(c => c.SharedFrameGroup!, StringComparer.Ordinal))
        {
            var mapped = group.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
            if (mapped.Length != 2 || mapped[0].PhysicalDeviceId != mapped[1].PhysicalDeviceId)
                throw new InvalidDataException("Line-scan group is not a single two-channel physical device.");
            var path = Path.Combine(directory, mapped[0].PhysicalDeviceId + ".png");
            if (!File.Exists(path))
            {
                records.AddRange(mapped.Select(c => Failure(c.ChannelId, ChannelOutcome.Missing, "MISSING_IMAGE", path)));
                continue;
            }
            Mat parent;
            try { parent = DecodePng(path, lineScan: true); }
            catch (Exception ex) when (ex is IOException or ArgumentException or OpenCVException or InvalidDataException)
            {
                records.AddRange(mapped.Select(c => Failure(c.ChannelId, ChannelOutcome.Error,
                    "INVALID_IMAGE:" + ex.Message, path)));
                continue;
            }
            using (parent)
            using (var even = VisionPixelOps.ExtractInterleavedRows(parent, 0))
            using (var odd = VisionPixelOps.ExtractInterleavedRows(parent, 1))
            {
                var scans = new[] { even, odd };
                var inspections = mapped.Select((c, i) =>
                    ContourDefectDetector.Inspect(scans[i],
                        releasedRuleForChannel?.Invoke(c.ChannelId) ?? rule,
                        SelectRoi(roiByChannel, c.ChannelId) ??
                        releasedRoiForImage?.Invoke(c.ChannelId, scans[i].Cols, scans[i].Rows))).ToArray();
                var outcomes = new Dictionary<string, ChannelOutcome>(StringComparer.Ordinal)
                {
                    [mapped[0].ChannelId] = inspections[0].Outcome,
                    [mapped[1].ChannelId] = inspections[1].Outcome
                };
                var captured = _ledger.CaptureLineInterleaved(item, group.Key, parent, attemptNo, outcomes);
                foreach (var c in mapped.Select((def, i) => (def, i)))
                {
                    var proof = captured.Single(r => r.ChannelId == c.def.ChannelId);
                    records.Add(Complete(c.def.ChannelId, path, output, item,
                        proof, scans[c.i], inspections[c.i]));
                }
            }
        }
        return new ReplayBatch(item.WorkpieceId, owner,
            records.OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray());
    }

    private static ImageRegion? SelectRoi(IReadOnlyDictionary<string, ImageRegion>? rois, string channel) =>
        rois is not null && rois.TryGetValue(channel, out var roi) ? roi : null;

    private static ReplayChannel Failure(string channel, ChannelOutcome status, string reason, string path) =>
        new(channel, status, reason, null, null, path, null, Array.Empty<DefectBox>());

    private static ReplayChannel Complete(string channel, string sourcePath, string outputRoot,
        WorkpieceIdentity item, ChannelResult capture, Mat pixels, ContourInspection inspected)
    {
        var folder = Path.Combine(outputRoot, item.WorkpieceId.ToString("N"), channel);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, capture.FrameId.ToString("N") + "-overlay.png");
        var pending = path + ".pending.png";
        using (var overlay = ContourDefectDetector.Annotate(pixels, inspected))
        {
            if (!Cv2.ImWrite(pending, overlay))
                throw new IOException("OpenCV could not save actual annotated PNG.");
        }
        File.Move(pending, path, overwrite: false);
        return new ReplayChannel(channel, capture.Outcome, inspected.Reason, capture,
            inspected.Roi, sourcePath, path, inspected.Boxes);
    }

    private static Mat DecodePng(string path, bool lineScan)
    {
        using (var file = File.OpenRead(path))
        {
            if (file.Length is < 16 or > MaximumPngBytes)
                throw new InvalidDataException("PNG file is empty or beyond offline input limit.");
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
            if ((long)pixels.Rows * pixels.Cols > 200_000_000)
                throw new InvalidDataException("Decoded PNG exceeds offline image pixel budget.");
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }
}
