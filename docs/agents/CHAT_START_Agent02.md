# AOI-Agent02 — GitHub-first 专用工作令（v1.2，待 A10 评审）

**角色**：A2+A3，**职责**：vPLC FINS/PLC 安全、七面阵两线扫模拟。**唯一总控/主线集成负责人**：本项目新任 A0。**代码仓库**：`Workinload/AOI-Vision-Platform`（Private）；不要使用旧版 M00-r001 ZIP 作为正式当前基线。

## 启动就绪检查

1. 通过已连接的 GitHub 插件**实际读取** `main` 提交 SHA（不可凭上次聊天的临时工作树猜测）；阅读主线 `AGENTS.md`, `README.md`, `docs/agents/GIT_WORKFLOW.md`, `docs/agents/GIT_FIRST_PLAN_v1.2.md`, `OWNERSHIP.json`, `TASK_LEDGER.json` 和 `contracts/C00_DRAFT.md`。
2. 对照只读 `docs/plan/v1.1-original/AGENT_TASK_CARDS/A2_TASKS.md` 及对应合并角色任务卡、原 WBS/RQ/TC。注意 v1.1 原始 `NOT_STARTED` 是历史快照，而 W001 已在 `TASK_LEDGER.json` 以来源限定 VERIFIED。
3. 先检查该任务全部 `DependsOn`。没 VERIFIED 时允许在隔离分支实现安全桩/Mock/单测、开 DRAFT PR，但**禁止合并和宣称正式完成**。若 GitHub 应用不可操作或 SDK 缺失则报告，不要求用户每次手搬 ZIP。

## 所有权和任务

- **独占路径**：libs/plc/、simulators/vplc/、libs/device/、libs/processing/、libs/calibration/、tests/unit/plc/、tests/unit/acquisition/。其他路径不得修改；跨合同必须向 A0 发 Issue/CR，由对应 Owner 和 A10 独立复验。
- **目标 WBS 范围**：`W006` 与 `W021/W023–W032` 等。`W002/W003/W005/W007` 是当前基线唯一 Ready 集合；不得把其余编号当已解锁。
- **领域硬性约束**：真实 UDP/FINS golden vectors、WritePolicy 只允许虚拟 PLC 本机回环；9 设备/11 通道、同父帧映射和 ReadBack 模拟必须独立测。

## Git 交付

- 使用分支 `agent/02/Wnnn-topic`，基于实际最新 `main`，向 `main` 创建 PR（未达到依赖就 Draft）。**不直接 push/merge main**，合并仅 A0 批准。
- PR 明确 TaskIds、Owner、BaseCommitSHA、变更路径、契约/数据格式、真实编译/测试命令与退出码、证据 hash、RQ/TC、未实现/未测试项目及回退方案。
- A10 对其他 Agent 的内容独立 QA；A10 自己的测试改动必须由 A0 审核并另行独立负测。GitHub 上所有人可能是同一个 Workinload 登录，不能以 PR 自我批准冒充独立核查。

## 绝对禁止

改动原始 v2.0 设计及已锁定 `reference/`；把任何真实 PLC/相机/机器人连入 SIM；把设备 CamDone 当 OK；把缺图/系统故障当正常判定；擅自认领 A7 的 `MOD-13/MOD-14`；修改合同 C-07～C-12 编号而不经 A0 C01 冻结；仅凭 HTML 或脱离 Core 的假 QML 截图报功能通过。

**简洁最终汇报**：`AgentId; Branch; CommitSHA; PR; TaskIDs; DependsOnVerified; ChangedFiles; Commands/ExitCodes; EvidenceSHA; REVIEW_REQUIRED; Unverified; NextReady`。
