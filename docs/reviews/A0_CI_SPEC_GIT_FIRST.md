# A10 implementation specification — GitHub CI and safety gates (not yet implemented)

**Owner**: Agent05/A10 (`.github/workflows/`, `tests/`, `tools/`, CMake), A0 approves. **Status**: DESIGN_ONLY; none of these CI checks is currently running merely because this document exists.

## Minimum PR pipeline

1. **Python static (fast, offline)**: `reference/` content and locked `BASELINE_LOCK` unchanged; WBS 130 DAG and RQ114 ↔ TC114 refs; 14 FG / 45 PLC write FALSE; path ownership and cross-path CR; current `TASK_LEDGER.json` and PR TaskIds `DependsOn` guards; formal C01 schema/version/malformed input negatives. Only modify WBS via reviewed versioned follow-up CR, never edit `reference/plan_v1.1/` or read-only `docs/plan/v1.1-original/`.
2. **Linux Qt + OpenCV**: exact Qt 6.11.2 and OpenCV 5.0.0, C++20 Debug + Release, two Core/two Qt Quick UI/vPLC process real socket smoke, strict PNG CRC and missing/corrupt PNG refusal, SQLite WAL independent roles, deny PLC physical write and non-loopback endpoint, CTest full exit 0. Restore allowed offline SDK from secure artifact/runner cache and validate part SHA; absent SDK is `BLOCKED/NOT_VERIFIED`, not an apparently green job.
3. **Windows x64 Native**: exact SDK ABI and QtQuick/Controls2 plugin closure, qwindows.dll/qsqlite.dll/QML imports, actual Windows PE on a Windows runner, native clean extraction dual-UI/dual-Core/vPLC, display and Chinese font fallback, OpenVINO2025.4.1 real smoke if enabled. Cross-compile or Wine-only cannot replace native evidence.
4. **114 RQ/TC and 52 scenarios**: fully real business scenario runner after functional modules arrive; count each actual assertion/exit/evidence, never mark a static mapping as TC executed. Fault injection: delayed Epoch, duplicated barcode, frame-loss, D1013 alias, wrong D1016 flow, packet corruption, DB crash, peer reconnect, approval bypass and product half-release.
5. **Artifact provenance**: attach full command logs, process/socket captures, screenshots, SQL queries, source commit, SDK manifest, build IDs and SHA256. CI must not connect real PLC IP or add physical device adapter.

## GitHub service constraints

- Current private personal repository rulesets endpoint responds 403 with Pro/paywall warning. Codeowners and workflow cannot claim automated *mandatory* review/branch protection until GitHub reports it active; A0 must operate procedural merge gates meanwhile.
- One GitHub login may represent five ChatGPT agents; Github "approved review" from that login alone is not independent A10 proof.
- Use matrix/environment mark: `PASS`, `FAIL`, `BLOCKED_MISSING_SDK`, `NOT_VERIFIED` and `NOT_APPLICABLE_APPROVED`. Do not give a success name to an unexecuted Windows-native job.
- Owner Agent05 submits new PR for actual CI; do not let A0 governance PR impersonate test implementation.
