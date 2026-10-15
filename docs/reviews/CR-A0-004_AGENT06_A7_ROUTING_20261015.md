# CR-A0-004 — 独立 Agent06 承接 A7 Solution / Deployment 角色

**日期：** 2026-10-15｜**请求及批准：** 用户同意新增 Agent06，由 A0 记录（仅人员路由）。｜**独立复核：** A10 PENDING。  
**目标仓库：** `Workinload/AOI-Vision-Platform` PRIVATE；**正式 main SHA：** `761af2c84a8692dc8a782575822209aca16668f8`（M00-r003 OFFICIAL_SOURCE，SIM_ONLY）。  
**父治理工作：** Draft PR #3，确切 head `e2595df68eb2810b7315683b4b638ce05a4ca251`；当前 A10 正在核查，不允许更动 PR #3 的 head。本路由使用 **独立子分支 + 独立 Draft PR**，以 PR #3 分支为基底，绝不擅自合并或更改正式 main。

## 决策与边界
- 用户已同意 **另建 AOI-Agent06 对话**，专职实现原设计 **A7**，不增删原 PLAN 的 A7 owner。原 **MOD-13 SolutionManager**、**MOD-14 DeploymentManager** 归 A7，执行 Chat 路由为 AOI-Agent06，未来任务 W083～W087。
- 此决定只解决 **人力职责缺口**，**不解除** W083..W087 的原 WBS DependsOn、不更改 `TASK_LEDGER.json` 的任务状态，不授予 A7 以外任何模块的写权限。
- 登记状态 `USER_APPROVED_A0_AUTHORIZED / PENDING_NEW_CHAT_START`，实际用户创建 Agent06 对话且按工作令启动后才可写 `AGENT06_STARTED`。不得将批准建聊等价于对话已启动、源码已实现、A10 已签审。
- 在 PR #3 的 Git-first v1.2 提交完成独立验收前，仍以 M00-r003 OFFICIAL 的 main 为正式代码基线。Agent06 本工作令为审查分支上的安全准备指令；不得以尚未批准的治理草案宣称 MAIN 已改变。
- 授权的 A7 代码目录仅限 `libs/solutions/`、`libs/deployment/`、`tests/contract/solution/`、`tests/unit/solution(s)/`、`tests/unit/deployment/`、`config/product/`。公共 C01 由 A0 冻结，C-07..C-12 旧编号不一致必须先解；跨属主改动需要 CR。
- 双机 staged/readback/commit/epoch/rollback 全面 SIM_ONLY，未经 14 项现场 FG 授权永不允许真实生产写；错误、缺件、读回不符和单侧断线必须 FAILED_SAFE。

## A10 独立复核要求与合并门禁
1. 验证两轮：Git 仓库 PRIVATE；差异限治理文件；原 55 份 PLAN 原文、130 WBS、C00、RELEASE、TASK_LEDGER、原始源码/测试及 A10 PR #3 **exact SHA 未动**。
2. 逐项核对 A7 5 项任务和 DependsOn 的原始任务卡及 `WBS_TASKS_130.csv`；所有权路径与已有角色不得冲突；Agent06 只有 A7 一种代码角色。
3. 核对工作令对于 BranchDev/MergeReady/ReleaseReady/FieldAccepted、模拟读回、PLC 禁写和负测的表述。缺少 C01 或原 DependsOn 不得合并业务 PR。
4. 只有 A10 对本新 Draft PR 的 **确切 HEAD**给出独立证据和 PASS，且 PR #3 已通过审查、A0 确认顺序后，才可合并。合并本路由 PR 不代表 W083..W087 通过。
5. 仓库 Ruleset/CI 目前不可当作已经强制生效。保留用户独立启动新聊天的最后一步，不假装后台能替用户新建 ChatGPT 对话。

**A0 结论：人员分工授权 APPROVED，代码/任务/正式 Git 合并仍 HOLD_BY_DEPENDENCY_AND_A10。**
