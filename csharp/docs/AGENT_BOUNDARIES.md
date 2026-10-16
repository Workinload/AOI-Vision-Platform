# Multi-Chat integration contract v0.1 (A0-owned)

Canonical branch: `migration/csharp-avalonia-20261016` (after A0 PR is merged). Each child chat must start from this current C# branch, not main, and use a small Draft PR. Do not independently modify `AOI.Domain`, `AOI.Contracts`, `Directory.Packages.props`, or `AOI.slnx` without A0's integration review.

## Existing minimal DTOs (real source; reference rather than re-create)

- `AOI.Domain.WorkpieceIdentity` — WorkpieceId (Guid), Barcode (string, not ID), Epoch, ReleaseId, TraceId, CreatedAtUtc.
- `AOI.Domain.ChannelDefinition` and `ChannelCatalog.All` — truthful 11 channels / 9 physical device mappings from v2.0 CSV.
- `AOI.Domain.ChannelResult` — result/workpiece/epoch/release/trace/channel/station/device/capture/frame/parentFrame/attempt/source SHA256/quality/sim flag.
- `AOI.Domain.QualityRules.Evaluate(...)` — explicit expected-channel set; missing or wrong identity holds; CH01 positioning excluded from inspection list.
- `AOI.Contracts.PeerEnvelope` — protocol v1, MessageId, sender/receiver, WorkpieceId/Epoch/Trace/Release, payload type/hash; `PersistedAck` returned only after receiver commits.
- `AOI.Contracts.MachineSnapshot` and `ChannelSnapshot` — versioned read-only Core→UI status contract; `AOI.Ui.Client.SnapshotClient` loopback client.

These are v0.1 **integration contracts**, not a claim that all original schemas are frozen forever. When extending, add compatible fields/version and tests; preserve identity/invariants.

## Direct child task ownership

| Child | Issue | Code ownership / next useful PR |
|---|---|---|
| A1 | #56 | Core state machine, multi-item concurrency, PeerLink reconnect/dead-letter, persistent PLC effect; use A0 DTOs |
| A2 | #57 | Full offline FINS/UDP vPLC register/handshakes, virtual mechanics/cameras; keep REAL writes unimplemented/disabled |
| A3 | #58 | Native OpenCvSharp5 9→11 capture/line-scan processing, WAL/assets/statistics, true frame ownership |
| A4 | #59 | Full Avalonia MVVM UI on top of `AOI.Ui.Client` and status/command DTOs; never link hardware layer |
| A5 | #60 | Independent test/CI harness, five real processes, failure injection, Windows native checks |
| A6 | #61 | Draft/immutable Release, staging/readback/dual prepared/epoch/rollback |
| A7 | #62 | Flight Recorder, liveness, misbinding proofs, diagnosable silent logic errors, replay |

Current A0 demo covers only two synthetic channels. Extending it must never silently mark unimplemented channels OK, infer unknown PLC addresses, or enable real production writes. When multiple children change runtime code, use bounded adapters and narrow PRs; A0 resolves shared contracts and integration.

Each PR should include source, compile/test commands and observed exit codes, a known limitations list, and a specific Git SHA. No auto-merge to historical C++ main.
