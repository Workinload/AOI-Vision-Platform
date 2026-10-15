# C01 consumer gate matrix / A0 decision log (2026-10-16)

**STATUS: REVIEW_ONLY / NOT_FROZEN / NOT_MERGE_READY.**
Original PLAN v1.1 C-01..C-12 numbering is canonical; C00 draft C-07..C-12 collisions require an explicit migration adapter. No WBS or official RELEASE is promoted by this document.

## Reviewed inputs, exact source references

- Agent01 A1/A5 C-01/C-04/C-05/C-06: [PR #31](https://github.com/Workinload/AOI-Vision-Platform/pull/31), original C01 review pinned to 1dc75f37b28b76e1f028799521897f1ab686d9f7.
- Agent02 A2/A3 C-02/C-03: evidence `/AOI开发成果/AOI_Agent02_PR27_PR29_C02_C03_REVIEW_EVIDENCE_20261016.zip`. No physical PLC/real camera tests accepted.
- Agent03 A4/A6 C-07/C-08/C-09: [PR #34](https://github.com/Workinload/AOI-Vision-Platform/pull/34), byte/parent/WAL/ROI analysis.
- Agent04 A8/A9 C-11/C-12: [PR #32](https://github.com/Workinload/AOI-Vision-Platform/pull/32), cursor/offline/default-deny.
- Agent06 A7 C-10: [PR #33](https://github.com/Workinload/AOI-Vision-Platform/pull/33), two-node deploy and rollback.
- Agent05 A10 latest passed only prior C01 draft static/JSON Schema, exact new HEAD requires separate review. W002 static facts A0 VERIFIED; W003 is NOT VERIFIED.

## Per-contract remaining MUST-CLOSE items

| Original PLAN ID | Owner | Blocking decisions / evidence | Proposed next A0 artifact |
| --- | --- | --- | --- |
| C-01 | A0 | issuer/host-scoped Strong IDs; uint bounds for Epoch/Attempt, per-event nullable context, canonical PayloadHash, EventId/TraceId/SequenceNo/time quality, schema negotiation | versioned domain IDs, envelope and error DTO, canonical serialization vectors |
| C-02 | A3 | FrameRef owner/lifetime, SDK buffer reuse, 9 physical capture units for 11 channels, CH08/09 and CH10/11 shared parent and attempt tracking, config ReadBack | IAreaCapture/ILineCapture contracts and negative buffer reuse/device readback |
| C-03 | A2 | typed SignalRegistry/WritePolicy, FINS WORD/BA/BADC vectors and readback; D1000/D1030/D1013/D2000 unknown; real writes always denied while SIM_ONLY | default-deny command contract with simulated-only ingress and trace |
| C-04 | A1 | barcode is not WorkpieceId, Epoch/slot/attempt fences, CH01 positional prerequisite, stale/foreign frame quarantine | WorkpieceTracker/Acquisition coordinator DTO and sparse/late negative tests |
| C-05 | A1 | master-only authoritative final, separate ProcessingStatus vs QualityStatus, SUCCEEDED+MISSING policy, no CAM_DONE->OK, 10 defect channels completeness | station/aggregate status pair table and missing/timeout/NG tests |
| C-06 | A5 | PeerEnvelope event variants, persisted-ACK tied to (MessageId,PayloadHash,Host), Inbox+business effect WAL atomic commit, retry/dead-letter/resume/idempotence | versioned peer ACK/NACK/REPLAY/FINAL DTO, crash-replay test suite |
| C-07 | A6 | RAW_FRAME parent unique (Host/CaptureGroup/PhysicalDevice/Attempt), derived child refs, actual URI confinement, file bytes+encoded+decoded pixel hash, fsync+rename recovery | AssetIntent+AssetState transitions and on-disk corruption tests |
| C-08 | A6 | per-host SQLite WAL migration, Outbox/Inbox same durable effect transaction; file bytes outside WAL reconciled; repeats cannot double-count | versioned migration/asset intent plan and two-host crash/rollback tests |
| C-09 | A4 | SensorROI vs DetectionROI vs DisplayROI, coordinate transforms/split row parity, rejected missing model/real algorithm, simulated inference marker | TransformChain DTO and pixel/ROI out-of-view NG negative fixtures |
| C-10 | A7 | immutable SolutionRelease separate from Draft, per-station CAS + BaseReleaseId, model/machine/calibration/device hashes; 2 Core+both vPLC readback receipts; prepare/commit/epoch-fenced rollback | Release/Deployment phase model and typed receipt/cert/error contracts |
| C-11 | A8 | initial authoritative snapshot then generation/cursor/sequence/digest TTL; Core disconnect => NOT_LIVE; EntityRegistry shared with device map/table | UIRead snapshot/delta/expired-cursor DTO and replay tests |
| C-12 | A9 | backend reject unknown/expired/revoked/physical PLC write, alarm ACK != CLEAR, crash-durable audit, staleness/time-quality semantics | AuthAudit/AlarmEnvelope/Telemetry DTO and negative Core-side permissions |

## A0 v0.2 already mitigates (review only, not production)

- `C01_EVENT_VARIANTS_REVIEW_V0_2.schema.json` provides a strict event-kind discriminator for WORKPIECE_RESULT, ALARM_TRANSITION, DEPLOYMENT, ASSET, UI_SNAPSHOT, AUTH_AUDIT; unlike the original draft, unrelated events no longer need fake WorkpieceId/ChannelId/CaptureId.
- JSON Schema itself rejects non-success PROCESSING status + QualityStatus OK. Epoch and physical attempt start at 1 on relevant variants; no automatic fabricated OK.
- A10 has independently verified the prior draft's safety, but v0.2 only has A0 *local* `python3 -W error contracts/C01/test_c01_v0_2_review.py` exit 0, 29 tests, exact Git blob match. These are isolated JSON Schema and SQLite in-memory Mock tests; they are NOT a durable integrated system.
- PREPARED_BOTH requires structural receipts, yet **actual** independent readback hashes and peer/PLC identity are enforced only in the example runtime oracle, not by real physical hardware. JSON fields cannot establish actual bytes or root confinement without filesystem readback.

## Nonnegotiable gate sequence

1. **W002 VERIFIED static only**, original 14 FIELD gates unchanged (7 BLOCKED / 7 OPEN).
2. W003: verify the exact **latest PR #28** SHA with A10 two-round independent QA, graph/ownership/route and A0 ledger decision. W003 cannot be marked VERIFIED from local logs.
3. Merge/accept cross-consumer contract decisions only after public DTO/schema ownership and compatibility questions are resolved. Publish C01 C++20 contract headers, evolution/errors, canonical hash, negative test vectors and rollback/ACK semantics.
4. A10 independently execute positive and negative contract tests at the final exact PR SHA, and A0 decide W004 VERIFIED. **Until then C01 NOT_FROZEN**, downstream agents only safe owner-scoped mock/Draft PR.
5. W005 114 RQ/TC static trace, W007 Windows Qt Quick native and CI fail-closed SDK are separate HOLD items; original 114 functional TC and 52 scenarios are NOT_EXECUTED. No REAL PLC/camera and no production write under SIM_ONLY.

**No original v2/PLAN SQL, locked manifest, main task ledger, device runtime or real FIELD gates were modified by this review.**
