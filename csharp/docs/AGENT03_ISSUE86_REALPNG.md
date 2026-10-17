# Agent03 · #86 · Real PNG replay / contour ROI / interleaved line scan

**Functional source:** `src/AOI.Runtime/Vision/OfflineVisionReplay.cs` extends A3-owned
`VisionAssetLedger.cs`. Optional, directly usable offline runner:
`tools/AOI.Vision.Cli`. The C# solution, Domain/Contracts, existing Host, and UI
contracts are unchanged. Real cameras and PLC remain disconnected.

## Operator run (Linux/Windows .NET 10)

Place **9 actual PNG files** in one folder (7 area: `CH01.png`–`CH07.png`,
2 physical line parents: `LINE-S-8.png`, `LINE-M-2.png`). Each line PNG must
be 8-bit grayscale with alternating scan rows belonging to the two logical
channels. Normal camera RGB PNGs are accepted on area channels (8-bit mono,
BGR or BGRA). Empty/missing/corrupt images remain Missing/Error, never Good.

```bash
cd csharp
dotnet run --project tools/AOI.Vision.Cli/AOI.Vision.Cli.csproj -- \
  --images /path/to/local-pngs --out /path/to/vision-results \
  --role all --flat --threshold 127 --min-area 5
```

Double-click **`/path/to/vision-results/report.html`**: 11 channel cards show
original imported pixels, green ROI and red defect bounding boxes, Good/Ng/
Missing/Error and parent/frame SHA lineage. `results.json` contains exact
WorkpieceId/TraceId/channel/result IDs and numeric boxes; `vision/` holds
immutable PNG originals in SQLite WAL; `overlays/` holds annotated PNGs.
The CLI exits `0` when every requested channel was actually inspected and
`1` when one or more is Missing/Error/Uncertain; a genuine NG still exits `0`
while clearly marked as NG (inspection completed, product rejected).

**Workpiece-safe batch input:** omit `--flat`, pass `--workpiece <GUID>`, and
place this item's files under `--images/<GUID-without-dashes>/`. Barcode is
never the primary key. `--flat` is an explicit single-sample offline lab
mode; it may reuse the same files across independent WorkpieceIds.

Optional ROI per channel:

```json
{"CH06":{"X":30,"Y":10,"Width":40,"Height":40}}
```

Save as `roi.json`, add `--roi-json roi.json`. Boxes are in **original source
pixel coordinates**. Moving ROI over/away from a white defect on black
background changes CH06 Good ↔ Ng with the same contour rule.

## A0 exact integration — no public DTO edits needed for first slice

Existing `csharp/src/AOI.Host/SimElevenChannelFixture.cs` method
`CaptureOwned(WorkpieceIdentity item, MachineRole role, ProductionStore store,
string nodeFolder, SimProductRuntime? product)` is the single changeover point
for A0: replace its `SimVisionFixture.CaptureOwnedChannels(...)` branch **only
when an explicitly configured offline image root is present** (for example,
`AOI_SIM_IMAGE_ROOT`). Keep synthetic fallback when no source is configured.

```csharp
var ledger = new VisionAssetLedger(Path.Combine(nodeFolder, "vision"));
var batch = new AOI.Runtime.Vision.OfflineVisionReplay(ledger).CaptureOwned(
    item, role, imageRoot, Path.Combine(nodeFolder, "vision-overlays"),
    new AOI.Runtime.Vision.ContourDefectRule(127, 5),
    roiByChannel: activePublishedRois,
    flatDemoFolder: false);
foreach (var result in batch.Channels.Where(c => c.Observation is not null))
{
    // Existing ProductionStore.SaveLocalResult + slave PeerEnvelope outbox path.
    // Each result.Observation! has normal A0 ChannelResult lineage.
}
// If batch.Missing > 0 or any Error, do not finalize/eject as Good.
```

On Slave and Master, preserve **existing** `ProductionStore.SaveLocalResult`
and durable PeerLink outbox/ACK logic. `FramePreviewIpc` already reads and
verifies the original frames from `nodeFolder/vision`; the new overlay PNG
path in each `ReplayChannel.OverlayPath` is a separate render resource for A0
to expose via bounded read-only IPC. No UI or PLC code added in this PR.

Line-scan mapping from `ChannelCatalog` is: `LINE-S-8` parent → CH08 (0,2,4,…
rows) and CH09 (1,3,5,… rows); `LINE-M-2` parent → CH10/CH11 in the same
order. A 9×20 parent produces 5×20 and 4×20, without interpolating or
cropping left/right. The full parent is unchanged; new `VisionAsset.PixelTransform`
records the transformation for byte-exact readback/audit alongside the
existing crop-based parent format. Existing `CaptureLinePair` remains supported.

## Verification

- `dotnet build AOI.slnx --no-restore -c Debug` (with `PLATFORM` unset).
- `dotnet test tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug`.
- `dotnet build tools/AOI.Vision.Cli/AOI.Vision.Cli.csproj -c Debug`.
- Actual CLI positive: disk PNG input set `CH06.png` with a white 18×24
  region, 10 Good / 1 Ng, persisted 11 channels, red CH06 bounding box,
  `report.html` and `results.json` opened as local resources.
- Negative: file absent → explicit Missing with no persisted result;
  invalid PNG magic → Error; ROI away from defect Good, ROI enclosing it Ng.

This is a **SIM_ONLY deterministic contour detector**, not a defect-trained
model, field-calibrated defect threshold, hardware camera adapter, or
certified quality-control decision. Full 123MB throughput, OpenVINO, production
ROI release binding, machine-side overlay IPC and real Windows UI integration
are subsequent independent tasks, not prerequisites for usable offline replay.
