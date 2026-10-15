# 新版 GitHub-first 启动中心（代替 M00-r001 ZIP 交接工作令）

当前唯一 GitHub 主线：`Workinload/AOI-Vision-Platform`（PRIVATE），Git 默认分支 `main`。上一份已正式发行源码：`M00-r003 OFFICIAL_SOURCE`，并非 SIM_READY。不要再从历史 `M00-r001_SOURCE.zip` 启动正式开发，也不要把任何暂存 ZIP 的旧 BaseSHA 当作 Git 最新基线。

## A0 与五个 Agent

- **A0 总控**：唯一架构、公共合同、任务登记、PR 合并/正式发布裁定；A10 独立审核。
- **Agent01（A1+A5）**：Core 调度、工件 ID/Epoch 和 PeerLink；读 `CHAT_START_Agent01.md`。
- **Agent02（A2+A3）**：虚拟 PLC FINS、采集/相机/线扫模拟；读 `CHAT_START_Agent02.md`。
- **Agent03（A4+A6）**：算法/ROI/Replay、SQLite/无损资产/统计；读 `CHAT_START_Agent03.md`。
- **Agent04（A8+A9）**：Qt QML UI、设备地图、权限/报警日志；读 `CHAT_START_Agent04.md`。
- **Agent05（A10）**：独立 QA、CI、故障注入与部署；读 `CHAT_START_Agent05.md`。
- **A7 缺口必须明确处理**：方案 `MOD-13`、部署 `MOD-14`，原 PLAN Owner=A7 且有 5 项 WBS。未来可由独立 Agent06 承担；如果 A0 代做，须单独 CR、受限路径、A10 独立复核。不能当做已经完成或偷偷分配给 Agent04。

## 工作准入

1. 各聊天首先用已连接 GitHub 插件实际读取仓库 `main`、`AGENTS.md`、本人工作令、`OWNERSHIP.json`、`TASK_LEDGER.json`，记录 **commit SHA**。不能假设其他聊天临时目录互通或以前解压包仍为最新。
2. W001 在已发布 M00-r003 中为来源范围 `VERIFIED`；按照当前 ledger 原始 `DependsOn`，当前可推进的正式任务只有 **W002/W003/W005/W007**。其他任务允许隔离研究、Mock、DRAFT PR，但**不允许破门合并与认证**。
3. 进入仓库 Issue 认领任务；分支 `agent/01/W014-core` 这种唯一命名，不在 `main` 直接编辑。PR 包含 TaskId、Owner、依赖、变更、真实命令/退出码/证据、风险、是否满足合并门禁。
4. A10 独立复查后，A0 决定合并与 ledger 状态。缺检查、离线 SDK、Windows 或物理设备则写 `NOT_VERIFIED`；日志不能凭空补写。
5. GitHub 无连接时，**停止影响 main 的提交**，允许本地临时代码研究；使用 `ZIP_PROTOCOL.md` 的封存回退必须经 A0 明确批准，不回到旧默认手工搬运。
6. v2.0 规格 `/AOI开发项目`、SDK `/工具开发` 保留只读，禁止推送旧 EXE、现役 PLC 工程、SDK 或机密数据。

完整工作流：`GIT_WORKFLOW.md`；修订计划：`GIT_FIRST_PLAN_v1.2.md`；原 WBS/RQ/TC/Gates：`docs/plan/v1.1-original/`（只读）。
