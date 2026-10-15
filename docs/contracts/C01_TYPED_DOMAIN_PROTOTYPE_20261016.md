# A0 C01 typed-domain prototype gate (2026-10-16)

Status: **REVIEW_ONLY / NOT_FROZEN / W004 NOT_VERIFIED / SIM_ONLY**.

Source: `libs/domain/include/aoi/domain/c01_review.hpp`; tests: `tests/unit/domain/c01_review_test.cpp`.
Owner: A0; based on main `cf11c7d436910c2d5da4566572b65c5d13c86fa4`. The final contract must be reconciled with PR #29 at exact SHA `dd3ace7907a913e5df49bd0a8c90f610beebcbc5` and all consumer reviews.

* Separate non-interchangeable typed Workpiece, Release, Capture, ParentCapture, Group, Channel, PhysicalDevice, Host, Message, Event and Trace IDs; Epoch, AttemptNo and SequenceNo must be positive.
* Strict CH01..CH11; WorkpieceResult requires all identity fields, while ALARM / DEPLOYMENT / ASSET / UI / AUTH variants must not forge per-workpiece identity.
* Processing and Quality are separate axes. ERROR/TIMEOUT/PENDING + OK/NG rejected; NG is not an implicit OK. This is a local prototype predicate, not the authoritative W040 quality engine.
* Host must be MASTER or SLAVE, version explicitly `C01-REVIEW-0.2`, SHA256 field must be lowercase hex; fields' syntax is not proof of actual payload hash, freshness, replay identity or WAL durability.
* RAW_FRAME parent identity groups Host/CaptureGroup/PhysicalDevice/Attempt; database uniqueness and real bytes are still A6-owned.
* No device I/O, no PLC writing, no Core/PeerLink/Qt linkage. Do not merge until W003 is VERIFIED, final C01 data/ABI contract agreed and A10 independently tests exact head.

Proposed deterministic execution (not claimed until actually run):
`g++ -std=c++20 -Wall -Wextra -Werror -pedantic -Ilibs/domain/include tests/unit/domain/c01_review_test.cpp -o /tmp/aoi_c01_review_test && /tmp/aoi_c01_review_test`.

Blocking follow-ups: canonical hash bytes, event variant DTO payloads, PERSISTED_ACK atomic WAL semantics, FrameRef ownership, 2-Core/vPLC genuine staged readback and phase fencing, UI cursor/delta and backend audit retention. No claim of actual C01 freeze.
