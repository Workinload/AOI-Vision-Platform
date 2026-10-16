# Agent05 / Issue #60 - executable SIM_ONLY quality gate

Source baseline: A0 PR #64, commit `5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8`.
No changes to A0-controlled Domain, Contracts, shared NuGet, solution, Runtime, Host or old C++ main.

## Run

From `csharp/` with .NET SDK 10, matching pinned NuGet dependencies, Python 3.10+:

```sh
dotnet restore AOI.slnx --source /path/to/verified/offline-feed -p:NuGetAudit=false
dotnet build AOI.slnx --no-restore -c Debug
dotnet test tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug
python3 scripts/e2e_sim_light.py --evidence-root .artifacts/qa-normal
python3 qa/integrity_audit.py --evidence-root .artifacts/qa-normal --scenario normal
python3 qa/chaos_e2e.py --evidence-root .artifacts/qa-chaos
```

The GitHub workflow attempts official NuGet source for matching **exact centrally pinned** versions. If a private/offline-only package cannot be resolved, CI must **fail**. A0 should configure a verified package source/secrets or offline runner with audited SHA256 for required packages. Never replace OpenCvSharp5 with another package to make CI green.

## Independent QA assertions

- Two independent SQLite WAL ledgers, with integrity_check OK, matching WorkpieceId, TraceId, Epoch, ReleaseId and explicitly recorded Barcode (not an identity key).
- Master CH02 + CH06 and Slave CH02: actual persisted records, matching frame SHA256 to owner-side PNG bytes, same peer CH02 data on both sides; no missing/extra channel is claimed complete in this narrow A0 fixture.
- Both PeerLink directions: hashed payload, durable inbox event, matching sender outbox and PERSISTED_ACK event/timestamp. Slave cannot finalize.
- Happy path: GOOD is marked `isSimulation=true` with `SIM_ONLY` proof and exactly CH02+CH06; stock A0 E2E script checks FINS and both UI snapshot processes.
- Injected fault: an **independent local-only FINS/UDP vPLC** changes DM2000 simulated barcode from `SIM-0001` to `SIM-9999` *after* the first loading scan and *before* the end-station echo read. All five OS processes must execute, Master must HOLD without writing a final, D1016 and D2099 stay zero, and both UI IPC snapshots complete. No real hardware IP configurable.
- Six negative-path NUnit cases against actual Core contracts: corrupt hash, wrong receiver, wrong ACK hash, duplicate message collision, conflicting frame attempt and barcode mismatch.

## Verification and limitations

Locally observed: Python syntax compilation for local A5 files passed; **8 A5 independent Python mutation tests PASS** against a separately constructed fixture (available as companion test bundle in conversation). A5 current container has **no dotnet executable and no network access to install SDK or NuGet**; thus `dotnet restore/build/test`, Windows native execution, actual five process E2E and this PR's GitHub CI are **NOT_VERIFIED** until CI runs. These must not be misrepresented by Python successes. In addition, the GitHub copy of the auditor was abbreviated from the richer locally exercised verifier; independent review of its assertions is required.

Scope is a first cut of #60, not sign-off for 114 TC, 52 scenarios, 10 UIs, 123 MB frame pressure, untested Windows desktop UI rendering, machine crashes/partitions, AI-only log blind diagnosis, camera/PLC physical verification or production safety FG. All remaining NOT_VERIFIED. A0/integration reviewer must independently validate the QA tests and generated evidence: Agent05 cannot self-approve.

REQ/WBS/TC trace pointers: #54/#60, W005 W007 W009 W011 / W104-W122 (partial samples only). No full original TC is declared closed. Release gates all remain closed. Rebase this small PR onto `migration/csharp-avalonia-20261016` only after A0 PR #64 merges there; never merge onto historical C++ main.
