# AOI Vision Platform — GitHub-first engineering guardrails (proposal v1.2)

**当前源码位置**：`Workinload/AOI-Vision-Platform` 私有仓库 `main`；基线 `M00-r003 OFFICIAL_SOURCE`，并非 `SIM_READY`。本文件作为 Git-first 开发约束草案，需 A10 审核并由 A0 合并后生效。

## 唯一事实来源（优先级由高到低）
1. 用户当前批准的需求 + 不可变 `/AOI开发项目` v2.0 设计包（SHA256 `de0f36f5c270e07abedc0e317d8517c8f5e0d86a5a42897120d42b3a1f5dec47`）；只读。
2. 原始 PLAN v1.1（SHA256 `154f632edc3d6352c6d57a95b7c1b6e8d038aa8782a34d1d571538c791a87ffe`）：见 `docs/plan/v1.1-original/` 完整逐字节镜像及 `reference/plan_v1.1/` 锁定节选，**不可编辑原内容**。
3. `docs/agents/GIT_FIRST_PLAN_v1.2.md`、`docs/agents/GIT_WORKFLOW.md`：只改变交付流程与允许的并行方式，不私自改 130 WBS、114 RQ/TC 或 14 现场 FG。
4. `docs/agents/TASK_LEDGER.json` 为实际已批准 WBS 验收状态；历史 PLAN CSV 中的 `NOT_STARTED` 是只读计划快照，不能当现时状态。
5. GitHub `main` 为当前集成开发树，`RELEASE.json` 和 `FILE_HASHES.json`、`SHA256SUMS.txt` 是**上次正式发布包的快照记录**；下一次 A0 正式发布时才重新冻结/更新它们。日常 PR **不得**用旧 SHA 清单假装变更后的工作树仍为签审过的 M00-r003 ZIP。

## 最小开工阅读
`README.md`、`BASELINE_LOCK.json`、`RELEASE.json`、`docs/agents/OWNERSHIP.json`、`docs/agents/TASK_LEDGER.json`、`docs/agents/GIT_WORKFLOW.md`、`docs/agents/GIT_FIRST_PLAN_v1.2.md`、`contracts/C00_DRAFT.md`、`reference/plan_v1.1/WBS_TASKS_130.csv`、本人 Agent Git 工作令。

## 代码协作
- 使用 `agent/01/Wnnn-topic` 等独立分支 + PR。代码以 **commit SHA + 路径 + WBS + RQ/TC + 真实测试证据**交付；不再用反复传 ZIP 作为默认协作模式。不能跨聊天假定共享本地磁盘；每次从 GitHub 实际读取最新主线。
- **可以在隔离分支提前实现并测试尚未解锁的内容（DRAFT PR）**；但前置 WBS 未 VERIFIED 时，禁止合并、禁止声明正式任务完成。任务准入、合并准入、软件发布、现场许可是四层不同门禁。
- A0 独占 `contracts/`、`libs/domain/`、架构决策、授权所有权和版本发布；A10 独立审查、CI/测试工具；其余所有权按 `OWNERSHIP.json`。跨路径须经 A0 CR + 受影响 Owner 审核 + A10 独立复测。
- **GitHub 当前私有仓库的分支保护/Ruleset 未经证实生效**。任何操作员可绕过软件工程纪律不等于获准绕过 A0/A10 发布检查；不得把自己提交自己审核、CI 缺席写成测试通过。
- 原 `docs/agents/ZIP_PROTOCOL.md` 只适用于 GitHub 无法连接时的封存/灾备回退；其“必须 ZIP 交付”的旧规则已由 `docs/adr/ADR-0005-GITHUB-FIRST.md` 取代。

## 绝不可变的安全/质量约束
- Qt **6.11.2**、C++20、Qt Quick/QML、OpenCV **5.0.0**；AI 按需 OpenVINO **2025.4.1**。离线 SDK `/工具开发` 只读不提交到 GitHub；每台 CI/开发机构建必须重验 SDK SHA/ABI。
- **SIM_ONLY**：不能连接真实 PLC/相机，不启用现场写入；原始 45 PLC 信号全部 `ProductionWriteEnabled=FALSE`，14 个现场 FG 全部 `ProductionWriteAllowed=FALSE`。不能把本机 UDP 健康检查冒充 FINS。
- 工件质量按 `WorkpieceId+Epoch+ReleaseId+ChannelId+CaptureId+AttemptNo` 关联。丢图、过期帧、相同条码、异常结果或身份不确定不能默认 OK。9 台采集设备（7 面阵、2 线扫）对应 11 图通道，线扫原父帧仅存一份。
- `C00_DRAFT.md` 与 PLAN 的 C-07～C-12 编号 **确有冲突**，在 C01 由 A0 统一规范前，各 Agent 不得自创合同字段/编号；见 `GIT_FIRST_PLAN_v1.2.md`。
- 两台 Core、两套 SQLite WAL/资产/日志、两套 Qt UI 和独立 vPLC 必须是真实隔离的五个 OS 进程；结果图、设备地图、报警、PLC 信号和 UI 只能使用真实 Core/DB/模拟链路，不得伪造实时数据。
- 真正可申请 `SIM_READY` 必须过 W001～W122 对应门禁、114 原始 TC 与 52 情景及 Linux/Windows 所需证据；Windows 原生与现场 `FIELD_ACCEPTED` 分开。缺测试一律 `NOT_VERIFIED`。

## A7 / Agent06 人员路由（CR-A0-004，待独立审查）
- 用户已批准新增 **Agent06** 独立负责 A7 的 `MOD-13 SolutionManager` 和 `MOD-14 DeploymentManager`；A7 原设计 Owner、W083～W087 原 WBS DependsOn 均不改变。
- `docs/agents/CHAT_START_Agent06.md` 为专属工作令；**聊天实际启动前仍为 PENDING_NEW_CHAT_START**。任何已有仓库路由记录都不代表功能已实现、CI 已通过或合并许可已发放。
- W083～W087 可在隔离分支准备 Draft/Mock 测试；必须在各自的正式依赖 VERIFIED、C01 公共合同批准、独立 A10 测试及 A0 合并批准后才可进入 main。现场和 PLC 生产写仍禁用。
