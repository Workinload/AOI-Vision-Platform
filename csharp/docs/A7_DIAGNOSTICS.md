# A7 SIM_ONLY Flight Recorder / replay (Issue #62)

Built against A0 PR #64 exact head `5c9d6f3b0bdd76c0b36c55e82da4596a450fceb8`. A7 owns only new `AOI.Diagnostics`, `AOI.Diagnostics.Cli` and `AOI.Diagnostics.Tests` subprojects. A0-owned public DTOs, `AOI.slnx`, central NuGet and old C++ main have NOT been changed.

## What is implemented
- SQLite **read-only** capture of separate master/slave `production.db` WALs (workpieces, channel results, inbox/outbox and existing flight events). Never writes to Core DB; if absent marks missing node rather than inventing OK.
- Append-only newline JSON flight journal with SHA-256 state envelope, sequence, previous-entry digest, full-entry digest, strict replay rejection on malformed/tampered lines.
- Deterministic offline analysis for cross-node identity mismatches, wrong station/device/channel linkage, frame/capture reuse, per-attempt duplication, unbacked GOOD, unexpected simulation, unacknowledged aged outbox, ACK without receiver persisted inbox, sender/receiver hash differences and stale unfinished workpieces.
- Human-readable findings, bundle ZIP with SHA256 manifest and re-verification, stepwise restricted AI triage process. Diagnostic code has **no network client, PLC actuator or production write**.
- Raw event details and barcode replaced with redaction markers. Pseudonymous UUID, release and source hashes remain potentially sensitive; transport/store accordingly.

## Run after A0 dependencies are restored (not included in base solution file)
```bash
cd csharp
dotnet restore src/AOI.Diagnostics.Cli/AOI.Diagnostics.Cli.csproj
dotnet build src/AOI.Diagnostics.Cli/AOI.Diagnostics.Cli.csproj --no-restore
dotnet test tests/AOI.Diagnostics.Tests/AOI.Diagnostics.Tests.csproj
dotnet run --project src/AOI.Diagnostics.Cli -- capture /tmp/aoi-sim /tmp/aoi-flight.jsonl
dotnet run --project src/AOI.Diagnostics.Cli -- watch /tmp/aoi-sim /tmp/aoi-flight.jsonl 5
dotnet run --project src/AOI.Diagnostics.Cli -- replay /tmp/aoi-flight.jsonl
dotnet run --project src/AOI.Diagnostics.Cli -- bundle /tmp/aoi-flight.jsonl /tmp/aoi-diagnostics.zip
dotnet run --project src/AOI.Diagnostics.Cli -- verify /tmp/aoi-diagnostics.zip
```

`replay` exit code 3 means ERROR/CRITICAL finding; 0 means no such finding, not proof of REAL safety. `capture/watch/bundle/verify` success 0, error 1. `watch` polls immutable snapshots at requested interval; stop with Ctrl-C. This is a diagnostic sidecar, not yet integrated into A0 Core hot path, and is not a replacement for atomic in-transaction recording.

## Verification status and known limitations
- Runtime available to this agent: GitHub connector and local container; `dotnet --info` returned `dotnet: command not found`, exit 127. Direct git clone failed due to DNS (`Could not resolve host: github.com`). **restore/build/test/e2e are NOT_VERIFIED** (no fabricated green badge).
- Six authored NUnit tests are source only, not run. A0/QA must execute on .NET SDK 10 with the approved offline NuGet feed; do not merge without passing evidence.
- Existing flight event text is redacted; A0's current event schema lacks TraceId, CaptureId, FrameId, ChannelId and signed monotonic sequence. Full transactional capture would require A0 to add a versioned event sink hook at Core persistence boundaries.
- Sidecar WAL snapshot queries span multiple SQLite read transactions; high concurrency can yield a temporarily inconsistent composite snapshot. Detection is advisory and cannot adjudicate a physical defect without frame proof, actual scanner, or device evidence.
- Journal hash chain detects edits but does not protect against coordinated re-signing or deletion of whole journal; use signed manifests, protected retention, independent storage and bounded rotation before production.
- ZIP verifier checks included hashes/chain, not an external signature. No automatic AI code modification or deployment. REAL hard gates remain closed.

## A0 contract request
Provide a versioned, nonblocking, crash-safe Core event sink or transactional outbox observer carrying the existing identity keys (`TraceId, WorkpieceId, Epoch, ReleaseId, CaptureId, FrameId, ParentFrameId, ChannelId, AttemptNo, MessageId`) plus monotonic time, causal links and node role. A7 will adapt without directly mutating `AOI.Domain`/`AOI.Contracts` or `AOI.Runtime`. A0 should add projects to `AOI.slnx` during integration. QA (A5/A10) independently reviews acceptance mappings and physical safety.

Scope: #62 and #54. WBS/RQ/TC: silent fault diagnosis / replay / evidence mapping pending A0 formal updated C# task IDs (legacy v1.1 A7 mapped to solution ownership and is superseded by #62).
