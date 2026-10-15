# C01 Review-only schema variant proposal v0.2

**NOT FROZEN / NOT MERGE_READY / SIM_ONLY**

This is an A0 offline candidate prepared while the private GitHub connector was unavailable. It is NOT a new PR commit and does not supersede currently reviewed PR #29. Do not merge until the exact A10 review of W002/W003, cross-owner acceptance and W004 prerequisites are verified.

Prior architectural basis: M00-r003 official, Git-first v1.2, A0 C01 Draft PR #29 as previously observed at `d3e73167d903181ebb7fe010cbd74561ff61b0d0`. This SHA is NOT claimed to still be the latest remote SHA.

## Changes proposed after six consumer reviews

- Transport common envelope no longer requires every alarm, authorization or deployment event to forge WorkpieceId, ChannelId and CaptureId. A strict oneOf event kind switch handles WORKPIECE_RESULT, ALARM_TRANSITION, DEPLOYMENT, ASSET, UI_SNAPSHOT and AUTH_AUDIT. Workpiece IDs, AttemptNo, epoch, channel and release are mandatory only for workpiece result payloads.
- Non-success processing can never have quality OK; encoded declaratively in JSON Schema, not only an imperative Python validator.
- Alarm ACK and CLEAR require distinct receipts and clear evidence; disconnected UI never claims live state.
- PREPARED_BOTH structurally requires two Core readbacks and simulated PLC parameters receipt, with runtime-only cross-field hash/Epoch checks explicitly separated.
- AVAILABLE asset requires normalized relative URI, encoded and pixel hashes and byte size. A robust server must still verify bytes and storage root confinement; URI/hash fields alone are not evidence.
- Demo SQLite in-memory uniqueness enforces exactly one physical parent per host/capture group/device/attempt and demonstrates one parent -> two line-scan channel refs; WAL-like rollback/outbox example is MOCK only.

## Intentionally unresolved before C01 freeze

- Correct ProductSolution immutable release schemas and station CAS; authoritative persisted deployment readback receipts, signature/trust model.
- Final PeerLink persisted ACK and crash-safe Outbox/Inbox semantics.
- Actual SQLite WAL vs in-memory transaction, image fsync/atomic rename, two-node crash recovery and 123 MB lossless decoded-pixel hash.
- Full ROI TransformChain and DetectionROI vs DisplayROI semantics.
- Strong C++20 DTOs, evolution policy, device FrameRef lifecycle and FINS secure WritePolicy.
- Real Qt GUI, Windows native, all 114 TC/52 scenarios, actual field acceptance.

## Test commands

`python3 -W error contracts/C01/test_c01_v0_2_review.py`

Actual local execution on prepared candidate: **29 tests PASS, exit 0**, see `evidence/C01_V0_2_A0_TEST.log`. These are schema/static/mock tests, NOT W004 acceptance or real PLC/production tests.

Prioritization: this is a candidate for PR #29 once the GitHub connector recovers; A0 must reconcile newer Agent05 feedback and submit a new exact SHA for A10 independent review. All 14 field gates remain pending and production writing remains blocked.
