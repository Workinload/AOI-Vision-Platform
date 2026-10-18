# AOI.OfflineCompare — OFFLINE_ONLY / NOT_PRODUCTION

Read-only A/B re-inspection of **one immutable PNG set** against **two already
persisted A6 ReleaseIds**. It answers the engineering question "did this
published recipe change actually change the pixel result?" without a PLC,
camera, Core WAL or production write.

## Safety boundary

- Byte-copies the given `solutions.sqlite` (plus any `-wal`) into
  `<out>/_db-snapshot/`, verifies the copy SHA256, and opens only that copy with
  `SqliteOpenMode.ReadOnly`. The caller's database and its directory are never
  opened, so not even SQLite's transient `-shm`/`-wal` sidecars appear there.
- Never writes outside the caller's isolated output root; refuses a non-empty
  output root or one inside the image fixture.
- Re-hashes the database and every input PNG after the run and fails closed if
  any byte changed.
- Emits no PLC/FINS traffic, no `CamDone`, no production statistics, no field
  acceptance and no second recipe database.
- An unknown plugin, missing PNG, bad ROI or decode error is reported as
  `UNSUPPORTED` / `MISSING` / `ERROR`; it is never rendered as `OK`.

## Usage

```powershell
# 1. deterministic offline fixture (refuses to overwrite)
dotnet run --project csharp/tools/AOI.OfflineCompare -- --make-fixture-images <new-image-dir>
dotnet run --project csharp/tools/AOI.OfflineCompare -- --scaffold-fixture-db <new.sqlite>

# 2. compare the SAME images through two persisted releases
dotnet run --project csharp/tools/AOI.OfflineCompare -- --compare `
  --db <solutions.sqlite> --images <image-dir> --out <new-report-dir> `
  --release-a SIMREL-... --release-b SIMREL-... [--epoch 1] [--line-mapping demo|legacy]

# one command that builds fixtures and compares them
dotnet run --project csharp/tools/AOI.OfflineCompare -- --all-in-one <new-scratch-dir>
```

Fixture input names: `CH01.png` .. `CH07.png` (7 area devices) and
`LINE-S-8.png`, `LINE-M-2.png` (2 shared line-scan parents; one parent feeds
two logical channels). Point `--images` at a folder of real product PNGs to
compare field images instead.

The project is intentionally **not** in `AOI.slnx` (A0 owns that file); build it
explicitly with `dotnet build csharp/tools/AOI.OfflineCompare/AOI.OfflineCompare.csproj`.

## Outputs (all beneath `--out`)

| File | Content |
|---|---|
| `ab-manifest.json` | Releases + recomputed SHA256, per-channel A/B status, ROI, parameters, defects, input hashes |
| `channel-results.csv` | Flat A/B table for spreadsheet diffing |
| `summary.txt` | Human-readable channel table |
| `inputs.sha256.txt` | SHA256 of the database and every input PNG |
| `A/<device>/<channel>.png`, `B/<device>/<channel>.png` | Annotated result images (green ROI, red defect boxes) |

## Status values

| Status | Meaning |
|---|---|
| `OK` / `NG` | Real pixel result of that release's published ROI and algorithm parameters |
| `ERROR` | Decode, ROI or plan rejection; never a silent pass |
| `MISSING` | The input PNG for that physical device is absent |
| `POSITIONING` | CH01: a positioning prerequisite, explicitly **not** an inspection verdict |
| `UNSUPPORTED` | The release names an algorithm this build cannot execute |

## Reuse, not reinvention

The tool calls the existing A6/A3 code: `SolutionRepository` (read-only release
lookup and immutable digest verification), `PublishedParameterSnapshot` /
`PublishedRuntimeParameterResolver`, `OpenCvInspectionProcessor`,
`ContourDefectDetector`, `VisionPixelOps` and `ChannelCatalog`.

One small duplication is deliberate: the legacy brightness fraction path is
mirrored here because the A0 implementation in
`AOI.Host/PublishedReleaseInspector.cs` is `internal`. Promoting that inspector
into `AOI.Runtime.Vision` would remove the duplication.

## Explicitly NOT verified

- Contour/brightness rules are SIM_ONLY examples, **not** a field-certified
  defect classifier; detection accuracy is `NOT_VERIFIED`.
- Line-scan row parity follows an explicit `--line-mapping` choice; the physical
  light-path / orientation relation remains `FIELD_PENDING` (FG-07 / FG-09).
- Only the host OS and OpenCV build that actually ran are evidenced; every
  other platform is `NOT_VERIFIED`.
