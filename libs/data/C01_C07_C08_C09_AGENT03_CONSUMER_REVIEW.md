# Agent03 C-07 / C-08 / C-09 consumer compatibility review

Status: **CONSUMER_HOLD / REVIEW_REQUIRED / C01_NOT_FROZEN / SIM_ONLY**. Base main `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`. A0 PR #29 head `1dc75f37b28b76e1f028799521897f1ab686d9f7`. Related #23 and #25.

## Compatibility

| ID | PR #29 proposed owner/scope | Consumer decision |
|---|---|---|
| C-07 | A6 IAssetStore; metadata, lossless codec | **Number/owner compatible** with original PLAN v1.1. Freeze HOLD until parent identity, state transitions and actual on-disk-byte/decoded-pixel hash checks are executable |
| C-08 | A6 DB migrations, durable Outbox/Inbox/queries | **Number/owner compatible**. Freeze HOLD until A5 ACK/Inbox boundary, two-host WAL, atomic effects, repeated message behavior and crash recovery semantics are defined |
| C-09 | A4 algorithm/model/ROI TransformChain | **Number/owner compatible**. Freeze HOLD until DetectionROI vs DisplayROI vs SensorROI coordinate and processing-status mappings are defined |

C00 used incompatible meanings for C-07..C-09; PR #29 restores PLAN numbering but remains **DRAFT**, not migration/wire ABI authority.

## Verified issues for A0 to resolve

1. **P0** PR #29 envelope Schema permits `Epoch=0`, `AttemptNo=0`, whereas locked v2 SQL checks `production_epochs.epoch>=1` and `acquisitions.attempt_no>=1`. Specify a production numeric domain and sentinel behavior.
2. **P0** JSON Schema accepts `ProcessingStatus=ERROR, QualityStatus=OK`; the accompanying A0 Python validator rejects it. They are inconsistent validators for a safety-critical status pair. Schema also accepts `SUCCEEDED+MISSING`. Define a normative cross-field rule, fail closed.
3. **P0** A0 prose requires immutable parent reference/Host/Device/CaptureGroup/TraceId/monotonic ordering, but exact envelope has no `CaptureGroupId`, `OriginHostId`, `PhysicalDeviceId`, `TraceId`, `SequenceNo` fields and `additionalProperties:false`. Decide typed companion FrameRef/AssetRef vs versioned envelope extension.
4. **P0** Original immutable SQLite draft permits duplicate `RAW_FRAME` for one physical capture and `AVAILABLE` without URI/hash. PR #29 proposes parent uniqueness `(HostId,CaptureGroupId,PhysicalDeviceId,AttemptNo)`; Agent03 agrees **conditionally** pending A3 frame/recapture semantics. PR #25 oracle lacks AttemptNo; it cannot certify the amended key. Derived CH08+CH09 must reference one rear raw parent, CH10+CH11 one front raw parent, and each new physical attempt may use its own unique parent.
5. **P0** A 64-character SHA metadata field does not prove byte existence, hex validity, lossless codec decode or pixel integrity. Require normalized root-confined URI, real file readback, file SHA256 and actual decoded pixel SHA256 with dimensions/stride/pixel format, atomic staging/fsync/rename and `PENDING->WRITING->AVAILABLE` recovery. An interruption after rename but before SQLite WAL commit leaves a recoverable orphan; no false AVAILABLE. Missing/corrupt mandatory original must HOLD, never OK.
6. **P0** C-08 receiver's `PERSISTED_ACK` must follow InboxDedup + business effect durable commit, with idempotent retransmission and MessageId/hash collision rejection. WAL cannot atomically commit external image bytes; define explicit asset intent+reconciliation plus event/outbox commit ordering.
7. **P1** sqlite3 online backup covers WAL-committed DB rows, not external images; require image manifest, checksums and restore protocol. A genuine production failure test remains pending.
8. **P1** Proposed C01 processing enum `PENDING/RUNNING/SUCCEEDED/ERROR/CANCELLED/TIMEOUT` differs from locked v2 SQL `EXECUTED/ERROR/MISSING/SKIPPED_VERIFIED/CANCELED`; freeze reject-on-unknown versioned adapter.
9. **P1** SensorROI affects capture A3/readback; DetectionROI affects A4 algorithm quality; DisplayROI is A8 read-only visualization projection. Display crop **must not** remove offscreen NG/defect count. Specify split/mirror/rotate/affine inverse, coordinates, pixel-center and ROI edge semantics; fixtures assume half-open integer rectangles only.
10. **P1** MockInference must carry `SIMULATED`, with failures/missing inputs never OK; cannot establish battery defect accuracy or claim OpenCV5/OpenVINO verification.

## Actual evidence and limits

- **PR #25 exact HEAD** `5e83fc63d484ecb55d7a554575c66b93be580e80`: verified Git blob hashes `5e30fbd6b094b79cf7f3ee1955174562a37c127b` and `60711f225ae3fd456d80fa7d0195c415814da57b`. Exact source rerun 7 storage and 4 vision tests, exit 0 each. Evidence log SHA256 `b92a81a42ea89fef0617cba6adbd8cd5712030054a73507ed4b104cb4147be47`.
- **PR #29 exact schema/registry/test blobs** `f15170a0461bad8c23ce4d2a987f9fdc6ee63558`, `6f60ecc77707a793efad0ca6a024cc3bfcfeb6fd`, `02a22aaf3b216357e8b0a85ac1612b114f35e1cf`. A0 draft selftest 8 tests exit 0. Schema probe exit 0; demonstrates divergent ERROR/OK handling, accepted 0 numeric fields and omitted correlation fields. Evidence log SHA256 `011f79497cf3f87a02d85a21a115ecd3884632434e5572f449a7df27f1f5b585`.
- **New self-contained stdlib fixtures**: `tests/unit/storage/test_c01_asset_recovery_fixture.py` (13 tests: separate WAL hosts, shared parent+recapture, real bytes/tamper/path traversal, injected abrupt child exit before/after COMMIT, replay/rollback/backup); `tests/unit/vision/test_c01_roi_consumer_fixture.py` (7 tests: DetectionROI/DisplayROI separation, negative coords, simulated Mock/status mismatch). Local 20/20 exit 0; file SHA256 storage `ad96638d346c2d8e2006ddb6e447b986abb37b4011b3fcc35f22e44fddec1778`, vision `2027cca20e029b97c5204b0ad48cb2beccee2949522ff461dc6dd78a50168bc8`; log SHA256 `29de5d5221f28eb270593fab62133afa173b3ffff24a3ee930be5ed07d18cb74`.
- **Executable proposed head recheck**: `python3 -B -m unittest discover -s tests/unit/storage -p 'test_c01_*.py' -v`; analogous vision; `python3 -B -m py_compile ...`; SHA256 source and Git blobs before release. Tests are fixtures only: they **do not** validate actual production migration, OpenCV decoded pixel equality, Qt, 123MB, network PeerLink, actual host restart, physical disk power fault, Windows/Field gates.
- A10 must independently rerun **this PR exact SHA**; local tests are Agent03 self-test, not independent QA.

## Dependencies / authorization

W018 depends W011,W012; W059 depends W018,W030; W060 depends W051,W059; W052 depends W050,W051; W062 depends W043,W061. None of these dependencies is VERIFIED in latest checked main ledger; W003 not VERIFIED, W004 C01 not frozen. **No integration or formal WBS acceptance.** Only Agent03-owned files changed, never reference SQL, C01 contract, A0 domain or ledger.

**A0 requested outcome:** record normative C-07 asset identity and lossless verification, C-08 WAL/outbox and ACK recovery, C-09 ROI coordinate/error mapping, testable DTO/schema version and negative contracts. **Consumer decision: PLAN numbering compatible; C01 freeze HOLD until P0s closed.**
