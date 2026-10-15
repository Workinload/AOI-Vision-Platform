# Agent04 A8+A9 — PR #29 C-11/C-12 consumer review (HOLD)

Reviewed PR #29 exact head: `1dc75f37b28b76e1f028799521897f1ab686d9f7`.
Reviewed PR #26 exact head: `8b4ae88cfa56ce0ad13e3dd503d34c454fc31ef1`.
Base main: `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`.
Decision: **HOLD / C01 NOT_FROZEN / NOT_INTEGRATION_READY**. W003 is not VERIFIED.

## C-11 UIReadAPI consumers

1. Versioned authoritative per-Core/HostId initial snapshot, then monotonic ordered deltas; define stream generation, epoch, sequence, digest, TTL, heartbeat and cursor retention.
2. Dropped sequence, old epoch, wrong host, conflicting duplicate digest => invalidate LIVE presentation and request new full snapshot; do not reuse cached good state after disconnect or silently show OK.
3. Device map, PLC parameter table and logs must project one Core-owned EntityRegistry with canonical HostId, StationId, DeviceId, SignalId, ChannelId, revision. Actual, previous, draft, readback and quality/timestamps are distinct. A2 owns 45-signal truth and 9 physical devices to 11 result channels mapping.
4. Correlate per-workpiece results with WorkpieceId+Epoch+ReleaseId+ChannelId+CaptureId+AttemptNo. ProcessingStatus and QualityStatus independent; CH01 position vs CH02..CH11 defect channels. Never display missing/stale image as OK.
5. C00 health-only IPC does **not** provide a C01 cursor/subscribe API. Do not invent wire opcodes before A0 freeze.

## C-12 AuthAudit / AlarmEnvelope / Telemetry consumers

1. Core-side fail-closed authorization: no, expired, revoked, forged, wrong-role session, unknown command, replay, optimistic-lock conflict and physical PLC writes all rejected. QML disabled controls are not a backend security boundary. SIM_ONLY overrides admin capability.
2. ACK is an acknowledgement; it must not clear the underlying active fault, change its health status to green or imply CLEAR. CLEAR needs root source restored+readback, independent authorization and durable audit; otherwise reject.
3. Append-only cross-host audit must bind EventId, TraceId, HostId, session actor, target EntityId, denied action, reason, wall+monotonic time quality and integrity/recovery; in-memory hashes are insufficient for a durable audit claim.
4. Telemetry must carry provenance, freshness, validity/clock quality and alarm-cause links; missing data displays UNKNOWN/OFFLINE rather than fabricated normal.

## Two C01 draft schema contradictions requiring A0 resolution

- The prose asks for HostId, TraceId, station/device/capture group, monotonic sequence and time quality. Yet `CORRELATION_ENVELOPE_DRAFT.schema.json` has `additionalProperties:false` and lacks those properties: legitimate enriched events would be rejected.
- The same draft schema always requires WorkpieceId+ChannelId+CaptureId, impossible for a pure PLC communication alarm or an authorization denial not associated with an image. Define discriminated, event-specific schemas or explicit optional contexts, never dummy image identity.
- PayloadHash currently checks only hex shape, not hash of a canonical serialized payload. Define algorithm/encoding, replay and duplicate semantics.

## Required positive/negative contracts before merge

C11: full+delta, disconnect, cursor gap, stale epoch, foreign host, duplicate changed payload, snapshot-delta race, source-revision conflict, map/table entity crosslink.
C12: anonymous/expired/revoked/unknown/forged command, administrator physical-write denial, ACK still active, CLEAR without recovery denied, audit crash-replay, telemetry TTL/time unknown.
A10 to independently execute exact SHA tests; A0 to freeze DTO/schema/ABI first after W003 VERIFIED. No Agent04 edits to A0 public contracts.

## Evidence boundaries

PR #26 source files were reproduced and git-blob checked against GitHub. Python static suite: exit 0, 3/3. Qt 6.11.2 actual Linux offscreen QQuick rendering screenshots: 1920x1080/2560x1440 main and map/table, each exit 0. **This validates a MOCK/OFFLINE QML view only.** Separate local C11/A9 consumer-prep fixtures: 13 UI + 10 security unittest cases each pass, exit 0. They are not IPC or real Core/DB/backend RBAC tests.
