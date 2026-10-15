# A0 M00-r003 official source promotion — scope and evidence

Date: 2026-10-15 / A0 sole source integration coordinator.

## Immutable approved input

A10 Agent05 reviewed candidate `AOI_Vision_Platform_M00_r003_CANDIDATE_QA_REQUIRED.zip`, exact SHA256 `3217da7a30c3b60a5232629df738d1476be4897da507aad22708b43e457aa4ae`, issued `PROMOTION_PASS` for Linux Qt6.11.2/OpenCV5.0.0 five-process health-only source scaffold, SIM_ONLY, C00-DRAFT, source governance only. A10 ran unmodified CTest 6/6 twice, strict missing/corrupted PNG failure injection, provenance and ownership checks. A10 did NOT sign this subsequently generated official ZIP byte hash.

## A0 independent separate official-release transition

A0 promotes candidate with only these A0-owned differences:
- `RELEASE.json`: change `status` to `OFFICIAL_SOURCE`, add explicit limited-source status and candidate-A10 provenance; no SIM/field/Windows release flags relaxed;
- `docs/agents/TASK_LEDGER.json`: record published M00-r003, preserve every WBS item status exactly; W001=VERIFIED/SOURCE_PROVENANCE_ONLY, W005/W007 not VERIFIED;
- `docs/reviews/A0_R003_OFFICIAL_PROMOTION_20261015.md`: this governance note;
- `FILE_HASHES.json`, `SHA256SUMS.txt`: generated deterministic digest maps.

All other candidate files and all original v2 design reference bytes MUST remain byte-identical. Signed r003 candidate is immutable; prior official M00-r001 and held M00-r002 remain archived unchanged. The official archive obtains a new SHA256 due to approved governance changes. A0 must independently validate archive CRC, file hashes, exact deltas, original source locks, full unmodified CTest and negative PNG tests for the newly packaged official tree. No A10 review of new official SHA is claimed; A10 candidate PASS is evidence for unchanged build/test business code and independent source QA. A0 owns this promotion decision.

## Non-acceptance

`SIM_ONLY=true`, `sim_ready=false`, `Windows native=NOT_VERIFIED`, 114 formal TC NOT_EXECUTED, 52 formal scenarios NOT_EXECUTED, 14 field gates still unsigned, every PLC production write permission FALSE. This is an official M00 source scaffold, not an installed/production-ready AOI system.

Future GitHub collaboration must use a PRIVATE `Workinload/AOI-Vision-Platform` repository; branch protection and CI are separate gates. Older Agent01..04 preparation patches are not merged into this source.
