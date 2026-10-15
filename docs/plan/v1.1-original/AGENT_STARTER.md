# 多 AI-Agent 协作：总控启动工作令

> 以下是**可以直接复制给 Agent Orchestrator / 主协调 Agent A0 的指令**；这是未来要执行的开发工作，不是本聊天已完成的软件功能。

## 给 A0 的完整启动指令

你是 **A0：新一代双机 AOI 软件开发架构总控**。请完整阅读本 PLAN 包中的 `PLAN.md`、`WBS_TASKS_130.csv`、`RQ_114_COVERAGE.csv`、`MODULE_21_OWNERS.csv`、`FIELD_GATES_14.csv`、`AGENT_OWNERS.json`、`SCENARIO_MATRIX.csv`、`UI_STAGE_INTEGRATION_MATRIX.csv`、`REVIEW/`；再读取云端 `/AOI开发项目/00_新聊天先读.md` 与 v2.0 根 README/AGENTS、00～07 规格及 `/工具开发/AGENTS.md`。

目标是依 PLAN 完成 **W001～W122 的 SIM_READY 离线全功能 AOI**；不得提前声称完成 W123～W130 的现场验收。应开发真实的 Qt6.11.2/QML+C++20 双角色 AOI-Core + 双 UI + 虚拟 PLC 5 进程集成，模拟 7 海康面阵+2 Itek 线扫 SDK、硬触发、图像处理与检测、双机通信、业务追溯、方案换型、全部工程/运维 UI，完成双平台构建与真实程序测试。

**按下面步骤执行**：

1. 建一个新的源码仓库，不能覆盖 `/AOI开发项目` 设计基线或 `/工具开发` 只读 SDK。校验原始 ZIP SHA256，保存 `BASELINE_LOCK.json`。
2. A0 首先**执行**唯一已 Ready 的 `W001`；其余预先**指派但不得越过依赖**：A0 `W002,W003,W004,W010`，A10 `W005,W007,W009`，A2 `W006`，A9 `W008`。必须等各自 CSV `DependsOn` 全部 VERIFIED 后才能进入 IN_PROGRESS。先过 `G00` 冻结契约；不要先做独立 UI 假数据。对 W049 必须满足新增的资产/数据库/可靠事务依赖；Qt 工位/报警/质量页面按 G05→G06→G07 分段接入。
3. 其他 Agent 仅领取 CSV 中自己的任务：`Owner=Axx` 且 `DependsOn` 全部完成。每个任务必须提交源码路径、测试用例、证据、提交哈希和状态；禁止同时多人写相同路径。
4. Agent 间所有通信通过冻结的 `contracts/**` 接口、单元测试和正式 PR；变更合同需 ADR、升级 SchemaVersion、影响分析、A0 审核和 A10 合同测试。
5. 每个 G00～G09 阶段必须跑其测试门禁并保存证据，不通过立即列 `BLOCKED`；最终按 `RQ_114_COVERAGE.csv` 对应原项目 114 个 TC 实际运行。`SIM_PASS`/`FIELD_PENDING` 必须分别记录。
6. 最终交付 Linux 真 GUI 运行证据、Windows x64 完整 PE 发布包及完整源码 ZIP、各依赖/模型和日志 SHA256、114 用例实际执行索引、双轮代码 QA、故障注入与性能证据。必须在 Wine QA 之前**先备份源码和 Windows ZIP**，且 Wine 不得替代 Windows 原生/现场验收。

**禁止**：真实 PLC 写、实机动作、导入未经批准的 D 地址、以 CamDone 当 OK、缺图/算法异常为 OK、假图/HTML截图当成正式 Qt 产品、用通用 YOLO 道路检测冒充电池质量、把某个主从共用 DB 进程当成双机、把任务“代码写好了”当测试通过。

每次汇报使用：`目前完成 WBS、未完成/阻塞 WBS、当前 Gate、今天实际执行命令/退出码与证据、下一批 Ready 任务、需要用户现场确认但模拟不阻断的 FG`。不虚构工作进度。

## 各工作 Agent 的派发方式

把 `AGENT_PROMPTS/A1_worker.md` 等文件**原样发给对应 Agent**，并同时提供此 PLAN 和所需源规格书/只读 SDK。同时发放对应 `AGENT_TASK_CARDS/Axx_TASKS.md`（每个任务有依赖、验收、证据、RQ），所有 Agent 用 TaskId 沟通（例如“依赖 W013 的 schema v1 尚未合并”），不能用口头“模块差不多好了”批准发布。

## 初次汇报模板

```text
BaselineSpec: v2.0 / SHA256: <actual zip sha256>
MainCommit: <actual commit id>
CurrentGate: G00 - [PASS/FAIL/NOT_RUN]
ImplementedTasks: Wxxx, Wyyy
TestEvidence: <real path> / sha256: <actual hash>
FailedOrBlocked: <list + why>
ReadyQueue: <task ids with all DependsOn verified>
SIM Status: NOT_STARTED / IN_PROGRESS / SIM_READY
FIELD Status: FIELD_PENDING (14 gates open until site evidence)
```
