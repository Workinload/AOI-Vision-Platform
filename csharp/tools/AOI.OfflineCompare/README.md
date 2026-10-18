# AOI.OfflineCompare — OFFLINE_ONLY / NOT_PRODUCTION

Read-only A/B re-inspection of **one immutable PNG set** against **two already
persisted A6 ReleaseIds**. It answers the engineering question "did this published
recipe change actually change the pixel result?" without a PLC, camera, Core WAL
or production write.

## Safety boundary

- The A6 database must be a **stable offline/checkpointed snapshot**. A live
  `-wal` or `-shm` next to it is rejected with `SOURCE_WAL_PRESENT`: copying a WAL
  database as separate files is not one certified transaction. The tool never
  deletes or rewrites WAL/SHM; export a checkpointed copy instead.
- The source database itself is never opened. A byte copy is taken into
  `<out>/_db-snapshot/`, its SHA256 is compared with the pre-copy hash (a mismatch
  means the source was written during the copy and the run fails), and only that
  copy is opened with `SqliteOpenMode.ReadOnly`.
- The report folder must be new, and must not be inside the image fixture, inside
  the A6 database's run folder, or behind a `..`/symlink alias that resolves there.
- Machine names and absolute paths are **redacted by default** so a report can be
  committed as public evidence; `--include-absolute-paths` is for local-only runs.
- Emits no PLC/FINS traffic, no `CamDone`, no production statistics, no field
  acceptance and no second recipe database.
- An unknown plugin, missing/corrupt PNG, bad ROI or native OpenCV failure is
  reported per channel as `UNSUPPORTED` / `MISSING` / `ERROR`; it is never rendered as `OK`.

## Usage

```powershell
# 1. deterministic offline fixture (refuses to overwrite)
dotnet run --project csharp/tools/AOI.OfflineCompare -- --make-fixture-images <new-image-dir>
dotnet run --project csharp/tools/AOI.OfflineCompare -- --scaffold-fixture-db  <new.sqlite>

# 2. compare the SAME images through two persisted releases
dotnet run --project csharp/tools/AOI.OfflineCompare -- --compare `
  --db <solutions.sqlite> --images <image-dir> --out <new-report-dir> `
  --release-a SIMREL-... --release-b SIMREL-... [--epoch 1] [--line-mapping demo|legacy] `
  [--include-absolute-paths]

# one command that builds fixtures and compares them
dotnet run --project csharp/tools/AOI.OfflineCompare -- --all-in-one <new-scratch-dir>
```

`--all-in-one` writes `<root>/run/fixture-solutions.sqlite`, `<root>/fixtures/images`
and `<root>/reports/ab`, so the database run folder, the image fixture and the
report are three separate trees by construction.

Fixture input names: `CH01.png` .. `CH07.png` (7 area devices) and
`LINE-S-8.png`, `LINE-M-2.png` (2 shared line-scan parents; one parent feeds
two logical channels). Point `--images` at a folder of real product PNGs to
compare field images instead.

## Outputs (all beneath `--out`)

| File | Content |
|---|---|
| `ab-manifest.json` | Releases + recomputed SHA256, station/channel counts, per-channel A/B status, ROI, parameters, defects, input hashes |
| `channel-results.csv` | Flat A/B table for spreadsheet diffing |
| `summary.txt` | Human-readable channel table |
| `inputs.sha256.txt` | SHA256 of the database and every input PNG |
| `A/<device>/<channel>.png`, `B/<device>/<channel>.png` | Annotated result images (green ROI, red defect boxes) |

## Status values

| Status | Meaning |
|---|---|
| `OK` / `NG` | Real pixel result of that release's published ROI and algorithm parameters |
| `ERROR` | Decode, ROI, plan or native OpenCV failure; never a silent pass |
| `MISSING` | The input PNG for that physical device is absent |
| `POSITIONING` | CH01: a positioning prerequisite, explicitly **not** an inspection verdict |
| `UNSUPPORTED` | The release names an algorithm this build cannot execute |

## Test coverage vs CI

- `csharp/tests/AOI.Tests/AOI.Tests.csproj` references this project, and that test
  project is part of `AOI.slnx`. The existing `sim-e2e` workflow
  (`dotnet build AOI.slnx` + `dotnet test AOI.slnx``) therefore compiles and runs
  `OfflineReleaseAbCompareTests` on **ubuntu-24.04 and windows-2025**.
- The executable CLI (`--all-in-one`, `--compare`) is **not** invoked by any
  workflow. CI green proves the NUnit slice, not an end-to-end CLI run; that
  distinction must stay explicit in any claim about this tool.
- The project is intentionally not added to `AOI.slnx` as its own entry; it is
  reached through the test project.

## Acceptance mapping (A0 decision, 2026-10-18)

| ID | Test |
|---|---|
| TC-154-AB-01 | `SameImmutablePngs_TwoPersistedReleases_ChangeOnlyRoiAndThresholdChannels` — same bytes, two releases, ROI/threshold change, 7 stations / 11 channels / 9 devices, redacted paths |
| TC-154-AB-02 | `LineScanParentFeedsTwoLogicalChannelsWithExplicitRowMapping` |
| TC-154-AB-03 | `PositioningChannelIsNeverReportedAsInspectionOk` |
| TC-154-AB-04 | `MissingCorruptPngAndUnsupportedPluginNeverBecomeOk` |
| TC-154-AB-05 | `ReportRootNeverReusedOrInsideImageTreeOrDatabaseRunRoot` |
| TC-154-AB-06 | `UnknownReleaseIdenticalReleasesAndLiveWalAreRejected` |
| TC-154-AB-07 | `TamperedReleaseDigestInDatabaseIsRejected` |

## Reuse, not reinvention

The tool calls the existing A6/A3 code: `SolutionRepository` (read-only release
lookup and immutable digest verification), `PublishedParameterSnapshot` /
`PublishedRuntimeParameterResolver`, `OpenCvInspectionProcessor`,
`ContourDefectDetector`, `VisionPixelOps` and `ChannelCatalog`.

One small duplication is deliberate and A0-approved for this offline CLI: the
legacy brightness fraction path is mirrored here because the A0 implementation in
`AOI.Host/PublishedReleaseInspector.cs` is `internal`. A shared single implementation is
an A0 integration follow-up, not a change made here.

## Recorded run (reviewable from the cloud)

`ab-evidence/run-windows-20261018/` holds one real Windows run: six command
logs (fixtures, database, compare, NUnit, `dotnet build AOI.slnx`, `dotnet test AOI.slnx`),
the before/after SHA256 of the database and all nine input PNGs, the full
`ab-manifest.json`, the CSV/summary, and the CH06 A/B annotated pair that shows the
published ROI change.

It is redacted: the manifest records file/folder names only, and the logs are
sanitized (`<REPO>`, `<HOME>`, `<MACHINE>`) before commit. The run was executed from
a scratch tree where the database run folder, the image fixture and the report
were three separate directories, so the report never entered the source run folder.

Regenerate it at any time with

```powershell
dotnet run --project csharp/tools/AOI.OfflineCompare -- --all-in-one <new-scratch-dir>
```

Both ReleaseIds are deterministic (the digest covers only the canonical release
manifest), so an independent run must reproduce exactly the same pair.

## Explicitly NOT verified

- Contour/brightness rules are SIM_ONLY examples, **not** a field-certified
  defect classifier; detection accuracy is `NOT_VERIFIED`.
- Line-scan row parity follows an explicit `--line-mapping` choice; the physical
  light-path / orientation relation remains `FIELD_PENDING` (FG-07 / FG-09).
- Fixture PNGs are deterministic offline images, not field product photos.
- Not a five-process Host publish/activation proof and not a `SIM_READY` claim.
