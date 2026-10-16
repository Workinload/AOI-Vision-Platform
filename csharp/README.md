# AOI Vision Platform — C# / .NET 10 / Avalonia (A0 first integration slice)

**Development branch:** `migration/csharp-avalonia-20261016` (base). This `csharp/` subtree is a new C# implementation, not a C++ rewrite. The historic C++ source at repository root is read-only reference.

## What is actually implemented

- Independently launched OS processes: `AOI.Host vplc`, `AOI.Host slave`, `AOI.Host master`, `AOI.Ui.Avalonia --master`, `AOI.Ui.Avalonia --slave`.
- vPLC: real loopback `127.0.0.1:19600` FINS/UDP DM word read/write framing (restricted **simulator subset**, not production driver).
- Master/Slave: real loopback TCP PeerLink `127.0.0.1:19610` with versioned JSON envelope, SHA256, sender/message identity, independent SQLite WAL, persisted Inbox/Outbox and ACK-after-commit.
- Workpiece identity separate from Barcode; Epoch/ReleaseId/TraceId and frame/capture lineage; explicit fail-closed missing/mismatched result handling.
- True OpenCvSharp5/OpenCV5 pixel processing of two **synthetic** 96×96 images; generated lossless PNG assets for `CH02` (Slave) and `CH06` (Master). This does NOT validate real defect accuracy or the full asset recovery policy.
- Master finalizes only the explicitly partial `SIM_LIGHT_TEST_RELEASE` `[CH02,CH06]`; the barcode check is an echo of the *same simulated fixture*, not a real end-station scan. Virtual D1016/D2099 receive the simulation-only result.
- UI: Avalonia 12.1.4 + CommunityToolkit.Mvvm, 11 mapped status tiles with real read-only IPC (`127.0.0.1:19621/19622`). Unimplemented channel captures are honestly shown as pending; no synthetic frames are shown as camera images.
- 8 NUnit tests; five independent-process automated SIM_LIGHT integration harness.

## Important NOT IMPLEMENTED / NOT VERIFIED

This is **not** a complete AOI production build. Not yet implemented: all 9 camera SDK adapters and 11 real frame streams; entire PLC register/handshake protocol and mechanical digital twin; full 8200×15000 shared line-scan parent-frame processing; full workpiece/station concurrence and recovery; production-grade durable PLC outbound transactions; independent end barcode scanner; production model precision; complete assets/crash recovery; operational solution release/dual-machine switch; detailed UI, alerts, ROI and diagnostics; Windows x64 native execution or field acceptance.

Real PLC access is **impossible** from this initial host because all FINS client endpoints are hard-wired to `127.0.0.1:19600`. Never replace with a production address without an independently verified adapter, field gates and explicit authorization. TCP PeerLink and IPC are loopback-only in this slice (no production authentication/TLS).

## Restore, build, test, run — offline

Requirements: official .NET SDK 10.0.401 for Linux; offline verified NuGet packages (user `/工具开发/nug`, includes 227 packages), including native Linux OpenCvSharp5. The NuGet feed MUST be available locally as directory of `.nupkg` files. No package binaries are committed here.

```bash
cd csharp
# In this container a PLATFORM=linux/amd64 environment variable can wrongly override MSBuild platform:
unset PLATFORM
export NUGET_CLI_CERT_REVOCATION_MODE=offline
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Replace these paths with your actual machine's .NET executable + feed path:
DOTNET=/path/to/dotnet
FEED=/path/to/offline-nupkg-directory
"$DOTNET" restore AOI.slnx --source "$FEED" -p:NuGetAudit=false
"$DOTNET" build AOI.slnx --no-restore -c Debug
"$DOTNET" test tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug
python3 scripts/e2e_sim_light.py --dotnet "$DOTNET"
```

For a graphical run on a desktop, after `build` start three terminals with `AOI.Host.dll vplc|slave|master --data /same/path` and then two more processes with `AOI.Ui.Avalonia.dll --master` and `--slave` (without `--headless --once`). UI has no direct dependency on Core or hardware SDK.

## Ownership of this slice

- `AOI.Domain`, `AOI.Contracts`, Solution, centralized NuGet: **A0 controlled**.
- `AOI.Runtime` and `AOI.Host` here contain **temporary end-to-end integration implementations** (storage, sim PLC, peer) that the specialist A1/A2/A3 modules can replace in small PRs without breaking the public DTOs.
- `AOI.Ui.Client` is intentionally read-only; `AOI.Ui.Avalonia` is an initial working shell for A4 to enhance.
- The next milestone is to maintain all five actual processes while gradually replacing the simulated 2-channel fixture with full vPLC handshake/station state/9-camera-11-channel flows.

Read `docs/AGENT_BOUNDARIES.md` before parallel PRs. Legacy v2.0 design and 45-signal registry remain the source of industrial facts; later C# technology decisions override old Qt/C++ implementation prescriptions.
