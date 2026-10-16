# Agent06 implementation slice — Issue #61

Branch: `agent06/csharp-solutions-dual-changeover-20261017`; based on A0 PR #64 head `5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8`.

## Actual new source
- `src/AOI.Solutions/`: independent net10.0 library, references A0's unchanged Domain and Contracts, consumes existing centrally pinned Microsoft.Data.Sqlite.
- MachineProfile pins exactly 9 physical devices/11 logical channels as per A0 ChannelCatalog. No guessed PLC addresses.
- ProductSolution with station drafts; SQLite WAL, optimistic revision compare, operator audit.
- Content addressed immutable PublishedRelease; publish requires complete station/channel coverage. Changes to drafts cannot mutate released manifests.
- Independent persistent master/slave SIM nodes, stage hash/read-back, PREPARED_BOTH certificate, explicit safe interlocks, durable COMMIT_INTENT before epoch activation, safe hold after activation.
- Recovery does not infer a successful switch after power loss. Activation is held for manual/A0 integration reconciliation; blind rollback of activated nodes is rejected.
- No live PLC or camera API, no REAL address, no uncontrolled machine enable, no field-gate unlock.

## Validate (NOT_VERIFIED in current agent container: dotnet not installed, no restored offline NuGet)
```sh
dotnet restore csharp/tests/AOI.Solutions.Tests/AOI.Solutions.Tests.csproj --source /path/to/verified/offline-nupkg -p:NuGetAudit=false
dotnet build csharp/tests/AOI.Solutions.Tests/AOI.Solutions.Tests.csproj --no-restore -c Debug
dotnet test csharp/tests/AOI.Solutions.Tests/AOI.Solutions.Tests.csproj --no-build -c Debug
```
New independent NUnit project is deliberately NOT in the A0-owned `AOI.slnx`; A0 must opt in after review. Neither compilation nor tests may be claimed green in this environment.

## Blockers / integration contracts A0 must own
1. Production machine is NOT switched by A6 store alone. A0 needs Core quiesce, durable two-core control messages and post-crash reconciliation, actual PLC/camera read-back, explicit release of safety hold only after field policy and both nodes confirm.
2. Existing `PeerSession/ProductionStore` handles workpiece and result messages only; do not misuse `PersistedAck` as prepared confirmation. Need separate versioned `changeover_prepare/readback/commit` envelope types, retriable inbox/outbox, identity and trust validation.
3. Core needs to prove zero in-flight workpieces, output hold state, equipment readiness and fencing / monotonic epoch from durable authoritative store before starting switch.
4. No production REAL authorization or D-register writes. Field gates and 45 signals remain unknown until validated.

## Test coverage mappings
- Issue #61: MachineProfile, ProductSolution, station optimistic lock, immutable Release, STAGING/read-back, PREPARED_BOTH, epoch+1, safe HOLD, abort before activation, restart hold.
- Parent #54: preserved 9-to-11 ChannelCatalog, SIM_ONLY and REAL write prohibition.
- Full original 114 TC / 52 scenarios, power-cut between all operations, synchronized two-process orchestration, Windows x64, PLC device read-back and actual equipment acceptance: **NOT_VERIFIED / NOT_IMPLEMENTED**.
