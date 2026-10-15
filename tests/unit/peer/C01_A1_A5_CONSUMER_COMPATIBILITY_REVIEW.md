# Agent01 A1+A5 — C01 PR #29 consumer review

**Decision: HOLD / C01 NOT_FROZEN.** Review pinned PR #29 head `1dc75f37b28b76e1f028799521897f1ab686d9f7`, main base `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`. No alteration to #19. Only owned `tests/unit/peer/` changed.

## Compatibility decision

| Area | Result | A0 freeze requirement |
|---|---|---|
| PLAN C01 C-01/C-04/C-05/C-06/C-08 registry | ACCEPT direction | Preserve original PLAN names; avoid C00 C-07..C-12 collision |
| WorkpieceId, ReleaseId, CaptureId, ChannelId | CONDITIONAL | WorkpieceId never equals barcode; define issuer, host/site scope, normalization and provenance |
| Epoch / AttemptNo | HOLD | Schema accepts 0; current C00 `assess()` rejects Epoch=0/active AttemptNo=0; uint64/uint32 upper limits missing; explicit message-type exceptions |
| ProcessingStatus / QualityStatus | HOLD | Define allowed pair matrix and precise C++ CamelCase ↔ uppercase wire map, ERROR/MISSING/TIMEOUT must never result OK |
| PeerLink messages | HOLD | Tagged Handshake/Result/FinalDecision/PERSISTED_ACK/NACK/Resume variants; HostId/source/destination, role, SequenceNo, TraceId, schema negotiation, replay protection |
| PERSISTED_ACK | HOLD | Binding to MessageId+PayloadHash+receiver+committed durable receipt; ACK only after atomic inbox+business-effect WAL commit; identical replay returns ACK, not second effect; different hash quarantined |
| Restart / 60s disconnect | HOLD | Bounded retry and dead-letter policy, monotonic clocks, session fencing, old Epoch/Attempt quarantine and exactly-once final counts |
| Final result | HOLD | Master alone may produce FinalOutcome; Slave may only report its own result; CH01 positioning prerequisite plus CH02–CH11 completeness, barcode verification, no CAM_DONE→OK |

## Actual verification

A0 PR29 blob identity checked with `git hash-object` against:
- registry `6f60ecc77707a793efad0ca6a024cc3bfcfeb6fd`
- JSON schema `f15170a0461bad8c23ce4d2a987f9fdc6ee63558`
- A0 unittest `02a22aaf3b216357e8b0a85ac1612b114f35e1cf`

On exact blob copies:
- `python3 contracts/C01/test_c01_draft_contract.py`: exit **0**, 8/8 PASS (A0 narrow draft self-tests)
- `python3 tests/unit/peer/c01_schema_consumer_audit.py contracts/C01/CORRELATION_ENVELOPE_DRAFT.schema.json smoke`: exit **0**, 8/8 basic schema smoke PASS
- same command with `audit`: exit **2 EXPECTED HOLD**, 19 detected pre-freeze gaps (8 malformed/invalid payloads accepted by schema, 11 missing envelope/protocol guarantee fields). Distinguish expected rejection from successful C01 behavior.
- Separate local **fixture-only** Python SQLite WAL model: `python3 test_c01_peer_ack_prep.py` exit **0**, 10/10; tests ACK drop/replay, duplicate hash conflict, crash before commit rollback, cross-piece/old Epoch isolation, logical 60-second outage, Slave final-denied. It is NOT a real network/IPC/PeerLink implementation; this separate fixture has not yet been independently run on remote PR SHA.

Local evidence manifest SHA256 `42bff0ae49f5a59dbfaa21d29d0fb38c65d484367d683f3d35231d0b61179be1` (from local runner round 2). Audit script SHA256 `c305c1e40e93cf5a23ce634169f41f0ea6d776c779c54f9a13251caea0ae20bb`. Logs local, not CI.

**Required positive/negative contract tests before C01 freeze:** valid typed IDs; same barcode different pieces; old Epoch/Attempt, other CaptureId/ReleaseId, unknown versions/fields, oversized integers; mismatched/forged/replayed ACK, ACK before commit, crash between inbox and effect, replay under disconnected/restarted peers; rejected unauthorized Slave FinalDecision; missing CH01 or CH02–CH11 and NG vs ERROR/MISSING/TIMEOUT. A10 independently reruns exact SHA.

**WBS DependsOn NOT VERIFIED:** W004←W003; W014←W011,W012; W035←W017,W018,W012; W042←W013,W014; W043←W018,W042. No business implementation or merge. Next readiness path: A0/A10 verify W003, freeze W004 C01; progress W007/W010→W011/W012→W014, then W042 and W043. All runs SIM_ONLY; zero real PLC/camera writes; Windows native/FIELD NOT_VERIFIED.
