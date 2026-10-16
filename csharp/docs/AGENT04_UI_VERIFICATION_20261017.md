# Agent04 A4 Avalonia Monitoring — 2026-10-17

Owner: Issue #59; A0 source baseline: PR #64, head `5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8`.
This branch changes only A4-owned `AOI.Ui.Avalonia`, `AOI.Ui.Client`, and isolated tests/docs. It does not modify A0 DTOs, legacy C++ or REAL device access.

## Real code delivered
- Eleven logical channels mapped to nine real *defined* physical device IDs; an additional read-only machine-state summary panel ("11+1"). No claim of live camera integration.
- Linked selection between graphical equipment/channel cards and PLC/channel address list, preserving existing A0 ChannelCatalog addresses.
- Selection details contain station/device, owner, PLC address, and line-scan group. D1013 etc. remain descriptions, not authorization for writes.
- Validated read-only IPC status projection: role/node mismatch, stale snapshot, wrong topology/WorkpieceId, missing channel identifiers, non-SIM modes, REAL write flags and production decisions rejected. Missing channels remain NO_DATA rather than passing.
- IPC progress liveness indication; if no accepted snapshot for 5 seconds, visible state transitions to IPC_OFFLINE and all channel cards lose their last-known-success appearance.
- ROI normalized x/y/width/height validator: **local ephemeral draft only**; no storage/write/downstream effect.
- New independent NUnit test project `tests/AOI.Ui.Client.Tests` exercises topology fill-in, fail-closed snapshot safety, workpiece mismatch and ROI bounds. Deliberately not editing A0-owned `AOI.slnx`.

## Verification — transparent scope
The available execution container has no `dotnet` executable or offline NuGet feed; `dotnet --info` returned command not found. Network DNS for github.com is also unavailable in container. Therefore **NOT_VERIFIED** for:
- `dotnet restore csharp/tests/AOI.Ui.Client.Tests/AOI.Ui.Client.Tests.csproj` — NOT_RUN
- `dotnet build csharp/src/AOI.Ui.Avalonia/AOI.Ui.Avalonia.csproj -c Debug` — NOT_RUN
- `dotnet test csharp/tests/AOI.Ui.Client.Tests/AOI.Ui.Client.Tests.csproj` — NOT_RUN
- `python3 csharp/scripts/e2e_sim_light.py --dotnet dotnet` — NOT_RUN
- Native Linux desktop UI, Windows x64 desktop UI — NOT_VERIFIED

This is real C# and Avalonia AXAML source committed to GitHub, **not** a claim that this branch is verified to compile. A0's 8 passing tests and five-process success belong to PR #64 only and are not recycled as Agent04 results.

## A0 integration requests
1. Decide whether to include isolated A4 NUnit test project in canonical `AOI.slnx` (A0 owns solution).
2. Add versioned read-only IPC telemetry for all PLC 45 signals (explicit address, current value, source node, direction, readback/UNKNOWN, UTC, write policy); the current A0 v0.1 contract has only per-channel PLC address via ChannelCatalog, not actual PLC register status.
3. Add immutable machine-geometry / stations/links / motion / ROI metadata (plus drift and digital twin), read-only history/stats/alarms endpoints with pagination and filtering. No GUI may infer these from static fixture.
4. Add strictly authenticated and permissioned Core command contracts for later ROI/solution editing only after A0's safety gates and audit; this PR intentionally provides no write path.

## Coverage identifiers
- Issue #59 A4: 11+1 monitoring, device map selection, PLC mapping table linkage, ROI local draft.
- #54 SIM_ONLY, 9→11 truth, UNKNOWN/fail-closed, hardware write prohibition and IPC isolation.
- Historical original 114 TC / 10 UI / FG matrix mapping remains dependent on A0 evidence catalog; not represented as passed.

## Manual smoke procedure (once SDK and offline NuGet are available)
```sh
dotnet restore csharp/AOI.slnx --source /path/to/offline/feed -p:NuGetAudit=false
dotnet build csharp/src/AOI.Ui.Avalonia/AOI.Ui.Avalonia.csproj --no-restore -c Debug
dotnet test csharp/tests/AOI.Ui.Client.Tests/AOI.Ui.Client.Tests.csproj -c Debug
python3 csharp/scripts/e2e_sim_light.py --dotnet dotnet
```
Observe linked selection in the equipment map and PLC list; disconnect Master Core to verify IPC_OFFLINE after 5 seconds; enter x=0.9 width=0.2 to verify invalid ROI. Do not connect real hardware.
