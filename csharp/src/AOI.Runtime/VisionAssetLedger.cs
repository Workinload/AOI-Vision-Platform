using AOI.Contracts;
using AOI.Domain;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using System.Security.Cryptography;

namespace AOI.Runtime;

/// <summary>Configured, pixel-exact rectangle in source-frame coordinates (no implicit resize).</summary>
public sealed record ImageRegion(int X, int Y, int Width, int Height)
{
    public void ValidateWithin(int imageWidth, int imageHeight)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)X + Width > imageWidth || (long)Y + Height > imageHeight)
            throw new ArgumentOutOfRangeException(nameof(ImageRegion),
                "ROI is empty or outside the real parent frame; never silently clamp it.");
    }

    public Rect ToRect() => new(X, Y, Width, Height);
}

/// <summary>Real OpenCV measurements, NOT a validated industrial defect classifier.</summary>
public sealed record PixelProbe(double Mean, double StdDev, double BrightFraction, int Width, int Height);

/// <summary>Explicit deterministic SIM_ONLY threshold. Without it, outcome remains Uncertain.</summary>
public sealed record SimPixelRule(byte BrightThreshold, double MinimumBrightFraction)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumBrightFraction) || MinimumBrightFraction < 0 || MinimumBrightFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumBrightFraction));
    }
}

/// <summary>Only a SIM_ONLY pixel layout profile; not a real camera calibration.</summary>
public enum LineScanRowMapping { DemoEvenOdd, LegacySkipFirstRow }

public static class VisionPixelOps
{
    public static void RequireSupported(Mat pixels, bool lineScan)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Empty() || pixels.Rows <= 0 || pixels.Cols <= 0 ||
            (pixels.Type() != MatType.CV_8UC1 &&
             (lineScan || pixels.Type() != MatType.CV_8UC3 && pixels.Type() != MatType.CV_8UC4)))
            throw new ArgumentException("Expected nonempty 8-bit mono line scan or 8-bit mono/BGR/BGRA area frame.",
                nameof(pixels));
    }

    public static PixelProbe Probe(Mat pixels, byte brightThreshold = 127)
    {
        RequireSupported(pixels, lineScan: false);
        using var gray = new Mat();
        if (pixels.Type() == MatType.CV_8UC1)
            pixels.CopyTo(gray);
        else
            Cv2.CvtColor(pixels, gray, pixels.Type() == MatType.CV_8UC3
                ? ColorConversionCodes.BGR2GRAY : ColorConversionCodes.BGRA2GRAY);
        Cv2.MeanStdDev(gray, out var mean, out var deviation);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, brightThreshold, 255, ThresholdTypes.Binary);
        return new PixelProbe(mean.Val0, deviation.Val0,
            Cv2.CountNonZero(mask) / ((double)gray.Rows * gray.Cols), gray.Cols, gray.Rows);
    }

    /// <summary>
    /// Number of rows in one of two physical line-scan child images.
    /// The legacy profile reproduces source[1::2] and source[2::2], NOT [0::2]/[1::2].
    /// This is a historical software compatibility mode; field orientation is still unverified.
    /// </summary>
    public static int InterleavedChildRows(int parentRows, int parity,
        LineScanRowMapping mapping = LineScanRowMapping.DemoEvenOdd)
    {
        if (parity is not (0 or 1) || !Enum.IsDefined(mapping))
            throw new ArgumentOutOfRangeException(nameof(parity));
        int first = parity + (mapping == LineScanRowMapping.LegacySkipFirstRow ? 1 : 0);
        if (parentRows <= first)
            throw new ArgumentOutOfRangeException(nameof(parentRows));
        return (parentRows - first + 1) / 2;
    }

    /// <summary>Extract a logical line channel with explicit pixel-exact row origin.</summary>
    public static Mat ExtractInterleavedRows(Mat parent, int parity,
        LineScanRowMapping mapping = LineScanRowMapping.DemoEvenOdd)
    {
        RequireSupported(parent, lineScan: true);
        int height = InterleavedChildRows(parent.Rows, parity, mapping);
        int first = parity + (mapping == LineScanRowMapping.LegacySkipFirstRow ? 1 : 0);
        var child = new Mat(height, parent.Cols, MatType.CV_8UC1);
        try
        {
            var sourceRows = parent.AsRows<byte>();
            var destinationRows = child.AsRows<byte>();
            for (int y = first; y < parent.Rows; y += 2)
                sourceRows[y].CopyTo(destinationRows[(y - first) / 2]);
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Immutable evidence metadata. All persisted captures are offline/SIM_ONLY. Parent-child
/// frames share capture/identity; a line child SourceFrameSha256 names its original parent.
/// </summary>
public sealed record VisionAsset(
    Guid FrameId, Guid WorkpieceId, Guid TraceId, long Epoch, string ReleaseId,
    Guid CaptureId, string PhysicalDeviceId, string? ChannelId, Guid? ParentFrameId,
    int AttemptNo, int Width, int Height, string ContentSha256, string SourceFrameSha256,
    string RelativePath, ImageRegion? ParentRoi, DateTimeOffset CapturedUtc, bool IsSimulation,
    string? PixelTransform = null);

public sealed record VisionLedgerStats(long Assets, long AreaFrames, long LineParents,
    long LineChildren, long Workpieces, long StoredBytes, long ChannelResults);
public sealed record VisionIntegrityReport(int VerifiedAssets, IReadOnlyList<string> Problems);

/// <summary>
/// Isolated A3 image ledger. Does NOT change A0 contracts or production store, expose PLC
/// writes, attach live cameras, or assert production defect accuracy. Real OpenCV encodes and
/// decodes actual pixels. PNG assets are fsynced and renamed BEFORE SQLite WAL commit.
/// Crashed/uncommitted files are surfaced by Audit(), never silently accepted as results.
/// </summary>
public sealed class VisionAssetLedger
{
    private readonly string _root;
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public VisionAssetLedger(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        DatabasePath = Path.Combine(_root, "vision-assets.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();
        using var db = Open();
        using var init = db.CreateCommand();
        init.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS vision_assets (
              frame_id TEXT PRIMARY KEY,
              workpiece_id TEXT NOT NULL,
              epoch INTEGER NOT NULL,
              release_id TEXT NOT NULL,
              channel_id TEXT,
              parent_frame_id TEXT REFERENCES vision_assets(frame_id),
              kind TEXT NOT NULL CHECK (kind IN ('area','parent','child')),
              relative_path TEXT NOT NULL UNIQUE,
              bytes INTEGER NOT NULL CHECK (bytes > 0),
              asset_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_vision_assets_workpiece ON vision_assets(workpiece_id);
            CREATE TABLE IF NOT EXISTS vision_results (
              result_id TEXT PRIMARY KEY,
              workpiece_id TEXT NOT NULL,
              channel_id TEXT NOT NULL,
              epoch INTEGER NOT NULL,
              release_id TEXT NOT NULL,
              attempt_no INTEGER NOT NULL,
              frame_id TEXT NOT NULL REFERENCES vision_assets(frame_id),
              result_json TEXT NOT NULL,
              UNIQUE(workpiece_id, channel_id, epoch, release_id, attempt_no)
            );
            """;
        init.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Persist a single real pixel buffer from a configured area/positioning channel.</summary>
    public ChannelResult CaptureArea(WorkpieceIdentity item, string channelId, Mat pixels,
        int attemptNo = 1, SimPixelRule? simRule = null)
    {
        item.Validate();
        var channel = ChannelCatalog.Require(channelId);
        if (channel.SharedFrameGroup is not null)
            throw new ArgumentException("Line-scan channels must be captured once per shared parent.", nameof(channelId));
        ValidateCapture(pixels, attemptNo, simRule, lineScan: false);
        var captureId = Guid.NewGuid();
        var frame = NewAsset(item, channel.PhysicalDeviceId, channelId, captureId,
            Guid.NewGuid(), null, attemptNo, pixels, null);
        return Persist(item, [(frame, pixels)], simRule)[0];
    }

    /// <summary>Commit the actual pixels with a result computed by the A3 OpenCV detector.</summary>
    public ChannelResult CaptureAreaDetected(WorkpieceIdentity item, string channelId,
        Mat pixels, ChannelOutcome outcome, int attemptNo = 1,
        Guid? captureId = null, Guid? frameId = null)
    {
        item.Validate();
        var channel = ChannelCatalog.Require(channelId);
        if (channel.SharedFrameGroup is not null)
            throw new ArgumentException("Line-scan channel requires shared parent.", nameof(channelId));
        ValidateCapture(pixels, attemptNo, rule: null, lineScan: false);
        RequireDetectedOutcome(outcome);
        if (captureId == Guid.Empty || frameId == Guid.Empty)
            throw new ArgumentException("Acquisition identity cannot be empty.");
        var frame = NewAsset(item, channel.PhysicalDeviceId, channelId, captureId ?? Guid.NewGuid(),
            frameId ?? Guid.NewGuid(), null, attemptNo, pixels, null);
        return Persist(item, [(frame, pixels)], null,
            new Dictionary<string, ChannelOutcome>(StringComparer.Ordinal) { [channelId] = outcome })[0];
    }

    /// <summary>
    /// Save ONE unchanged line-scan parent and TWO lossless, configured ROI crops atomically
    /// in the WAL metadata. No implicit geometry, approximate interpolation or fake cameras.
    /// </summary>
    public IReadOnlyList<ChannelResult> CaptureLinePair(WorkpieceIdentity item, string sharedFrameGroup,
        Mat parent, ImageRegion firstRoi, ImageRegion secondRoi,
        int attemptNo = 1, SimPixelRule? simRule = null)
    {
        item.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedFrameGroup);
        ArgumentNullException.ThrowIfNull(firstRoi);
        ArgumentNullException.ThrowIfNull(secondRoi);
        ValidateCapture(parent, attemptNo, simRule, lineScan: true);
        firstRoi.ValidateWithin(parent.Cols, parent.Rows);
        secondRoi.ValidateWithin(parent.Cols, parent.Rows);
        var channels = ChannelCatalog.All.Where(c =>
            string.Equals(c.SharedFrameGroup, sharedFrameGroup, StringComparison.Ordinal))
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        if (channels.Length != 2 || channels[0].PhysicalDeviceId != channels[1].PhysicalDeviceId ||
            channels[0].StationId != channels[1].StationId || channels[0].OwnerRole != channels[1].OwnerRole)
            throw new ArgumentException("Configured group must map one real device to exactly two channels.",
                nameof(sharedFrameGroup));

        var captureId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var parentAsset = NewAsset(item, channels[0].PhysicalDeviceId, null, captureId,
            parentId, null, attemptNo, parent, null);
        using var first = new Mat(parent, firstRoi.ToRect());
        using var second = new Mat(parent, secondRoi.ToRect());
        var childA = NewAsset(item, channels[0].PhysicalDeviceId, channels[0].ChannelId,
            captureId, Guid.NewGuid(), parentId, attemptNo, first, firstRoi);
        var childB = NewAsset(item, channels[1].PhysicalDeviceId, channels[1].ChannelId,
            captureId, Guid.NewGuid(), parentId, attemptNo, second, secondRoi);
        return Persist(item, [(parentAsset, parent), (childA, first), (childB, second)], simRule);
    }

    /// <summary>
    /// Extract row 0/2/4/... into the first mapped channel and 1/3/5/... into the second;
    /// retain the immutable complete parent and verify pixel-exact parity upon readback.
    /// </summary>
    public IReadOnlyList<ChannelResult> CaptureLineInterleaved(WorkpieceIdentity item,
        string sharedFrameGroup, Mat parent, int attemptNo = 1,
        IReadOnlyDictionary<string, ChannelOutcome>? detectedOutcomes = null,
        Guid? captureId = null, Guid? parentFrameId = null,
        LineScanRowMapping rowMapping = LineScanRowMapping.DemoEvenOdd)
    {
        item.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedFrameGroup);
        ValidateCapture(parent, attemptNo, rule: null, lineScan: true);
        var channels = ChannelCatalog.All.Where(c =>
            string.Equals(c.SharedFrameGroup, sharedFrameGroup, StringComparison.Ordinal))
            .OrderBy(c => c.ChannelId, StringComparer.Ordinal).ToArray();
        if (channels.Length != 2 || channels[0].PhysicalDeviceId != channels[1].PhysicalDeviceId ||
            channels[0].StationId != channels[1].StationId || channels[0].OwnerRole != channels[1].OwnerRole)
            throw new ArgumentException("Unknown or invalid two-channel line-scan group.", nameof(sharedFrameGroup));
        if (detectedOutcomes is not null)
        {
            if (detectedOutcomes.Count != 2 ||
                channels.Any(c => !detectedOutcomes.TryGetValue(c.ChannelId, out var verdict) ||
                                  verdict is not (ChannelOutcome.Ok or ChannelOutcome.Ng or ChannelOutcome.Uncertain)))
                throw new ArgumentException("Missing or unknown line channel detection outcome.",
                    nameof(detectedOutcomes));
        }
        using var even = VisionPixelOps.ExtractInterleavedRows(parent, 0, rowMapping);
        using var odd = VisionPixelOps.ExtractInterleavedRows(parent, 1, rowMapping);
        if (captureId == Guid.Empty || parentFrameId == Guid.Empty)
            throw new ArgumentException("Acquisition identity cannot be empty.");
        var sharedCapture = captureId ?? Guid.NewGuid();
        var parentId = parentFrameId ?? Guid.NewGuid();
        var asset = NewAsset(item, channels[0].PhysicalDeviceId, null,
            sharedCapture, parentId, null, attemptNo, parent, null);
        var firstTransform = rowMapping == LineScanRowMapping.LegacySkipFirstRow
            ? "legacy-interleaved-row1-step2" : "interleaved-even-rows";
        var secondTransform = rowMapping == LineScanRowMapping.LegacySkipFirstRow
            ? "legacy-interleaved-row2-step2" : "interleaved-odd-rows";
        var first = NewAsset(item, channels[0].PhysicalDeviceId, channels[0].ChannelId,
            sharedCapture, Guid.NewGuid(), parentId, attemptNo, even, null, firstTransform);
        var second = NewAsset(item, channels[1].PhysicalDeviceId, channels[1].ChannelId,
            sharedCapture, Guid.NewGuid(), parentId, attemptNo, odd, null, secondTransform);
        return Persist(item, [(asset, parent), (first, even), (second, odd)], null, detectedOutcomes);
    }

    private static void RequireDetectedOutcome(ChannelOutcome outcome)
    {
        if (outcome is not (ChannelOutcome.Ok or ChannelOutcome.Ng or ChannelOutcome.Uncertain))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only an actual A3 detection result can be persisted.");
    }

    private static void ValidateCapture(Mat pixels, int attemptNo, SimPixelRule? rule, bool lineScan)
    {
        VisionPixelOps.RequireSupported(pixels, lineScan);
        if (attemptNo < 1) throw new ArgumentOutOfRangeException(nameof(attemptNo));
        rule?.Validate();
    }

    private static VisionAsset NewAsset(WorkpieceIdentity item, string device, string? channel,
        Guid captureId, Guid frameId, Guid? parentId, int attempt, Mat pixels, ImageRegion? roi,
        string? transform = null) =>
        new(frameId, item.WorkpieceId, item.TraceId, item.Epoch, item.ReleaseId,
            captureId, device, channel, parentId, attempt, pixels.Cols, pixels.Rows,
            string.Empty, string.Empty, RelativeName(item.WorkpieceId, frameId), roi,
            DateTimeOffset.UtcNow, IsSimulation: true, PixelTransform: transform);

    private static string RelativeName(Guid workpiece, Guid frame) =>
        Path.Combine("objects", workpiece.ToString("N"), frame.ToString("N") + ".png");

    private sealed record Encoded(VisionAsset Asset, long Bytes, string PendingPath);

    private IReadOnlyList<ChannelResult> Persist(WorkpieceIdentity item,
        IReadOnlyList<(VisionAsset Asset, Mat Pixels)> drafts, SimPixelRule? rule,
        IReadOnlyDictionary<string, ChannelOutcome>? detectedOutcomes = null)
    {
        var encoded = new List<Encoded>(drafts.Count);
        // A line parent is the first item; all children reference exactly this parent digest.
        foreach (var (asset, pixels) in drafts)
        {
            var path = FullPath(asset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var pending = path + ".pending.png";
            // Never silently overwrite a preexisting file, including a crash-recovery remnant.
            if (File.Exists(path) || File.Exists(pending))
                throw new IOException("Immutable capture already exists; investigate frame identity collision.");
            if (!Cv2.ImWrite(pending, pixels))
                throw new IOException("Native OpenCV failed to losslessly encode pixel asset.");
            string hash;
            long bytes;
            using (var stream = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
                bytes = stream.Length;
                stream.Position = 0;
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            if (bytes <= 0) throw new InvalidDataException("OpenCV emitted an empty PNG.");
            var source = asset.ParentFrameId is null ? hash : encoded[0].Asset.ContentSha256;
            encoded.Add(new Encoded(asset with { ContentSha256 = hash, SourceFrameSha256 = source },
                bytes, pending));
        }

        var results = new List<ChannelResult>();
        for (var i = 0; i < encoded.Count; i++)
        {
            var asset = encoded[i].Asset;
            if (asset.ChannelId is null) continue; // parent alone is not an inspection result
            var outcome = ChannelOutcome.Uncertain;
            if (detectedOutcomes is not null)
            {
                if (!detectedOutcomes.TryGetValue(asset.ChannelId, out outcome))
                    throw new InvalidDataException("Missing OpenCV detection outcome for real image channel.");
                RequireDetectedOutcome(outcome);
            }
            else if (rule is not null)
            {
                var probe = VisionPixelOps.Probe(drafts[i].Pixels, rule.BrightThreshold);
                outcome = probe.BrightFraction >= rule.MinimumBrightFraction
                    ? ChannelOutcome.Ok : ChannelOutcome.Ng;
            }
            var channel = ChannelCatalog.Require(asset.ChannelId);
            var result = new ChannelResult(Guid.NewGuid(), item.WorkpieceId, item.Epoch,
                item.ReleaseId, item.TraceId, channel.ChannelId, channel.StationId,
                channel.PhysicalDeviceId, asset.CaptureId, asset.FrameId, asset.ParentFrameId,
                asset.AttemptNo, asset.SourceFrameSha256, outcome, IsSimulated: true,
                DateTimeOffset.UtcNow);
            result.ValidateFor(item);
            results.Add(result);
        }

        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var row in encoded)
        {
            // After a crash between rename and commit, Audit() exposes unreferenced assets.
            File.Move(row.PendingPath, FullPath(row.Asset), overwrite: false);
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO vision_assets(frame_id,workpiece_id,epoch,release_id,channel_id,
                   parent_frame_id,kind,relative_path,bytes,asset_json)
                VALUES($frame,$workpiece,$epoch,$release,$channel,$parent,$kind,$path,$bytes,$json)
                """;
            cmd.Parameters.AddWithValue("$frame", row.Asset.FrameId.ToString("D"));
            cmd.Parameters.AddWithValue("$workpiece", row.Asset.WorkpieceId.ToString("D"));
            cmd.Parameters.AddWithValue("$epoch", row.Asset.Epoch);
            cmd.Parameters.AddWithValue("$release", row.Asset.ReleaseId);
            cmd.Parameters.AddWithValue("$channel", (object?)row.Asset.ChannelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$parent", (object?)row.Asset.ParentFrameId?.ToString("D") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", row.Asset.ChannelId is null ? "parent" :
                row.Asset.ParentFrameId is null ? "area" : "child");
            cmd.Parameters.AddWithValue("$path", row.Asset.RelativePath);
            cmd.Parameters.AddWithValue("$bytes", row.Bytes);
            cmd.Parameters.AddWithValue("$json", WireFormat.Serialize(row.Asset));
            cmd.ExecuteNonQuery();
        }
        foreach (var result in results)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO vision_results(result_id,workpiece_id,channel_id,epoch,release_id,
                   attempt_no,frame_id,result_json)
                VALUES($id,$workpiece,$channel,$epoch,$release,$attempt,$frame,$json)
                """;
            cmd.Parameters.AddWithValue("$id", result.ResultId.ToString("D"));
            cmd.Parameters.AddWithValue("$workpiece", result.WorkpieceId.ToString("D"));
            cmd.Parameters.AddWithValue("$channel", result.ChannelId);
            cmd.Parameters.AddWithValue("$epoch", result.Epoch);
            cmd.Parameters.AddWithValue("$release", result.ReleaseId);
            cmd.Parameters.AddWithValue("$attempt", result.AttemptNo);
            cmd.Parameters.AddWithValue("$frame", result.FrameId.ToString("D"));
            cmd.Parameters.AddWithValue("$json", WireFormat.Serialize(result));
            cmd.ExecuteNonQuery();
        }
        transaction.Commit(); // only now may the caller publish these results
        return results;
    }

    private string FullPath(VisionAsset asset)
    {
        if (!string.Equals(asset.RelativePath, RelativeName(asset.WorkpieceId, asset.FrameId),
            StringComparison.Ordinal))
            throw new InvalidDataException("Tampered asset path rejected.");
        return Path.Combine(_root, asset.RelativePath);
    }

    public VisionAsset? GetAsset(Guid frameId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT asset_json FROM vision_assets WHERE frame_id=$id";
        cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
        return cmd.ExecuteScalar() is string json ? WireFormat.Parse<VisionAsset>(json) : null;
    }

    public IReadOnlyList<ChannelResult> ResultsFor(Guid workpieceId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT result_json FROM vision_results WHERE workpiece_id=$id
            ORDER BY channel_id, attempt_no
            """;
        cmd.Parameters.AddWithValue("$id", workpieceId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var found = new List<ChannelResult>();
        while (reader.Read())
        {
            var result = WireFormat.Parse<ChannelResult>(reader.GetString(0));
            var meta = GetAsset(result.FrameId) ??
                throw new InvalidDataException("Channel result points to missing frame metadata.");
            if (!meta.IsSimulation || !result.IsSimulated || meta.WorkpieceId != result.WorkpieceId ||
                meta.TraceId != result.TraceId || meta.Epoch != result.Epoch ||
                meta.ReleaseId != result.ReleaseId || meta.CaptureId != result.CaptureId ||
                meta.FrameId != result.FrameId || meta.ParentFrameId != result.ParentFrameId ||
                meta.ChannelId != result.ChannelId || meta.PhysicalDeviceId != result.PhysicalDeviceId ||
                meta.AttemptNo != result.AttemptNo || meta.SourceFrameSha256 != result.SourceFrameSha256)
                throw new InvalidDataException("Channel result and immutable image evidence disagree.");
            using var verifiedPixels = LoadVerified(result.FrameId);
            found.Add(result);
        }
        return found;
    }

    /// <summary>Fail closed on corrupted/missing PNG, incorrect shape, parent or child pixel lineage.</summary>
    public Mat LoadVerified(Guid frameId)
    {
        var asset = GetAsset(frameId) ?? throw new FileNotFoundException("Unknown committed frame.");
        var path = FullPath(asset);
        string actualHash;
        using (var input = File.OpenRead(path))
            actualHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (!string.Equals(actualHash, asset.ContentSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Asset SHA256 differs from immutable WAL metadata.");
        Mat image = Cv2.ImRead(path, ImreadModes.Unchanged);
        try
        {
            VisionPixelOps.RequireSupported(image, lineScan: asset.ChannelId is null || asset.ParentFrameId is not null);
            if (image.Cols != asset.Width || image.Rows != asset.Height)
                throw new InvalidDataException("Decoded pixel dimensions do not match asset metadata.");
            if (asset.ParentFrameId is Guid parentId)
            {
                var parent = GetAsset(parentId) ??
                    throw new InvalidDataException("Missing original line-scan parent metadata.");
                if (parent.ParentFrameId is not null || parent.ChannelId is not null ||
                    parent.WorkpieceId != asset.WorkpieceId || parent.TraceId != asset.TraceId ||
                    parent.Epoch != asset.Epoch || parent.ReleaseId != asset.ReleaseId ||
                    parent.CaptureId != asset.CaptureId || parent.PhysicalDeviceId != asset.PhysicalDeviceId ||
                    parent.ContentSha256 != asset.SourceFrameSha256 || asset.ChannelId is null)
                    throw new InvalidDataException("Wrong line-scan parent/child identity or source hash.");
                using var original = LoadVerified(parentId);
                using Mat expected = asset.PixelTransform switch
                {
                    null when asset.ParentRoi is not null => CropExpected(original, asset.ParentRoi),
                    "interleaved-even-rows" when CorrectInterleavedChannel(asset, parity: 0) && asset.ParentRoi is null
                        => VisionPixelOps.ExtractInterleavedRows(original, 0),
                    "interleaved-odd-rows" when CorrectInterleavedChannel(asset, parity: 1) && asset.ParentRoi is null
                        => VisionPixelOps.ExtractInterleavedRows(original, 1),
                    "legacy-interleaved-row1-step2" when CorrectInterleavedChannel(asset, parity: 0) && asset.ParentRoi is null
                        => VisionPixelOps.ExtractInterleavedRows(original, 0, LineScanRowMapping.LegacySkipFirstRow),
                    "legacy-interleaved-row2-step2" when CorrectInterleavedChannel(asset, parity: 1) && asset.ParentRoi is null
                        => VisionPixelOps.ExtractInterleavedRows(original, 1, LineScanRowMapping.LegacySkipFirstRow),
                    _ => throw new InvalidDataException("Unsupported or mismatched parent-to-child pixel transform.")
                };
                if (expected.Cols != image.Cols || expected.Rows != image.Rows)
                    throw new InvalidDataException("Line child pixel dimensions differ from source transform.");
                using var delta = new Mat();
                Cv2.Absdiff(image, expected, delta);
                if (Cv2.CountNonZero(delta) != 0)
                    throw new InvalidDataException("Child pixels do not match original parent transformation.");
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static Mat CropExpected(Mat parent, ImageRegion roi)
    {
        roi.ValidateWithin(parent.Cols, parent.Rows);
        return new Mat(parent, roi.ToRect());
    }

    private static bool CorrectInterleavedChannel(VisionAsset child, int parity)
    {
        var definition = ChannelCatalog.Require(child.ChannelId!);
        if (definition.SharedFrameGroup is null || definition.PhysicalDeviceId != child.PhysicalDeviceId)
            return false;
        var members = ChannelCatalog.All.Where(x => x.SharedFrameGroup == definition.SharedFrameGroup)
            .OrderBy(x => x.ChannelId, StringComparer.Ordinal).ToArray();
        return members.Length == 2 && members[parity].ChannelId == child.ChannelId;
    }

    public VisionLedgerStats Stats()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
              COALESCE(SUM(CASE WHEN kind='area' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN kind='parent' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN kind='child' THEN 1 ELSE 0 END),0),
              COUNT(DISTINCT workpiece_id), COALESCE(SUM(bytes),0),
              (SELECT COUNT(*) FROM vision_results) FROM vision_assets
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("SQLite statistics query returned no row.");
        return new VisionLedgerStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
    }

    /// <summary>Read-only crash/integrity audit; never silently delete or reattach orphan pixels.</summary>
    public VisionIntegrityReport Audit()
    {
        var problems = new List<string>();
        var indexed = new HashSet<string>(StringComparer.Ordinal);
        var verified = 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT frame_id, asset_json FROM vision_assets ORDER BY frame_id";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                try
                {
                    var meta = WireFormat.Parse<VisionAsset>(reader.GetString(1));
                    if (meta.FrameId != id) throw new InvalidDataException("Metadata ID mismatch.");
                    indexed.Add(Path.GetFullPath(FullPath(meta)));
                    using var image = LoadVerified(id);
                    verified++;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or OpenCVException or InvalidDataException or System.Text.Json.JsonException)
                {
                    problems.Add($"FRAME {id:N}: {ex.Message}");
                }
            }
        }
        var objectsRoot = Path.Combine(_root, "objects");
        if (Directory.Exists(objectsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(objectsRoot, "*.png", SearchOption.AllDirectories))
            {
                if (!indexed.Contains(Path.GetFullPath(file)))
                    problems.Add("UNCOMMITTED_OR_PENDING_ASSET: " + Path.GetRelativePath(_root, file));
            }
        }
        return new VisionIntegrityReport(verified, problems);
    }
}
