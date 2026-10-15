# Agent06 / A7 consumer review: C-10 ProductSolution / Draft / immutable Release / Deployment

**Decision: HOLD_FOR_C10_DTO_AND_STATE_CONTRACT** | 2026-10-16 | `SIM_ONLY` | **REVIEW-ONLY, NOT C01 FROZEN**

- Official main reviewed: `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`.
- A0 contract Draft PR #29 **exact reviewed HEAD**: `1dc75f37b28b76e1f028799521897f1ab686d9f7`; C01 file Git blobs: `C01_FREEZE_CANDIDATE.md` `b5902e6e3a664aefcf0817f7323411269ab034eb`, registry `6f60ecc77707a793efad0ca6a024cc3bfcfeb6fd`, envelope schema `f15170a0461bad8c23ce4d2a987f9fdc6ee63558`, Python draft tests `02a22aaf3b216357e8b0a85ac1612b114f35e1cf`.
- A7 previous Draft PR #14 exact HEAD `26e1e78bf784ab0d9617ed235aa10371e8754327` remains independent, **not replicated**. Routing PR #12 at `39598da19fd43eecc839a6c13047a2b78b58f366` still Draft. CR-A0-004 authorizes A7 BranchDev only.
- Original immutable task card A7: W083 requires W012/W053/W025, W084 W083/W061, W085 W084/W053/W055, W086 W085/W044/W032, W087 W086/W037/W088. A0 `TASK_LEDGER.json` has no VERIFIED evidence for any of these prerequisites; W004 `DRAFT_NOT_FROZEN`, W003 `IMPLEMENTED_PENDING_A10_REVIEW`.

## 1. Compatible points and concrete contract gaps

**Compatible:** PR #29 assigns **canonical C-10 to A7**, retains C01/C00 numbering migration (C00's C-08 was A7, no longer authoritative), requires SIM_ONLY, forbids Draft activation and demands both Core/device + virtual PLC readback before PREPARED_BOTH. Its common correlation proposal correctly rejects stale epoch and process fault disguised as OK. This is a high-level direction, **not** a freeze-able C-10 schema.

**C10-B01 (BLOCK)**: registry row and prose do not define C-10 wire/IPC DTO fields, validators, API method signatures, storage/idempotency, version negotiation, errors or transition ownership. Add A0-owned versioned JSON schema + C++20 strong-ID DTO and positive/negative tests; do not treat this A7 oracle as a public adapter.

**C10-B02 (BLOCK)**: PR #29's *workpiece* `CORRELATION_ENVELOPE_DRAFT.schema.json` requires nonempty `WorkpieceId`, `ChannelId`, `CaptureId`, and `AttemptNo` on every instance and sets `additionalProperties=false`. A release deployment event is **not necessarily workpiece-scoped** and needs `DeploymentId`, `HostRole`, `ReleaseSha256`, `Epoch`, `TraceId`, `EventId` and provenance. Fake CH/Workpiece IDs are prohibited. A0 should define a discriminated envelope union for `WORKPIECE_EVENT` and `DEPLOYMENT_EVENT`, or explicit optional fields with strict per-type validation. Do not weaken the existing workpiece correlation invariant.

**C10-B03 (BLOCK)**: legacy v2 `product_solution.schema.json` is explicitly `PRODUCT_SOLUTION_DRAFT_NOT_DEPLOYABLE`, `ReleaseState=DRAFT`, `CanPublish=false`; `MachineProfileReference.ProfileSha256` may be null. Never mutate it into a deployable Release by toggling flags. Define a separate immutable `SolutionRelease` manifest/schema with required **non-null, byte-verified** MachineProfile, ModelArtifacts, Calibration, DeviceSettings, SignalRegistry version and complete Release manifest hashes. Content hash must cover canonical bytes, not merely check length.

**C10-B04 (BLOCK)**: v2 `Channels` constrains 11 entries but does not prove unique `ChannelId` nor published required DetectorROI; only `DisplayROI` exists. `ModelBindings`/`ModelArtifacts` are untyped arrays and SharedCaptureGroups cannot prove correct CH08/09, CH10/11 linkage. Add semantic validation for exactly 9 physical devices, 11 distinct channels, CH01 positioning only, 10 defect expected set CH02..11, two single-parent line groups, DetectionROI and StationRules, coordinate spaces/transform references, model/shape/class table and final validation report. Clarify disabled station/conditional skip rules with A1/A4.

**C10-B05 (BLOCK)**: `PatchStationDraft` needs exact station-level expected revision **plus BaseReleaseId**, draft identity, request id, conflict response; isolation must hold across different products sharing a model, separate stations, and Active snapshots. Whole-document revision may be retained for manifest, but must not silently overwrite concurrent station edits. Specify immutable old/active snapshot linkage, multi-editor merge policy and RBAC audit with A9.

**C10-B06 (BLOCK)**: ReleaseId uniqueness, immutable snapshot/canonical SHA, persistent atomic transaction, validation report identity, approval policy, duplicate request id with changed hash, repeat publish and revision pinning lack normative definitions. Stable content-addressed hashes must include product-level threshold/ROI even when shared model hash is identical.

**C10-B07 (BLOCK)**: need staged receipt semantics from **both independent Core roles**, each with actual readback hash, host identity, lease/generation and deadline. Device staged is not an active software switch and **must not mean PREPARED_BOTH**. Core disconnect/timeout must force `FAILED_SAFE`; Peer delivery must be durable through C-06/A5, and locally persisted C-08/A6.

**C10-B08 (BLOCK)**: vPLC dimensions/params require **both roles' deterministic simulated readback**, source signal registry version and exact `FLOAT32` 2-WORD encoding/order/readback quality for D3010..20 as determined by A2's C-03/W088. Prohibit guessed physical PLC addresses. Distinguish expected, applied and observed arrays, reject incomplete/NaN/stale readback, expired confirmations, duplicate MessageId differing hash; readback must be from simulated gateway rather than UI flags.

**C10-B09 (BLOCK)**: specify `PREPARED_BOTH` as complete valid quorum (MASTER Core+device, SLAVE Core+device, simulated PLC readback on both roles), not production READY; Commit must be explicit, idempotent, epoch-fenced, durable and followed by *post-commit active readback* before `ACTIVE_SIMULATED`. Workpieces already in flight retain original (WorkpieceId, Epoch, ReleaseId); reject stale/past attempts and never retroactively relabel them.

**C10-B10 (BLOCK)**: failure after partial camera/lighting/PLC apply needs a compensating rollback plan with old ReleaseId/hash, all device/config readbacks, both virtual PLC parameter readbacks and epoch reconciliation; missing/mismatched rollback readback remains `FAILED_SAFE/HOLD`. Software pointer rollback alone does not re-enable READY. Decide crash recovery, operator authorization, alert/ack semantics, and restart replay boundaries (A5/A6/A9).

**C10-B11 (BLOCK)**: define actor RBAC, dual approval / lease expiry, audit events persisted via C-08/C-12. Deployment events must contain `TraceId`, `DeploymentId`, `ReleaseId`, `Epoch`, `HostRole` (and WorkpieceId explicit nullable when not scoped); alarm ACK does not clear failed-safe fault.

**C10-B12 (GATE)**: C01 pending A0/W004 freeze and W003 VERIFIED, PR #12 exact-head independent A10 review, all W083-W087 WBS prerequisites VERIFIED, A10 exact PR HEAD retest and A0 merge decision; *all* FieldGates FG-01..FG-14 remain pending. This draft PR is strictly `NOT_INTEGRATION_READY`.

## 2. Proposed A0-owned DTO inventory (field suggestions, not ABI)

| DTO / API | Required candidate fields / rule | Producer / consumer |
|---|---|---|
| `MachineProfileRef` | `MachineProfileId`, `ProfileSha256`, `SignalRegistryVersion`, `TopologySha256`, per-host physical device mapping and calibration digests | A0/C-01, A2/C-03, A3/C-02, A7 |
| `ProductSolutionDraft` | `SchemaVersion`, `ProductId`, `DraftId`, `BaseReleaseId`, `DraftRevision`, `MachineProfileRef`, `Stations` (unique station+channel), `ModelBindings`, 9 `DeviceSettings`, Master/Slave dimensions, `DetectionROI`, `DisplayROI`, `StationRules`, validators | A7/A4/A3/A8 |
| `PatchStationDraftRequest/Result` | `ProductId`, `DraftId`, `StationId`, `ChannelId`, `ExpectedStationRevision`, `ExpectedBaseReleaseId`, immutable patch payload hash, `IdempotencyKey`, authorized principal; return applied revision, current revision on conflict and audit id | A7/A8/A9/A6 |
| `ValidateSolutionResult` | ordered failed invariant codes, checked SHA asset proofs, expected 11 channels/9 devices/2 line groups, ROI/rules coverage, `ValidationReportId`, canonical manifest digest; cannot publish on any fatal failure | A7/A4/A6 |
| `SolutionRelease` | `ReleaseId`, `ProductId`, `SourceDraftId/Revision`, immutable manifest SHA, MachineProfile+Model+Calibration+Device digests, per-channel ROI/threshold/rules, PLC parameters encoding/expected readbacks, approvals, schema version | A7/A4/A6/A9 |
| `DeploymentIntent/Status` | `DeploymentId`, current and target ReleaseId/ManifestSha, `ExpectedOldEpoch`, `ProposedEpoch`, `DeadlineMonotonic`, `TraceId`, `IdempotencyKey`, `SIM_ONLY`, in-flight workpiece fence and owner host | A7/A1/A5 |
| `CoreDeviceStagingReceipt` | `DeploymentId`, HostId/Role, Release SHA, TargetEpoch, actual device settings readback SHA, camera/lighting calibration proof, sequence/generation, status and observed/monotonic time | A7/A3/A5 |
| `VirtualPlcReadbackReceipt` | `DeploymentId`, HostRole, registry version, intended encoded 2-WORD values, actual decoded values/word bytes, readback count and source, TargetEpoch, pass/error; **vPLC only** | A7/A2/A5 |
| `PreparedBothCertificate` | validated pair of Core receipts + validated dual vPLC receipts, complete matching Release/Manifest/Epoch, unexpired lease and replay-safe certificate identity; does *not* grant READY | A7/A1/A5 |
| `CommitOutcome / RollbackOutcome` | prepare certificate, durable commit token, new epoch activation, two post-commit Core+vPLC readbacks, previous manifest/epoch re-apply, typed error/HOLD, audit trace and explicit manual recovery gate | A7/A1/A2/A5/A6/A9 |

## 3. Proposed durable events and state transitions (A0 to finalize)

Events: `SolutionDraftPatched`, `SolutionValidationFailed`, `SolutionReleaseFrozen`, `DeploymentStaged`, `DevicePrepareRequested`, `CoreDeviceReadbackObserved`, `VirtualPlcReadbackObserved`, `PreparedBothRecorded`, `CommitRequested`, `ActiveReleaseReadbackVerified`, `EpochActivated`, `DeploymentFailedSafe`, `RollbackRequested`, `RollbackReadbackVerified`, `OperatorHoldRequired`. Each event must declare `SchemaVersion`, `MessageId`, `PayloadHash`, `EventId`, `SequenceNo`, `TraceId`, `DeploymentId` where relevant, `ReleaseId`, `Epoch`, `HostId`, an explicit Workpiece scope, and persistence/ACK guarantees with A5/A6.

**Preparation/deploy candidate:** `CREATED -> STAGED -> PREPARING_DEVICES -> WAITING_PLC_READBACK -> PREPARED_BOTH -> COMMITTING -> VERIFYING_ACTIVE -> ACTIVE_SIMULATED`. Every disconnect/timeout/invalid SHA, Epoch, partial or conflicting readback goes to `FAILED_SAFE` -> `ROLLING_BACK` -> `ROLLED_BACK_VERIFIED_HOLD` **only after** old Release and both devices + virtual PLC old parameter readback verified; otherwise stay `FAILED_SAFE`. `ACTIVE_SIMULATED` and `ROLLED_BACK_VERIFIED_HOLD` NEVER imply production READY. Release publishing is a separate `DRAFT -> VALIDATED -> FROZEN` workflow; save never promotes Active.

## 4. Proposed typed error codes (not officially assigned)

`C10_REVISION_CONFLICT`, `C10_STALE_BASE_RELEASE`, `C10_INCOMPLETE_RELEASE`, `C10_HASH_MISMATCH`, `C10_DUPLICATE_RELEASE`, `C10_INVALID_STATE`, `C10_READBACK_INCOMPLETE`, `C10_READBACK_MISMATCH`, `C10_STALE_EPOCH`, `C10_PEER_DISCONNECTED`, `C10_TIMEOUT`, `C10_IDEMPOTENCY_CONFLICT`, `C10_ROLLBACK_UNVERIFIED`, `C10_SIM_ONLY_DENIED`.

A0 to decide machine numeric codes, retryable/fatal classification, unified C-01 error envelope, transport status mapping and telemetry severity. A7 must not freeze them through this mock.

## 5. Executable contract review oracle and evidence

- File: `tests/contract/solution/test_c10_review_oracle.py` (review-only, in-memory, Python stdlib, no hardware, no Core/QML integration, no I/O). Uses genuine SHA256 calculations on tiny **synthetic** artifact bytes; **not** validation of actual model/machine files or vPLC FINS/WORD transactions.
- Command: `AOI_SIM_ONLY=ON python3 tests/contract/solution/test_c10_review_oracle.py -v`.
- Actually run local Linux container: **34 unit tests, 34 PASS, exit code 0** (raw output also archived). Positive checks: identical model/different product parameters isolated; independent station edits; immutable release+hash; both Core and both virtual PLC mock receipts; post-commit readback; verified old-release rollback ends HOLD. Negative checks: stale revisions/base/Epoch, missing/calibration/model/device/ROI, tampered bytes, duplicate release, single Core, partial/mismatched virtual PLC, early commit, precommit readbacks reused as active, offline/timeout, duplicate MessageId different hash, rollback mismatch, SIM_ONLY off/physical-write attempt.
- Test file SHA256 and raw log SHA256 are in companion `tests/contract/solution/C10_REVIEW_EVIDENCE_20261016.md`. No claim of CI/Qt build/Windows native/two real Core processes/FINS simulator; **W083-W087 and C01 remain NOT_VERIFIED**. A10 must independently re-run the exact published Git SHA, inject mutation and sign PASS/HOLD.

## 6. Requested A0 decisions (before C01 freeze)

1. Approve separate workpiece and deployment envelope variants, or provide strict type-specific fields without placeholder IDs (B02).
2. Freeze distinct immutable Release schema (vs non-deployable v2 Draft) and field-level SHA/ROI, 11-channel/9-device invariants (B03-04).
3. Freeze station vs document revision, BaseReleaseId conflict, duplicate publish/idempotency, authority and audit semantics (B05-06, B11).
4. Freeze staged/PLC readback receipt provenance & byte/float contract with A2/A3/A5 and PREPARED_BOTH quorum, PostCommit fence/rollback semantics with A1/A5/A6 (B07-10).
5. Document C10 versioned DTO compatibility table, positive/negative schema fixtures, error codes, A10 signoff and official W003/W004 ledger status (B01/B12).

**No contracts/, libs/domain/, apps/, plc/, DB migration, CI, immutable original plan or main were modified by Agent06.**
