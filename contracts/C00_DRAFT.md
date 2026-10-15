# C00-DRAFT · Contract Freeze Proposal (A0 / W004 pending)

**State: draft. NOT approved C01 release.** Target PLAN v1.1 §4 lists C-01...C-12. Actual source-of-truth CSV/JSON schemas are under `reference/spec_v2_machine_readable`. Read actual v2 spec before implementing features.

## Draft typed identities
- Strong, non-interchangeable C++20 IDs: `WorkpieceId`, `ReleaseId`, `CaptureId`, `ChannelId`, full link `Workpiece + Epoch + Release + Channel + Capture + Attempt`.
- A workpiece barcode is **not** a unique `WorkpieceId`; Master/Slave may be viewing different live workpieces.
- `ProcessingStatus={Pending,Running,Succeeded,Error,Cancelled,Timeout}` separate from `QualityStatus={Undecided,Ok,Ng,Missing,SkippedByVerifiedCondition}`.
- Event compatibility requires `SchemaVersion`, unique EventId/MessageId, PayloadHash, SequenceNo, OccurredUtc and monotonic delta, stable TraceId and correlation IDs. These fields are not fully implemented in M00.
- Core simulated IPC `health\n` returns one JSON object plus LF; other commands denied. This **bootstrap health-only contract** does not stand in for final authenticated versioned IPC.
- vPLC simulated UDP `M00_HEALTH_V1` to 127.0.0.1 port; response `M00_SIM_ONLY_FINS_NOT_IMPLEMENTED`. This **is NOT FINS or PLC protocol**. Real FINS-only sim v1 implementation belongs to A2 W023 after prerequisites.

## Planned v1 contracts and responsible roles
| ID | Interface | Owner | M00 state |
|---|---|---|---|
| C-01 | IDs/processing/quality/event schema | A0 | Partial C++ draft; source schemas embedded |
| C-02 | IAreaCapture/ILineCapture/FrameRef and ReadBack | A3 | NOT_IMPLEMENTED |
| C-03 | IPLCGateway/SignalRegistry/WritePolicy | A2 | NOT_IMPLEMENTED |
| C-04 | IWorkpieceTracker/AcquisitionCoordinator | A1 | NOT_IMPLEMENTED |
| C-05 | IStationDecision/AggregateDecision | A1 | Pure illustrative safety test only |
| C-06 | PeerLink reliable envelope, PERSISTED_ACK | A5 | NOT_IMPLEMENTED |
| C-07 | SQLite asset/events durable transaction | A6 | SIM-only bootstrap DB; final NOT_IMPLEMENTED |
| C-08 | ProductSolution draft/release/deployment | A7 | NOT_IMPLEMENTED |
| C-09 | UI↔Core read snapshots and command RBAC | A8+A9 | Health read-only; writes REJECTED |
| C-10 | Alarms/log/audit semantics | A9 | NOT_IMPLEMENTED |
| C-11 | Vision pipeline MockInference and ROI | A4 | NOT_IMPLEMENTED |
| C-12 | Simulation scenarios/evidence/test status | A10 | Initial five-process smoke only |

## Critical safety assertions
1. `CAM_DONE`, `FRAME_READY` and `PRODUCT_FINAL_RESULT` are distinct.
2. Any missing channel/invalid CaptureId/processing timeout/epoch mismatch/unverified barcode must not produce OK.
3. CH01 is positioning, not a final defect channel. Required final defect channels CH02..CH11.
4. 9 physical capture devices map to 11 result channels; two pairs of line scan channels each share one parent frame.
5. Sim-only IPC cannot write an arbitrary PLC D address; true physical PLC addresses should NEVER be activated based solely on legacy static evidence.
6. Draft state never becomes active release on UI save; future readback of device and vPLC must precede prepared-both.
7. All hardware-related operations use replaceable adapters, not PLC/camera code embedded in QML.

## Change process
Agent proposes contract change using `docs/agents/CHANGE_REQUEST.md` and stops implementation against the disputed interface. A0 selects option, A10 validates cases, and only then can contract advance to C01. No silent unilateral rewrite.
