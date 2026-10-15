# C01 — AOI public contract freeze candidate (Issue #8)

**DRAFT_FOR_CROSS_OWNER_REVIEW / C01 NOT_FROZEN / W004 NOT_VERIFIED**. This contract proposal is non-executable specification only. W004 formally depends on W003 VERIFIED; currently NOT_MET. No public binary compatibility claims and no WBS status changes.

## Canonical ID decision proposal

Use original PLAN v1.1 section 4.2 numbering C-01..C-12 verbatim, not the conflicting C00-DRAFT C-07..C-12 meanings. The machine-readable `contracts/C01/CONTRACT_REGISTRY_DRAFT.csv` is an A0 proposal, **not authority for dependent feature merges**. C00 remains readable as a historical draft only. Reviewers A1..A9 must provide consumer-impact and migration matrices; A10 checks each contract with positive/negative executable schemas before actual freeze.

## Required common identity and outcomes (proposed)

- Stable event correlation tuple: `WorkpieceId + Epoch + ReleaseId + ChannelId + CaptureId + AttemptNo`; **barcode never substitutes for WorkpieceId**. Keep host, physical device, parent CaptureGroup, station, monotonic sequence, source and TraceId explicit.
- Processing state `PENDING / RUNNING / SUCCEEDED / ERROR / CANCELLED / TIMEOUT` and quality state `UNDECIDED / OK / NG / MISSING / SKIPPED_BY_VERIFIED_CONDITION` are **independent axes**, matching original PLAN; no silent default OK.
- Inter-host envelope must carry schema version, deterministic message id, payload hash, persisted ack semantics, monotonic duration/time-quality and crash-safe outbox-inbox transaction. Unknown major version, stale Epoch, foreign Release or repeated MessageId with different hash is a reject, not an ACK of a successful business result.
- A physical frame parent is immutable. 9 capture devices feed 11 channels, with CH08/09 sharing one physical capture parent and CH10/11 sharing another. Capture/recapture identity must include the proper AttemptNo and immutable capture group; UI screenshot crop must not hide a detected NG.
- SIM_ONLY default deny for production PLC address, unknown endpoints, edit commands and unpublished solution deployment. `PREPARED_BOTH` requires both Core/device readbacks and simulated PLC parameter readback; mismatch/partial confirmation forces `FAILED_SAFE`. 14 physical field gates remain unsigned.

## DB and asset integrity (Issue #23; proposed decision, not migration)

1. A6 should define transactional `RAW_FRAME` uniqueness for a physical (HostId, CaptureGroupId, PhysicalDeviceId, AttemptNo) tuple; the attempt/key model must be checked against actual camera trigger semantics. Distinct derived CH08/CH09, CH10/CH11 never duplicate parent raw asset storage.
2. `AVAILABLE` must require nonempty, normalized, root-confined `storage_uri`, valid SHA256, persisted bytes and decoded pixel hash; filename alone is not proof. Use a versioned state transition `PENDING -> WRITING -> AVAILABLE` and recovery to `CORRUPT` or retry on missing/checksum mismatch.
3. A6/A5 must define events and outbox changes committed in one WAL transaction and a recovery ledger; no lost image acknowledgement or repeated aggregate stats after crash or ACK replay. Fix original v2 draft holes via new versioned migration only, **never edit original read-only SQL**.

## Hard freeze conditions and dependency unlock

- Precondition: W003 **VERIFIED** with independent A10 evidence; Agent06 CR-A0-004 PR #12 governance HOLD resolved and separately reviewed; A0/A1/A2/A3/A4/A5/A6/A7/A8/A9 agree cross-role impacts, A10 independently runs exact-SHA positive/negative contract suite.
- W004 only after A0 records an explicit C01 version, schema+DTO+compatibility hash and test evidence in ledger. This document must not be used as current adapter interface.
- Until then, Agent01..06 may use isolated mocks in own paths, **no feature PR merge against guessed C01 wire/DB schema**. W005/W007 and Windows native remain separate prerequisites.

## Review checklist

A0: canonical registry and unresolved ambiguity ledger; A1/A5: epoch/workpiece/ACK; A2/A3: safe FINS and FrameRef; A4/A6: ROI, RAW_FRAME/AVAILABLE/outbox; A7: two-node deployment/rollback; A8/A9: read-only snapshots/deny-by-default RBAC/alarm; A10: strict fail-closed mutation and independent integration tests. Every reviewer must give concrete compatibility/rejection scenarios and exact tracked comments, not generic PASS.
