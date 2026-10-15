# AOI Git-first 多 Agent 执行总计划 v1.2（v1.1 的增量修订，不重写 130 项 WBS）

**状态：DRAFT_FOR_A10_REVIEW。** 本文件不自动改变已发行 `M00-r003 OFFICIAL_SOURCE` 的 W001～W130 任务状态，不代表系统已完成开发。

## 0. 原始需求必须保留

- 双机同一份 Core 二角色、双 UI + 独立 vPLC 五进程；Master/Slave 真 TCP PeerLink、可靠 outbox/inbox、独立 WAL/资产/日志、IPC、FINS/UDP vPLC；生产写默认禁用。
- 7 面阵 + 2 套线扫 = 9 个物理采集设备；CH01 定位 + CH02..CH11 十路缺陷 = 11 个结果通道；CH08/09 共享反面父帧、CH10/11 共享正面父帧；线扫静态 8200×15000×8bit（123 MB）真实压测、旧采样/旋转规范必须测试。
- Qt6.11.2 + C++20 + Qt Quick/QML、OpenCV5.0.0；OpenVINO2025.4.1 按需真实加载，MockInference 永远注明 SIMULATED；无真实电池图集不得宣称工艺准确率。
- 2K（2560×1440）和 1080p（1920×1080）适配、苹果式浅色现代主屏 **11 结果 + 第12格整料 4×3**、完整导航/设备游戏地图/PLC信号参数表双向联动；UI 数据必须来自真实 Core、DB 和事件，不得硬编码“运行正常”。
- 逐料身份、条码/定位/机器人/复拍/超时/同码不同件、C01 强类型状态、最终判定 HOLD/NG/OK 错误隔离；原图无损/结果图与重算 overlay/历史图集、统计口径、日志审计、双人审批、一键安全换型、离线回放、故障注入。
- 原始 114 RQ、114 TC、130 WBS、37 依赖波次、21 模块、52 场景、10 UI 项、11 退出门禁和 14 FG 继续是验收源。现场所有 FG 未签、45 PLC 生产写禁用，不得从模拟系统生产执行。Qt 头less 中文方框问题是未来 UI 额外检查项。

## 1. 采用 GitHub，不再用 ZIP 当日常交接

- 唯一源码 `Workinload/AOI-Vision-Platform/main`，一个版本一次 A0 发布裁定；Agent01..05 以及经 CR-A0-004 独立路由审核并完成新聊天启动后的 Agent06 在独占分支开发，提交 Draft/Ready PR，A10 独立复测，A0 审核合并。每次读当前 Git commit，不依赖旧聊天本地路径。
- 原先的 5 个 `CHAT_START`、`AGENTS.md`、`START_HERE` 和 ZIP 接口全部由此次治理 PR 更新。各 Agent 最多需要在**现有对话**接收一句话，自动读取仓库新工作令；无需每次人工贴所有原始文件。
- WBS 原文件内 `NOT_STARTED` 是历史快照；真实当前状态以 A0 `TASK_LEDGER.json` 为准。不能因此冒充其他 129 项完成。
- **四类授权**：BranchDev（允许安全 Mock/接口预研）→ MergeReady（WBS 前置 VERIFIED+owner+合同+单测+独立 A10）→ ReleaseReady（签审确定的 source ZIP/CI/门禁）→ FieldAccepted（现场 14 FG+W123..130 签字）。

## 2. 目前真正解锁的工作与先后关系

| 工作批次 | 对应原 WBS | 动作与交付 | 合并门禁 |
|---|---|---|---|
| A | **W002 / W003** | A0 核对 45 PLC、14 FG、21 模块与 A7 路由，解决 `AGENT_OWNERS.json`/实际代码 ownership 不一致 | A10 独立检查后 VERIFIED |
| A（并行） | **W005 / W007** | Agent05 真正核实 RQ/TC/场景到可执行测试的映射与 Windows QtQuick SDK/ABI | 只能按实际证据批准，Qt Windows 预构建不等于 native |
| B | **W004** | A0 正式冻结 C01 强类型 IDs、消息/FrameRef/DB/资产/Peer/安全合同及正反合同测试 | W003 VERIFIED 且编号歧义全部关闭 |
| B | **W006 / W008 / W009 / W010** | A2 PLC 45 信号与 14 FG，A9 威胁模型，A10 性能 SLO未知列表，A0 PR 合同治理 | 各任务原 `DependsOn` 必须 VERIFIED |
| C | **W011 / W012 / W013 / W014 / W015 / W018 / W022** | Agent05 CI+双平台 SDK 可构建、A0 域模型、Agent01 两 Core、Agent04 两 UI、Agent03 双 WAL，接上真 IPC | W011 需 W007+W010；其他各守原依赖，功能完整才签 |
| D | **W023..W034** | vPLC FINS+9 相机接口 Mock、帧所有权/123MB 资源、simctl deterministic | G02 真报文/读回/触发，不是健康 UDP |
| E | **W035..W067** | 完成逐料/PeerLink/11 路处理/资产/独库/第一个 E2E | W049 包含 W051/58/60/61/62/63 等真实前置成果 |
| F | **W068..W095** | 真 Qt 4×3 主屏/地图+表/ROI 及方案双机换型/权限报警 | 按 UI 10 项分别在 G05、G06、G07 封版，禁止静态截图冒充 |
| G | **W096..W114** | 离线 Lab/统计/检索、52 场景故障注入、114 TC 逐一复验 | 需要逐条机器证据 / QA 真实退出码 |
| H | **W115..W122** | Linux+Windows x64 真机运行/依赖闭包、Wine 补充、正式发布与 SIM_READY | G00..G09；Win native NOT_VERIFIED 时不得冒充通过 |
| FIELD | **W123..W130** | 现场 PLC/相机/机器人/工艺安全签收 | 所有现场 FG / 有权人员签字，仅用户决定进入 |

**不改旧 DAG**：所有任务的 `DependsOn` 和优先级以 `docs/plan/v1.1-original/WBS_TASKS_130.csv` 为准；若将来需要解除正式集成依赖，必须另立 `ChangeRequest`，经过 A0 + A10 审核、版本化派生 WBS，不得悄悄删前置。

## 3. 工作分配（保留 Owner 定义，补 A7 缺口）

| Worker | 原职能 | 模块 / 核心任务 |
|---|---|---|
| Agent01 | A1+A5 | Core 调度、逐料身份/Epoch、TCP PeerLink durable ACK |
| Agent02 | A2+A3 | FINS/PLC 模拟与安全网关、7+2相机、FrameRef/线扫 |
| Agent03 | A4+A6 | 算法/ROI/Offline Mock + SQLite schema/资产/数据统计 |
| Agent04 | A8+A9 | QML 2K/1080 UI、设备信号图、权限/报警/审计 |
| Agent05 | A10 | CI/构建、Windows SDK/ABI/插件闭包、真正五进程 E2E、114 TC、52 场景与安全回归 |
| A0 | A0 总控 | C01 合同、source lock、Issue/PR/分支监督、任务与 release ledger、统一合并 |
| Agent06（获准分配，待聊天启动） | A7 | MOD-13 SolutionManager / MOD-14 DeploymentManager；W083..W087，原 Owner/DependsOn 不变 |

用户已同意新增 Agent06 承接 A7；由 `CR-A0-004` 记录路由授权，独立新聊天实际启动前标为 `PENDING_NEW_CHAT_START`。五项 WBS 仍全部 `NOT_STARTED` 且 DependsOn 未满足，不能因分配角色而改为 VERIFIED；正式合并前需 A10 独立 QA 和 A0 决定，绝不默认分配给 Agent04。

## 4. C01 合同冻结是技术核心阻断

当前源 `contracts/C00_DRAFT.md` 的 `C-07...C-12` 与 PLAN v1.1 §4.2 **编号及业务含义冲突**，例如：

| 编号 | 原 PLAN v1.1 §4.2 | 当前 C00 草案 |
|---|---|---|
| C-07 | IAssetStore / metadata / 无损 codec (A6) | SQLite asset/events transaction (A6) |
| C-08 | DB migration / Outbox-Inbox / Query (A6) | ProductSolution (A7) |
| C-09 | Vision Algorithm / ROI (A4) | UI snapshots + RBAC (A8/A9) |
| C-10 | ProductSolution / Deployment (A7) | Alarm/Log/Audit (A9) |
| C-11 | UI Read API (A8) | Vision pipeline (A4) |
| C-12 | AuthAudit / AlarmEnvelope / Telemetry (A9) | Simulation scenarios/evidence (A10) |

**决策建议**：在 W004 的新 `contracts/C01/` 规范中统一采用 PLAN 的 C-01～C-12 编码；旧 C00-DRAFT 仅为历史对照，禁止继续将旧编号当机器 contract_id；对所有 DTO、JSON Schema、IPC、Peer、DB、状态机、模拟 fixture 编写正反合同测试并准备兼容矩阵。正式冻版前任何子 Agent 不得互相猜字段。

必须额外提交 A0/A6 ADR：`RAW_FRAME` 父资产 `capture_group/device` 唯一约束、`AVAILABLE` 资产 URI/hash 非空与崩溃恢复；站级结果 `ProcessingStatus`/`QualityStatus`/数据 SQL 对齐。Agent03/A10 已发现真实设计漏洞，不能直接照旧 SQL 创建将来生产表。

## 5. GitHub PR、CI、产权与审查

- 所有变更必须走 `docs/agents/GIT_WORKFLOW.md` 规定的所有权矩阵、变更请求、A0 merge + A10 独立复核；A10 自己提交的测试工具由 A0 审查，必要时由另一独立轮复测。
- A0 只修改自己的架构/治理路径；Agent05 才能更新 `CMakeLists`, `.github/workflows/`, `tests/integration/`, `tests/patch/` 和测试脚本；共享路径变更需要 CR。
- 当前 GitHub 私有仓库 Rulesets GET 403 提示升级 GitHub Pro，不声称主线“技术上不可直推”；必须优先落实工程纪律。`CODEOWNERS` 只给 `Workinload` 实际账户，无法把 A0 与 A10 两个聊天变成两个 GitHub 审核人。
- CI 分层：①任何 PR Python 标准库静态源码锁与 owner/contract 状态检查；②实际 Qt 6.11.2/OpenCV 5.0.0 Linux 构建 CTest + IPC/QML 负测；③Windows x64 原生构建/QML plugin/模型 smoke；④A10 逐场景独立 QA。无 SDK/runner 时由 CI 报 `NOT_VERIFIED/FAIL`，不准假绿色。CI A10 实现见审核任务。

## 6. “能否完美满足”的客观门槛

此计划解决“设计覆盖”和“协作流程”，不能保证机器尚未实现的功能无缺陷。**满足全部离线开发要求只能通过可重现行为证据证明**：130 WBS 适用任务真实完成、114 TC 实测、52 场景执行、G00～G09、Qt Quick 真 UI 11+1 及地图、主从角色隔离/帧不可串件/缺图不 OK、失败补偿、资产校验、Windows 原生部署。真实 PLC/相机/机器人与标定/精度/节拍还须 W123～130 及 14 项现场签字。

此计划草案如 A10 审查失败则只记录风险，不改正式主线。下一轮优先**W002/W003/W005/W007、W004 C01、W010 Git PR 工作方式**，不得先大量合并尚未解锁的功能分支。
