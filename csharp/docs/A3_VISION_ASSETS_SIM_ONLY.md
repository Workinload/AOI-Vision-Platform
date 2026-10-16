# Agent03 / Issue #58: OpenCvSharp5 image & asset ledger — SIM_ONLY

Base: A0 PR #64, head commit `5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8`.
Scope: `AOI.Runtime` bounded A3 extension and `AOI.Tests` tests, **no edits** to
`AOI.Domain`, `AOI.Contracts`, shared solution/central NuGet config, root C++ or A0 host.

## Actual behavior

`VisionAssetLedger` is a separate SQLite database in each caller-provided offline asset
root. It uses WAL, foreign keys and FULL synchronous commits. `CaptureArea` accepts 8-bit
mono/BGR/BGRA native OpenCvSharp5 `Mat` pixels for CH01–CH07. `CaptureLinePair` accepts
one 8-bit mono parent for a real two-channel topology group, and two explicitly configured
pixel ROIs, saves the full parent unchanged, and crops the two children without resize.
Groups are looked up in A0's `ChannelCatalog`, not inferred from channel numbering.

All images are encoded with **native OpenCV5 lossless PNG**. Image data is fsynced and
atomically renamed before the SQLite insert transaction commits. Each persisted result
links WorkpieceId, TraceId, Epoch, ReleaseId, CaptureId, AttemptNo, ChannelId, DeviceId,
FrameId, ParentFrameId, and the SHA256 of its **source full parent frame** for line scan.
The parent and two line children commit in one WAL transaction; partial transactions do
not publish channel results. Completed PNGs remaining after power loss/failed commit are
**orphan evidence**, not automatically accepted. `Audit()` reports pending, orphan,
missing, tampered, parent-misbound or pixel-mismatched assets. It never deletes evidence.
`LoadVerified()` checks file SHA, decoded pixel shape, parent identity/digest and real
child-versus-parent ROI bytes. `ResultsFor()` should only be used with verified assets.

`VisionPixelOps.Probe` performs actual grayscale conversion, OpenCV mean/stddev, binary
threshold and nonzero-pixel measurements. The optional `SimPixelRule` is an explicit
synthetic brightness rule (not a trained/validated defect model); omitted rule produces
`ChannelOutcome.Uncertain`, **not Good**. Even with a SIM rule, `IsSimulated=true` and
`QualityRules` yields `:SIM_ONLY`, never a production authorization.

`SimVisionFixture.CaptureAllMappedChannels` creates 9 real `Mat` buffers for the mapped
physical devices, 11 stored channel results, 2 preserved line-scan parent PNGs and 4
child PNGs, using only small deterministic **fictional** images. The actual field line
frame (8200x15000 / ~123 MB), field split ROIs, crop orientation and sensor SDK remain
unverified; the fake fixture geometry MUST NOT be used as production configuration.

## API integration example

```csharp
using AOI.Domain;
using AOI.Runtime;
using OpenCvSharp;

var item = WorkpieceIdentity.New("SIM-ONLY", 4, "A3_LAB_RELEASE");
var ledger = new VisionAssetLedger("./offline-lab/master-assets");
using var areaPixels = new Mat(128, 128, MatType.CV_8UC1, Scalar.Black);
var uncertain = ledger.CaptureArea(item, "CH06", areaPixels); // Uncertain, SIM_ONLY

// Configuration MUST supply actual parent-pixel ROIs; no default production split.
using var parent = new Mat(32, 64, MatType.CV_8UC1, Scalar.Black);
var children = ledger.CaptureLinePair(item, "LINE-M-2", parent,
    new ImageRegion(0, 0, 32, 32), new ImageRegion(32, 0, 32, 32));
var integrity = ledger.Audit();
```

## Reproducible offline verification

```bash
cd csharp
unset PLATFORM
export NUGET_CLI_CERT_REVOCATION_MODE=offline
export DOTNET_CLI_TELEMETRY_OPTOUT=1
/path/dotnet restore AOI.slnx --source /path/offline-feed -p:NuGetAudit=false
/path/dotnet build AOI.slnx -c Debug --no-restore
/path/dotnet test tests/AOI.Tests/AOI.Tests.csproj -c Debug --no-build
# optional native five-process A0 baseline, unchanged:
python3 scripts/e2e_sim_light.py --dotnet /path/dotnet
```

## REQ/WBS/TC traceability (implementation slice, not closure of full requirement)

| Parent requirement | Evidence / boundaries |
| --- | --- |
| #54 physical topology 7 area + 2 line = 9 devices, 11 channels | `SimVisionFixture` + `NineDevicesElevenChannels...` NUnit; no actual SDK |
| #58 OpenCvSharp5 native pixels, ROI and line split | `VisionPixelOps`, `CaptureLinePair`, ROI byte-exact test |
| #58 durable parent, SHA, workpiece tracing and WAL | `VisionAssetLedger`, corruption/orphan/attempt/WAL tests |
| #58 history/statistics and Lab test | `ResultsFor`, `Stats`, `Audit`, simulated 11-channel NUnit fixture |
| #54 fail-closed result quality and REAL safety | no rule => Uncertain/Hold; rule => explicit `IsSimulated`/SIM_ONLY |
| #54 123MB full-size parent, field ROI, true camera | **NOT_VERIFIED**, requires A2 acquisition adapters and field fixtures |
| #58 optional OpenVINO inference & A/B lab | **NOT_IMPLEMENTED** in this bounded slice; no fake-model OK |

## A0-owned integration decisions requested

1. A0 to standardize a versioned frame acquisition/capture request interface including
   camera timestamp, monotonic sequence, pixel format/stride, hardware device serial,
   station, parent ROI manifest and attempt; no A3 modifications to public DTOs.
2. A0/A1/A2 to decide transaction boundary linking Core `ProductionStore` with A3 asset
   ledger and ACK-after-commit semantics. Separate WAL stores cannot create a single
   cross-process atomic transaction; A3 returns results **only after its asset commit**.
3. A0 to decide authoritative validated ROI/recipe release schema; fixture split values
   are never production defaults. Hardware write FG remains CLOSED.
4. A0 to decide memory/performance policy for huge line parents (streaming codec,
   bounded queues, 123MB test environment); no field/performance claim here.
