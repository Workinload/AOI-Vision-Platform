# Issue #15 — Agent01 identity/Epoch isolation preparation

**Status:** BRANCH_DEV / NOT_INTEGRATION_READY / A10_REVIEW_REQUIRED  
**Base main:** `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`  
**Mode:** SIM_ONLY; no PLC, camera, network socket, DB, Qt application, production writer or real acquisition.
**Contract:** C00-DRAFT; C01 NOT_FROZEN.

## Previous Agent01 work reused without duplication
The old unmerged M00-r001 patch supplied an A0-domain pure `assess()` preflight, not an actual W035 tracker. This PR builds a NEW stateful synthetic identity/Epoch/Attempt fixture and uses A0's UNMODIFIED domain header. The exact main `libs/domain/include/aoi/domain/contract.hpp` blob SHA is `a9665b824d6370811bba2ae93ef9c58955852702`. The historical ZIP is **not** this branch's base.

## Original WBS dependencies
- W035 → W017, W018, W012: **NOT VERIFIED**.
- W037 → W036: **NOT VERIFIED**.
- W039/W040 → W038 / W037+W039: **NOT VERIFIED**.
- W042 → W013, W014; W043 → W018, W042: **NOT VERIFIED**, separate PeerLink PR.
- W001 alone is VERIFIED with source-provenance-only scope. A0 C01 / W004 not frozen. The PLAN v1.1 CSV is historical, not current task approval.

## Real local execution evidence (2026-10-15, Debian Linux x64)
Reproduce from repository root:

```bash
bash tests/unit/production/run_issue15_identity_prep.sh /tmp/aoi_issue15_evidence
```

GCC 14.2.0 and Clang 17.0.0 were present. The local driver **exit 0**. Actual tested commands and exits:

| Command label | Exit | Observation |
|---|---:|---|
| gcc_build / gcc_16_cases | 0 / 0 | 16/16 synthetic scenarios PASS |
| clang_build / clang_16_cases | 0 / 0 | 16/16 PASS |
| ubsan_build / ubsan_16_cases | 0 / 0 | 16/16 PASS |
| mutate_epoch_build / mutate_epoch_detect | 0 / 1 | expected rejection, stale+future Epoch guards killed (14/16) |
| mutate_workpiece_build / mutate_workpiece_detect | 0 / 1 | expected rejection, WorkpieceId guard killed (15/16) |
| separate ASan+UBSan build / test | 0 / 0 | 16/16 PASS (local, not integrated into runner) |

The `1` on two injected **unsafe test-only mutations** is deliberately expected; never count the mutant binaries as passing software. Local fixture source SHA256 `4de82952d223ee0df93621d6aabdb0f6167afb9fbbb85439c5148f2c05aec67b`. SHA256 of local log manifest `242c993216c57aa8c5d146d6848bfa3919187f5055eff46ad5e61d755ef0f8ab`. Logs were generated locally; hashes identify those actual runs, **not remote GitHub CI**.

## Coverage and limits

Synthetic checks exercise 16 scenarios: distinct IDs for same barcode and for re-used slots, ambiguous slot quarantine, stale/future Epoch, wrong Release and CaptureId, superseded Attempt, idempotent duplicates, conflicting replay to HOLD, CH01 positioning and CH02..CH11 required, timeout/MISSING/NG distinctions, barcode mismatch, seeded out-of-order two-piece arrivals. Intended original trace: RQ-028, RQ-097, RQ-109, RQ-030, RQ-031, RQ-098 and related TC plans. **No original TC is marked executed/VERIFIED** by this mock.

No real W035 tracker or persistent ledger, W037 state machine, W042 PeerLink authentication, W043 persisted ACK, 60-second disconnect recovery, real SQLite WAL migration, Linux full Qt build, Windows-native test, FINS/PLC, hardware, CI or FIELD acceptance. Synthetic slots are explicitly *not* real PLC addresses. C01 must first freeze authoritative intake/slot/trigger evidence, Epoch/Attempt lifecycle and durable effect/ACK semantics. A10 must independently rerun the exact PR HEAD; A0 alone decides any future merge after DependsOn VERIFIED.

**Owned paths only:** `tests/unit/production/`. No other Agent paths or A0 contract modified.
**Rollback:** close Draft PR or revert these three isolated test preparation files.
