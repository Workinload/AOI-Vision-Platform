# A0-W001-20261015 · 正式来源/哈希验收审批书

**Decision time:** 2026-10-15 15:46:51 +08:00
**Task:** W001 / Owner A0 / Independent Reviewer A10
**Decision:** **A0_APPROVED_VERIFIED_SOURCE_PROVENANCE**。审批范围仅为「冻结 v2.0 来源及哈希，原始证据只读边界可复算」。不扩大到 GUI、生产逻辑、Windows/现场验收。

## 被验证的不可变工件

- Source M00-r001 ZIP SHA256 `c795faee0072ec6162a070464b00c7a1770d8b1ee3743f20fa5dc16a41653626`；319875 bytes、102 files、CRC + 内层 manifest + source ZIP protocol validator PASS。
- PLAN v1.1 ZIP SHA256 `154f632edc3d6352c6d57a95b7c1b6e8d038aa8782a34d1d571538c791a87ffe`；55 files / 130 WBS，10/10 embedded PLAN refs exact bytes。
- 原始 v2.0 ZIP SHA256 `de0f36f5c270e07abedc0e317d8517c8f5e0d86a5a42897120d42b3a1f5dec47`；CRC PASS，14/14 锁定 reference bytes 一致。
- Qt 模板 SHA256 `c2ba0f27012750756641a2ab626d5696444b6b51ecd8c66336a019dc07cb06ac`；SDK manifest SHA256 `adcbf986f508913d4cf9be60ec480c7da7ac966c0285b34bf9e6365aaf4427cf`。
- A10 独立 QA `AOI_Agent05_A10_M00-r001_QA-r001_REPORT.md` + `...EVIDENCE.zip`；QA evidence 68 indexed files hashes PASS。A10 明确 W001 推荐通过，不越权编辑 A0 ledger。

## A0 两轮复查

**Round 1:** independently recalculated five input hashes, all relevant ZIP CRCs, 14 v2 source refs, 10 PLAN refs, WBS DAG, 21 owner mapping, safety gates. `A0_INDEPENDENT_HANDOFF_AUDIT.log` exit 0; `A10_TRACE_RERUN.log` exit 0; 5/5 agent patches structural validation PASS (does not imply functional acceptance).

**Round 2:** Debian/GCC14.2/CMake/Ninja, external read-only-cache Qt6.11.2+OpenCV5.0.0 source rebuild exit 0, 39 Ninja steps, CTest 6/6 PASS exit 0; patched five-process QA smoke exit 0 with Qt rendered master/slave screenshots and loopback virtual PLC health; independently reran Agent04 denied-IPC and missing-Core QML probes exit 0. Negative SDK manifest mutation and `AOI_SIM_ONLY=OFF` correctly fail exit 1. `A0_LINUX_CONFIGURE.log`, `A0_LINUX_BUILD.log`, `A0_BASE_CTEST.log`, `A0_PATCHED_SMOKE_RUN.log` hold raw evidence.

## Exact gate promotion policy

1. At A0 governance level W001 **APPROVED**; W002/W003 may proceed in a *private* worktree under the explicit scope above.
2. **Official M00-r001 is immutable.** `docs/agents/TASK_LEDGER.json` in that source ZIP still says `IMPLEMENTED_PENDING_A10_REVIEW`; no replacement/overwrite will occur.
3. New candidate carrying `W001.status=VERIFIED` must be produced through an A0-authorized audited integration path and independently checked by A10; promote to official only after QA. All W005/W007 and downstream blocked patches must continue honoring the *current official source* until that promotion. CR-A0-001 tracks missing A0-authorized tooling identity.
4. A10's ongoing second self-check (requested by user) is not yet concluded in the cloud snapshot. If new negative evidence contradicts this approval before release, re-open gate and suspend promotion; never conceal test failures.

## Explicitly NOT approved

No C01, W002/W003/W004/W005/W007/W010/W011, Windows native, FINS, 9 mocks, 11-channel quality, 114 functional TC, 52 scenario execution, field hardware/PLC writes, FG signoffs or SIM_READY. Source/SDK originals left untouched.
