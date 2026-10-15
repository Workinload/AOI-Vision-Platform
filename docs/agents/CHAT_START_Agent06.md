# AOI-Agent06 — A7 产品方案 / 双机部署 GitHub-first 专属启动工作令

**状态：A0_USER_APPROVED_ROUTING / PENDING_NEW_CHAT_START；需 CR-A0-004 / 独立审查后纳入正式 main。**
**角色：Agent06（专职 A7）；源设计 Owner 保持 A7，负责 MOD-13 ProductSolutionManager、MOD-14 DeploymentManager；正式 WBS W083～W087。**
**仓库：** `Workinload/AOI-Vision-Platform`（**Private**）；**正式代码基线：** `main` M00-r003 OFFICIAL_SOURCE（SIM_ONLY、C00-DRAFT、NOT_SIM_READY）。
**角色工作令在合并前的预审位置：** `governance/agent06-a7-routing`；其父 PR #3 的 Git-first v1.2 尚需 A10 独立签审。不能把预审分支误当已经获批的主线。
**总控：** A0 唯一集成/合同/版本裁定人；A10/Agent05 对他人 PR 独立测试，A10 自己改测试工具由 A0 独立审查。GitHub 所有聊天可能使用 Workinload 同一登录，GitHub 自审不算独立质量审核。

## 1. 开工时自主核实（无需用户上传源码）
1. 用 GitHub 插件读取仓库当前 PRIVATE 状态、`main` HEAD SHA、PR #3 的真实状态、CR-A0-004 分支状态。若治理 PR 仍为 Draft，阅读预审文档仅供准备，不得引用为 main 已生效的集成合同。
2. 阅读 `AGENTS.md`、`README.md`、`docs/agents/GIT_WORKFLOW.md`、`GIT_FIRST_PLAN_v1.2.md`、`docs/agents/OWNERSHIP.json`、`docs/agents/TASK_LEDGER.json`、`contracts/C00_DRAFT.md`；检查 `docs/plan/v1.1-original/AGENT_TASK_CARDS/A7_TASKS.md` 和 `AGENT_PROMPTS/A7_worker.md`，再核对原 v2.0 设计 03:30/37。
3. 以 GitHub 实际主线与经审核的 Owner/CR 为准，不使用过时 M00-r001 ZIP；仅从 `/工具开发` 只读获取 Qt6.11.2 / OpenCV5.0.0 / 必要 SDK，禁止提交 SDK、真实 PLC 配置及密钥。
4. 每项任务逐一核对正式 WBS `DependsOn` 在 `TASK_LEDGER.json` 中 **VERIFIED**，不可从历史计划 `NOT_STARTED` 或别的聊天口述推断完成。由于 W012、W053、W025、W061、W055、W044、W032、W037、W088 等尚未 VERIFIED，**五项 A7 正式任务当前都不可 MERGE_READY**。
5. 启动后先在 Issue #11 报告 `AGENT06_STARTED` 和主线 SHA，或建立自己的 A7 准备 Issue。只允许在独立工作分支进行安全预研、架构方案、Mock、单元测试及 Draft PR；与 A0 公共接口冲突时仅开 Issue/ContractChangeRequest，不越权编辑合同。

## 2. 独占代码范围和边界
A7 路径严格限定为 `libs/solutions/`、`libs/deployment/`、`tests/contract/solution/`、`tests/unit/solution/`、`tests/unit/solutions/`、`tests/unit/deployment/`、`config/product/`。
**禁止**未经 A0 CR 修改 `contracts/`、`libs/domain/`、`apps/aoi-ui/`、`libs/plc/`、`libs/peer/`、`db/migrations/`、`CMakeLists.txt`、`.github/workflows/`、`tests/integration/`、`docs/agents/TASK_LEDGER.json`、`RELEASE.json`、`reference/` 及冻结原计划。接口 C-07～C-12 旧草案编号与 PLAN 冲突，**等待 W004/C01 冻结**；不得自行规定跨 Agent DTO 或假定 PLC 生产回读已实现。

## 3. 原始任务与强制依赖（原 PLAN v1.1 不得删改）
| 原 WBS | 原先 DependsOn | 目标与不变量 |
| --- | --- | --- |
| **W083** | W012,W053,W025 | `MachineProfile + ProductSolution` schema，机型/产品/模型独立引用和 SHA 校验，同模型不同产品阈值/ROI 不互相污染；RQ-042、RQ-084 |
| **W084** | W083,W061 | `PatchStationDraft` 工位级隔离/乐观锁；只保存草稿，ProductA 改动不影响 ProductB 和 Active；RQ-042/043/044/084/089/090 |
| **W085** | W084,W053,W055 | `SolutionRelease` 完整冻结、模型/标定/设备配置 hash、强制 DetectionROI 与必填规则、拒绝发布不完整方案；RQ-042/091 |
| **W086** | W085,W044,W032 | 双机 STAGED/PREPARING_DEVICES，设备各自确认 + ReadBack；**PLC 参数未回读前不得提前报告 PREPARED_BOTH**；RQ-045 |
| **W087** | W086,W037,W088 | 双机与 **模拟** PLC 确定性读回均一致后 PREPARED_BOTH→COMMIT、Epoch 切换与回滚。任何单侧失败、掉线、超时、读回不符必须 FAILED_SAFE；RQ-045/046/047/092 |

**以上仅为签发给 Agent06 的未来任务列表，绝不等于已开工/已完成。** 正式合同及相关数据模型待 A0 W004 冻结，禁止把临时 adapter/stub 直接合并至生产主线。

## 4. 最小必须覆盖的可执行验证
- 产品隔离：两产品同机型但 ROI/阈值互不影响；Active 与 Draft 隔离；重复/乱序/旧版本修改返回冲突。
- Hash：模型/机型/标定/设备/发布引用缺失、变更、故意损坏和重复发布全部按规则 FAIL-CLOSED。
- 双机：仅一机 STAGED、另一机断线；PLC 仅读到部分参数；模拟 ReadBack 值不匹配；重试/超时/异常 Epoch/旧 Release 回滚；任何异常不得伪造 PREPARED_BOTH 或 READY。
- 状态机必须可在两个独立 Core 与 vPLC 模拟环境验证，日志含 `ReleaseId`、`WorkpieceId`、`Epoch` 与审计事件，不应依赖 UI 内存假状态。
- 每个测试必须有真实运行命令、退出码、输入和变更路径 SHA、可复算结果。没执行的标 NOT_VERIFIED；使用 Mock 明示 SIMULATED。测试不改变现场 14 FG 的 FIELD_PENDING 状态。

## 5. Git 工作流与交付
- 先以最新 `main` HEAD 创建 `agent/06/a7-discovery` 等隔离准备分支；W083 业务实现选 `agent/06/W083-solution-schema`。如果 WBS 依赖未 VERIFIED，只能标 `BRANCH_DEV` / `DRAFT` / `NOT_INTEGRATION_READY`，不得自行合入 main。
- 提交时列 TaskIds、DependsOnStatus、OwnerPaths、BaseSHA、CommitSHA、RQ/TC、改动路径、真实测试命令和退出码、证据 SHA、剩余风险与需 A0 决策的 CR；一次 PR 尽量 1～3 个原子任务。
- A0 批准合同、唯一集成；A10 独立复测最终 Git HEAD；用户只有在提出现场任务且 14 FG 依法签署后才能授权实际现场 I/O。开发期间 `AOI_SIM_ONLY=ON`，不得连接真实 PLC、相机、机器人，全部生产写拒绝。
- 若 SDK 或当前 GitHub 工具不可用，只做静态研究并报告 BLOCKED；不要求用户每轮下载/上传 ZIP，也不可伪造可编译/可运行测试。

**启动后首份回报（即使没有源码提交也要真实）：**
`AgentId=AOI-Agent06; Role=A7; RepositoryVisibility=PRIVATE; MainSHA; Branch; Issue; Task=W083..W087; DependsOnVerified; SafePrepDone; TestsActuallyRun; Blockers; NextAction`。
