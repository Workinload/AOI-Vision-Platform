# Agent01 / Issue #56 — Core identity routing and SIM_ONLY crash recovery

## Implementation (based on A0 PR #64 head 5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8)

- `AOI.Runtime.Agent01.WorkpieceFlow`: deterministic per-WorkpieceId/Epoch/Release/TraceId assessment; required channel completeness, capture lineage, non-independent end scan explicitly simulation only; HOLD rather than identity remap.
- `ProductionStore.FindWorkpiece`: query exact workpiece primary key, never take current/latest barcode as identity.
- `WorkpiecesMissingChannel` and `SimOnlySlaveRecovery`: restart and duplicate-message reconciliation for the narrowly whitelisted `SIM_LIGHT_TEST_RELEASE` / `CH02` synthetic fixture. SQLite Inbox is persisted before ACK, synthetic result + outbox are persisted in one transaction.
- `OutboxRetryPlanner`: bounded exponential retransmit pacing; no message marked delivered until actual stored PERSISTED_ACK.
- `PeerSession`: integrates retry planning without modifying A0 wire DTO.
- `AOI.Host`: runs Slave recovery, handles duplicate registrations safely, routes Master results by exact WorkpieceId; refuses stale-item writes to the single shared simulated eject register.
- `Agent01CoreTests`: six NUnit tests including same barcode/different WorkpieceIds, scan/frame misbinding, ACK-before-processing crash window and bounded retransmits.

## Build/test reproduction

```bash
# Requires .NET SDK 10.0.401 plus the verified offline NuGet feed and native OpenCvSharp5
cd csharp
export NUGET_CLI_CERT_REVOCATION_MODE=offline
unset PLATFORM
dotnet restore AOI.slnx --source /path/to/offline-feed -p:NuGetAudit=false
dotnet build AOI.slnx --no-restore -c Debug
dotnet test tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug
python3 scripts/e2e_sim_light.py --dotnet dotnet
```

**Current Agent01 execution evidence**: `dotnet restore` **NOT_VERIFIED**, `dotnet build` **NOT_VERIFIED**, `dotnet test` **NOT_VERIFIED**, `e2e_sim_light.py` **NOT_VERIFIED**. Execution container has no .NET SDK/NuGet feed, with unavailable external download DNS. These are new source and test commits, **not** claimed green CI. The 8 original tests and A0 five-process run were verified *by A0* on PR #64 and cannot stand in for verification of this branch.

## Issues for A0 integration, no public contract edits

1. The existing `AOI.Host` demo only supports a single global vPLC release/eject state, not concurrent physically keyed release effects. A1 safely holds stale workpieces until A2 and A0 supply a queue/acknowledged `WorkpieceId`-bound release actuator with transactional effect recovery.
2. Slave recovery is presently restricted to one **synthetic** channel/release. Generalized camera task inbox processing requires A2/A3 capture orchestration and message handler idempotency.
3. Final state and vPLC write are separate actions; loss of connection after final persistence can leave unconfirmed physical effects. Durable idempotent PLC effect journal, confirmation and restart replay must precede any production use.
4. `EndScanEvidence.SimulatedFixtureEcho` is not a separate barcode scan, even if matching. Formal end-scan command/result DTO and verification rules belong to A0.
5. Master startup does not yet reconcile every persisted result and pending final. Full multi-item scheduler, station capacities, epochs and releases depend on A6/A2 contracts.
6. TCP PeerLink and IPC are loopback-only without field authentication or TLS. REAL PLC writes remain disabled.

REQ/TC: `#54` WorkpieceId identity separation / PeerLink persisted-ack / no mismatched result attachment; `#56` A1 Core state, replay after crash and limited concurrency; source tests are a **partial** verification mapping. Original 114 TC / 52 scenarios remain **NOT_VERIFIED**.

**Do not merge to C++ main. Do not enable REAL hardware writes.**
