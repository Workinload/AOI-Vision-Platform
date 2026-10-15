# CR-A0-004 — 独立 Agent06 承接 A7 Solution / Deployment 角色

**日期：** 2026-10-15｜**请求及批准：** 用户同意新增 Agent06，由 A0 记录（仅人员路由）。｜**独立复核：** A10 对原 HEAD `f9604c681160a3de534f636510937ea153689eeb` 已作两轮审核，结论 `HOLD_FOR_STATUS_CORRECTION`；本更正提交待 A10 再审。  
**目标仓库：** `Workinload/AOI-Vision-Platform` PRIVATE；**创建时正式 main SHA：** `761af2c84a8692dc8a782575822209aca16668f8`；**当前已验证 main SHA：** `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`（PR #3 治理合并，M00-r003 为上次正式发布 SOURCE，SIM_ONLY）。  
**父治理历史：** PR #3 的原审计 head `e2595df68eb2810b7315683b4b638ce05a4ca251` 已由 A0 审核合并，main 合并提交 `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`。本 PR #12 已改以 `main` 为 base；**本更正只更新路由文档，未改 main 或任何原设计文件**。

## 决策与边界
- 用户已同意 **另建 AOI-Agent06 对话**，专职实现原设计 **A7**，不增删原 PLAN 的 A7 owner。原 **MOD-13 SolutionManager**、**MOD-14 DeploymentManager** 归 A7，执行 Chat 路由为 AOI-Agent06，未来任务 W083～W087。
- 此决定只解决 **人力职责缺口**，**不解除** W083..W087 的原 WBS DependsOn、不更改 `TASK_LEDGER.json` 的任务状态，不授予 A7 以外任何模块的写权限。
- 登记状态现为 `USER_APPROVED_A0_AUTHORIZED / AGENT06_STARTED`，可核证 GitHub Issue #11 comment `6190633300` 与隔离预研 PR #14（HEAD `26e1e78bf784ab0d9617ed235aa10371e8754327`）。**仅表示聊天已启动、Mock 已交付；不是 W083～W087 正式完成、CI PASS、C01 冻结或 A10 业务签审。**
- PR #3 的 Git-first v1.2 治理规范已通过独立审查并由 A0 合并；主线已改变为上述 Git SHA，但上一次不可变正式源码包仍是 M00-r003 OFFICIAL。Agent06 工作令本身仍属 PR #12 待审路由变更，不可假定已在 main 生效。
- 授权的 A7 代码目录仅限 `libs/solutions/`、`libs/deployment/`、`tests/contract/solution/`、`tests/unit/solution(s)/`、`tests/unit/deployment/`、`config/product/`。公共 C01 由 A0 冻结，C-07..C-12 旧编号不一致必须先解；跨属主改动需要 CR。
- 双机 staged/readback/commit/epoch/rollback 全面 SIM_ONLY，未经 14 项现场 FG 授权永不允许真实生产写；错误、缺件、读回不符和单侧断线必须 FAILED_SAFE。

## A10 独立复核要求与合并门禁
1. 验证两轮：Git 仓库 PRIVATE；差异限治理文件；原 55 份 PLAN 原文、130 WBS、C00、RELEASE、TASK_LEDGER、原始源码/测试及 A10 PR #3 **exact SHA 未动**。
2. 逐项核对 A7 5 项任务和 DependsOn 的原始任务卡及 `WBS_TASKS_130.csv`；所有权路径与已有角色不得冲突；Agent06 只有 A7 一种代码角色。
3. 核对工作令对于 BranchDev/MergeReady/ReleaseReady/FieldAccepted、模拟读回、PLC 禁写和负测的表述。缺少 C01 或原 DependsOn 不得合并业务 PR。
4. 只有 A10 对本 Draft PR **更正后的确切 HEAD**给出新的独立证据和 PASS，且 A0 明确记录合并裁定后，才能合并；原提交 `f9604c6` 的 HOLD 不可转用为 PASS。合并本路由 PR 不代表 W083..W087 通过。
5. 仓库 Ruleset/CI 目前不可当作已经强制生效。用户已实际启动 Agent06，Issue #11 和 PR #14 可核实；不假装此事实意味着业务 WBS 的 MergeReady 或 FieldAccepted。

**A0 结论：人员分工授权 APPROVED，代码/任务/正式 Git 合并仍 HOLD_BY_DEPENDENCY_AND_A10。**
