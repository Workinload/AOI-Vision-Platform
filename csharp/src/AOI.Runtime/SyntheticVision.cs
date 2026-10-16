using AOI.Domain;
using OpenCvSharp;
using System.Security.Cryptography;

namespace AOI.Runtime;

/// <summary>Real OpenCvSharp5 pixel pipeline, but image data and verdict are SIMULATED, never defect-accuracy proof.</summary>
public static class SyntheticVision
{
    public static string NativeVersion => Cv2.GetVersionString() ?? "UNKNOWN";

    public static ChannelResult Generate(WorkpieceIdentity workpiece, string channelId, string assetsRoot)
    {
        var channel = ChannelCatalog.Require(channelId);
        if (channel.SharedFrameGroup is not null)
            throw new NotSupportedException("This initial area-camera SIM slice does not implement full line-scan processing.");
        // Real Cv2 calls and real lossless PNG bytes, but synthetic pixels only.
        using var image = new Mat(96, 96, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(15, 20, 42, 32), Scalar.White, thickness: -1);
        var pixels = Cv2.CountNonZero(image);
        if (!Cv2.ImEncode(".png", image, out var png) || png.Length == 0)
            throw new IOException("OpenCV failed to encode lossless synthetic frame.");
        var digest = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var captureId = Guid.NewGuid();
        var frameId = Guid.NewGuid();
        var folder = Path.Combine(assetsRoot, workpiece.WorkpieceId.ToString("N"), channel.ChannelId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, frameId.ToString("N") + ".png");
        var pending = path + ".pending";
        File.WriteAllBytes(pending, png);
        File.Move(pending, path); // same-directory atomic rename; asset metadata/DB recovery belongs to Agent03
        return new ChannelResult(Guid.NewGuid(), workpiece.WorkpieceId, workpiece.Epoch,
            workpiece.ReleaseId, workpiece.TraceId, channel.ChannelId, channel.StationId,
            channel.PhysicalDeviceId, captureId, frameId, null, 1, digest,
            pixels == 1344 ? ChannelOutcome.Ok : ChannelOutcome.Error,
            IsSimulated: true, DateTimeOffset.UtcNow);
    }
}
