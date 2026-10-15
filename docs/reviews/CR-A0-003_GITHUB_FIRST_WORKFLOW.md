# CR-A0-003 — M00-r003 GitHub-first governance PR (no business acceptance promotion)

**Date:** 2026-10-15 | **Owner/Approver:** A0 | **Reviewer:** A10 pending
**Git base main:** `761af2c84a8692dc8a782575822209aca16668f8`
**Immutable source ZIP:** `AOI_Vision_Platform_M00_r003_OFFICIAL_SOURCE.zip`, SHA256 `737a140ff2da49da1428edd8152060da1818e0c3981711c105beecff6239f468`.

## Intent

Replace stale ZIP-only Agent instructions with a Git-first workflow, mirror the exact 55 original PLAN v1.1 files read-only for remote agents, fill unowned source paths, propose four-tier task gating, and explicitly surface current GitHub private-repo ruleset and CI limitations. Add `ADR-0005` and A0 review/CI requests. **No business module, formal WBS DependsOn, contract C00, PLC production signal, field gate, source ZIP, `RELEASE.json`, `TASK_LEDGER.json`, `BASELINE_LOCK.json`, frozen original references, or SDK is to be changed.**

A10 must independently audit 55 plan mirrors SHA, ownership map, link integrity, old/new instructions, safety invariants and no task-status promotion. A0 must not merge own governance PR as "A10-reviewed" until independent evidence exists.

## Special scopes

- `tests/patch/` / `tests/unit/opencv5_link_probe.cpp` / `.github/workflows/` newly assigned as A10-owned **path metadata only**; this PR does **not** modify tests or CI. A10 reviews these path additions.
- `A7` remains design Owner. A proposed `AOI-Agent06_PENDING_REGISTRATION` is a routing placeholder, not proof of a chat being created or 5 WBS being implemented.
- `FILE_HASHES.json` and `SHA256SUMS.txt` remain the sealed last release snapshot, not an auto-generated attest for intermediate Git commits. Future release packaging must calculate fresh archive checksum.

**Acceptance:** A10 signoff for governance diff only, A0 merges one PR via GitHub plugin, then separately opens A10 CI task; v2 source/plan/SDK stay unchanged.
