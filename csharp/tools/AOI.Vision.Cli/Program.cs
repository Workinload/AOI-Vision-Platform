using AOI.Domain;
using AOI.Runtime;
using AOI.Runtime.Vision;
using System.Net;
using System.Text;
using System.Text.Json;

// Deliberately an offline-only utility, NOT a second host/core or camera/PLC controller.
try
{
    return Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine("AOI_VISION_REPLAY_ERROR " + error.Message);
    return 2;
}

static int Run(string[] args)
{
    if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            Usage: dotnet run --project csharp/tools/AOI.Vision.Cli --
                 --images /path/to/pngs --out /path/to/report --role all --flat
                 [--threshold 127] [--min-area 5] [--roi-json roi.json]
                 [--barcode EXAMPLE] [--workpiece GUID] [--epoch 1] [--release SIM_LAB]
            Flat input: CH01.png ... CH07.png and LINE-S-8.png / LINE-M-2.png.
            Without --flat, supply --workpiece GUID and use --images/GUID_N/CHxx.png.
            
            Actual 8-bit PNG import, real OpenCV threshold/contours, original evidence in
            SQLite WAL, annotated PNGs, HTML report. Always SIM_ONLY; NO hardware access.
            """);
        return 0;
    }
    var opts = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--flat") { opts["--flat"] = "true"; continue; }
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || ++i >= args.Length)
            throw new ArgumentException("Malformed CLI argument list.");
        opts.Add(args[i - 1], args[i]);
    }
    string Need(string key) => opts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing required option " + key);
    string Get(string key, string fallback) => opts.TryGetValue(key, out var v) ? v : fallback;
    var imageRoot = Path.GetFullPath(Need("--images"));
    var outRoot = Path.GetFullPath(Need("--out"));
    var role = Get("--role", "all").ToLowerInvariant();
    if (role is not ("master" or "slave" or "all"))
        throw new ArgumentException("--role must be master, slave, or all.");
    bool flat = opts.ContainsKey("--flat");
    if (!flat && !opts.ContainsKey("--workpiece"))
        throw new ArgumentException("Workpiece-scoped import requires --workpiece GUID; use --flat for a demo folder.");
    var item = new WorkpieceIdentity(
        opts.TryGetValue("--workpiece", out var id) ? Guid.Parse(id) : Guid.NewGuid(),
        Get("--barcode", "OFFLINE_IMAGE_REPLAY"),
        long.Parse(Get("--epoch", "1"), System.Globalization.CultureInfo.InvariantCulture),
        Get("--release", "SIM_ONLY_IMAGE_LAB"), Guid.NewGuid(), DateTimeOffset.UtcNow);
    item.Validate();
    var rule = new ContourDefectRule(
        byte.Parse(Get("--threshold", "127"), System.Globalization.CultureInfo.InvariantCulture),
        double.Parse(Get("--min-area", "5"), System.Globalization.CultureInfo.InvariantCulture));
    rule.Validate();
    IReadOnlyDictionary<string, ImageRegion>? rois = null;
    if (opts.TryGetValue("--roi-json", out var roiJson))
        rois = JsonSerializer.Deserialize<Dictionary<string, ImageRegion>>(File.ReadAllText(roiJson),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("ROI JSON must be an object keyed by channel ID.");
    Directory.CreateDirectory(outRoot);
    var ledger = new VisionAssetLedger(Path.Combine(outRoot, "vision"));
    var replay = new OfflineVisionReplay(ledger);
    var roles = role switch
    {
        "master" => new[] { MachineRole.Master },
        "slave" => new[] { MachineRole.Slave },
        _ => new[] { MachineRole.Master, MachineRole.Slave }
    };
    var records = new List<ReplayChannel>();
    foreach (var node in roles)
        records.AddRange(replay.CaptureOwned(item, node, imageRoot, Path.Combine(outRoot, "overlays"),
            rule, rois is null ? null : rois.Where(p => ChannelCatalog.Require(p.Key).OwnerRole == node)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            flatDemoFolder: flat).Channels);
    var channels = records.OrderBy(r => r.ChannelId, StringComparer.Ordinal).ToArray();
    File.WriteAllText(Path.Combine(outRoot, "results.json"), JsonSerializer.Serialize(new
    {
        item.WorkpieceId, item.Epoch, item.ReleaseId, item.TraceId,
        IsSimulation = true, SourceDirectory = imageRoot, Channels = channels
    }, new JsonSerializerOptions { WriteIndented = true }));
    WriteHtml(outRoot, item, channels, ledger);
    foreach (var c in channels)
        Console.WriteLine($"{c.ChannelId} {c.Outcome} {c.Reason} " +
            $"frame={(c.Observation?.FrameId.ToString("N") ?? "NONE")} " +
            $"overlay={(c.OverlayPath ?? "NONE")}");
    Console.WriteLine($"AOI_VISION_REPLAY workpiece={item.WorkpieceId} total={channels.Length} " +
        $"captured={channels.Count(x => x.Observation is not null)} " +
        $"good={channels.Count(x => x.Outcome == ChannelOutcome.Ok)} " +
        $"ng={channels.Count(x => x.Outcome == ChannelOutcome.Ng)} " +
        $"missing={channels.Count(x => x.Outcome == ChannelOutcome.Missing)} " +
        $"error={channels.Count(x => x.Outcome == ChannelOutcome.Error)} " +
        $"report={Path.Combine(outRoot, "report.html")}");
    return channels.Any(c => c.Outcome is ChannelOutcome.Error or ChannelOutcome.Missing or ChannelOutcome.Uncertain)
        ? 1 : 0;
}

static void WriteHtml(string root, WorkpieceIdentity item, IEnumerable<ReplayChannel> records,
    VisionAssetLedger ledger)
{
    static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string Rel(string root, string path) =>
        string.Join('/', Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
            .Select(Uri.EscapeDataString));
    var sb = new StringBuilder("""
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
        <title>AOI · SIM_ONLY 图像检测回放</title>
        <style>
        body{background:#0b1422;color:#e8f0fa;font:15px system-ui;margin:0 auto;padding:30px;max-width:1500px}
        h1{margin:0 0 6px}.info{color:#99aec7;margin-bottom:22px}
        section{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:18px}
        article{background:#172439;border:1px solid #30455d;border-radius:14px;padding:18px}
        article h2{margin:0 0 12px;font-size:19px}.good{color:#62ddaa}.ng{color:#ff7777}
        .missing{color:#ffc470}figure{margin:10px 0}img{max-width:100%;height:auto;border:1px solid #3b4c63}
        small{color:#aabbd2}code{overflow-wrap:anywhere}
        </style></head><body><h1>AOI · 图像回放 / Contour</h1>
        """);
    sb.Append($"<p class=info>SIM_ONLY · WorkpieceId: <code>{item.WorkpieceId}</code> · " +
        $"Release: {Esc(item.ReleaseId)} · Epoch: {item.Epoch}</p><section>");
    foreach (var c in records)
    {
        string css = c.Outcome switch
        {
            ChannelOutcome.Ok => "good", ChannelOutcome.Ng => "ng", _ => "missing"
        };
        sb.Append($"<article><h2>{Esc(c.ChannelId)} · <span class={css}>{c.Outcome}</span></h2>" +
            $"<p>{Esc(c.Reason)}</p>");
        if (c.Observation is { } proof)
        {
            var asset = ledger.GetAsset(proof.FrameId)!;
            var original = Path.Combine(root, "vision", asset.RelativePath);
            sb.Append($"<figure><figcaption>原始像素 / {asset.Width} × {asset.Height}</figcaption>" +
                $"<img alt=原图 src=\"{Esc(Rel(root, original))}\"></figure>");
            if (c.OverlayPath is not null)
                sb.Append($"<figure><figcaption>ROI + 缺陷框 / {c.Defects.Count} 处</figcaption>" +
                    $"<img alt=检测框 src=\"{Esc(Rel(root, c.OverlayPath))}\"></figure>");
            sb.Append($"<small>FrameId {proof.FrameId} · Parent {proof.ParentFrameId} " +
                $"· SHA256 {Esc(proof.SourceFrameSha256)}</small>");
        }
        sb.Append("</article>");
    }
    sb.Append("</section><p class=info>纯离线检测结果，不构成工业现场质量放行凭据。</p></body></html>");
    File.WriteAllText(Path.Combine(root, "report.html"), sb.ToString());
}
