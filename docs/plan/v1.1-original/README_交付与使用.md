# 双机 AOI 多Agent完整开发 PLAN 交付说明

**计划版本** v1.1 · **日期** 2026-10-15 · **状态** `PLAN_RECHECKED_INDEPENDENTLY / IMPLEMENTATION_NOT_STARTED`。

本 ZIP 是可直接分给多个 Agent 的**开发执行计划**，不是编译好的 AOI 软件。正式开发目标是全部离线业务功能与双机仿真交付；真实 PLC/机器人、硬件 SDK、模型工艺精度和现场验证保留在独立后续门禁。

## 先看哪三个文件

1. `PLAN.md`：总开发目标、技术架构、10个离线阶段、具体门禁、Agent 职责、协议冻结、测试矩阵、协作与交付要求。
2. `AGENT_STARTER.md`：复制给总控 Agent A0 的开工指令；每个 Agent 对应完整工作令在 `AGENT_PROMPTS/`。
3. `WBS_TASKS_130.csv`：130条 WBS，包含 Owner、Reviewer、先决依赖、验收、证据、状态；配套 `RQ_114_COVERAGE.csv` 和 `MODULE_21_OWNERS.csv` 用于追溯。

## 文件布局

```text
PLAN.md                         # 总计划
AGENT_STARTER.md                # 总控开工工作令
SIMULATION_CONTRACT.md          # 五进程/模拟通讯/SDK/图像/故障输入契约
AGENT_BOOTSTRAP_DEPS.md         # 外部Agent获得正式v2.0源包/SDK依赖的交接要求
CHANGE_REQUEST_TEMPLATE.md     # 跨Agent合同变更模板
CONFIG/sim_profile.example.json # 仅模拟的示例配置，不能直接控制PLC
WBS_TASKS_130.csv              # 122个离线开发+8个未来现场任务
RQ_114_COVERAGE.csv             # 114项需求→WBS/测试/责任人
MODULE_21_OWNERS.csv            # 21模块Owner
FIELD_GATES_14.csv              # 14项现场未决，全部禁生产写
SCENARIO_MATRIX.csv             # 52个场景/断言/必留证据
UI_STAGE_INTEGRATION_MATRIX.csv # 10项UI骨架→正式后端的真实完成阶段
DEPENDENCY_WAVES_37.csv         # 37层拓扑就绪顺序（WBS改变后须重新生成）
EXIT_GATES.csv                  # G00-G09 + FIELD 门槛，全未运行
RISK_REGISTER.csv               # 18个高/重大项目风险
AGENT_OWNERS.json               # 工作域/独占代码路径
AGENT_PROMPTS/A0_lead.md        # 总控 Agent
AGENT_PROMPTS/A1..A10_worker.md # 10个工作/测试Agent
AGENT_TASK_CARDS/A0..A10_TASKS.md # 每Agent自己的完整WBS任务卡（含证据）
REVIEW/ROUND1_结构与需求审核.md
REVIEW/ROUND2_语义安全与交付独立审核.md
REVIEW/ROUND3_独立复判_审核报告.md
REVIEW/audit_round3_independent.json
REVIEW/audit_round3_sensitivity.json
REVIEW/audit_round1_structure.json
REVIEW/audit_round2_semantics.json
tools/audit_round1_structure.py
tools/audit_round2_semantics.py
tools/audit_round3_independent.py
tools/test_round3_sensitivity.py
tools/rebuild_wbs_derived.py
tools/generate_matrices.py
BASELINE_LOCK.json
SHA256SUMS.txt
```

## 原两轮计划审核 + 第三轮独立深度复判

- 第1轮：发现并修复了未来阶段阻塞前期任务、Wine打包先后颠倒、发布前读回链缺失、部分边缘需求未明列等；修复后**40/40通过**。
- 第2轮：独立检查模拟权限隔离、完整双机、工件不串号、CamDone/OK区分、11通道/两线扫、崩溃恢复、方案回滚、GUI真实性、交付真实性；**33/33通过**。
- 第3轮：重新检查双机首个端到端依赖、主从方案Prepare/PLC读回、UI后端接线阶段、开发权限默认拒绝、代码路径归属、Windows QtQuick/QML发布、场景截止顺序与生成数据一致性。修复16类计划缺口，**53/53通过**；故意改坏6种临时副本，新脚本**6/6成功报错**。详见 `REVIEW/ROUND3_独立复判_审核报告.md`。
- 计划检测状态 `PASS` **不能解释为软件功能 PASS**：52个模拟场景当前 `NOT_RUN`，114条需求测试 `NOT_RUN`，14项现场门禁未关闭。

## 复跑计划检查（Python 3，无外网）

在本文件所在目录执行：

```bash
python3 tools/audit_round2_semantics.py
tools/audit_round3_independent.py
tools/test_round3_sensitivity.py
tools/rebuild_wbs_derived.py
python3 tools/audit_round1_structure.py /path/to/AOI_新一代上位机_开发设计全书_v2.0_20261015
sha256sum -c SHA256SUMS.txt
```

第三轮审计和反向敏感性测试需要原v2.0展开目录及`BASELINE_LOCK.json`里锁定的三份输入源文件。结构审核可使用 `v2.0` 原 ZIP 的展开目录；无展开基线时 `python3 tools/audit_round1_structure.py` 仍可校验本计划内部索引，但缺少源表对照检查。命令用于**审核计划文件**，不执行未来软件模拟，也不会访问或写入 PLC。

## 云端目录权限

`/AOI开发项目` 是设计基线/旧机只读证据，`/工具开发` 是 SDK/模板只读资源。把本 ZIP 解压到独立新工作目录创建源码仓库后再交给 Agent，禁止覆盖以上两个目录。默认不修改用户任何云端资源。


## v1.0与v1.1的使用顺序

从本包执行时以 **v1.1 WBS / SCENARIO_MATRIX / UI_STAGE_INTEGRATION_MATRIX / 第三轮审核报告** 为唯一有效任务表；v1.0的两轮文字报告保留作为发现缺口前的历史，不能覆盖修订后的先决依赖。外部Agent没有源ZIP/SDK访问权限时先按`AGENT_BOOTSTRAP_DEPS.md`补齐只读输入。
