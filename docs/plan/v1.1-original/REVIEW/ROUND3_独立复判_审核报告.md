# 第三轮独立复判｜执行可行性、业务断点与多Agent协作核查

**计划版本**：v1.1（2026-10-15）  
**审查对象**：原v1.0多Agent计划ZIP、全部130条WBS/114条需求映射、模拟契约、Agent工作令、场景/门禁/依赖表，及其修订包。  
**审查性质**：PLAN级审查，而非未来AOI软件的实际编译/运行、实机PLC/工业相机联调或认证。

## 一、重要结论

**原v1.0的“两轮全部通过”是真实的计划规则检测结果，但不足以证明可以无阻塞地直接分给多个Agent执行。** 两个旧审核程序主要检查ID、字段、关键字、文件存在和一般DAG无环；它们没有拦截下列跨业务依赖、分阶段功能未接线、双机Prepare时序和代码归属冲突。因此本轮不沿用“原计划毫无问题”的说法，进行了实质修改，独立产出v1.1。

**已执行的计划层面检查**：

- 重新对照 `/AOI开发项目` v2.0原始114需求/114验收用例、21模块和14现场门禁：一致；原始v2 ZIP、Qt示例模板ZIP、SDK Manifest三个SHA256与计划锁文件一致。
- 原结构审核再次运行：**40/40 PASS**。原语义关键词审核再次运行：**33/33 PASS**。
- 新写第三轮独立深度审核：**53/53 PASS**。额外进行6种故意破坏计划临时副本的反向敏感性测试：**6/6成功识别**，说明至少所列关键错误可以被新审核程序拦住。
- 经修订的WBS依赖仍**无循环**，且保留37层就绪波次、唯一首个READY任务W001、122项离线+8项未来现场任务。
- 新增8种模拟异常场景，故障/验证场景由44→52条；新追加UI阶段交接矩阵10条、风险登记15→18条。

## 二、查出的原始计划问题与修复证据

| 复判编号 | 级别 | 原v1.0缺口 | v1.1修复与验收入口 |
|---|---|---|---|
| R3-01 | 严重 | W049“首次完整E2E”可早于StationResult位图生成、线扫父资产/两库结果、Outbox同事务和追溯查询完成 | W049新增 W051/W058/W060/W061/W062/W063硬依赖；G04必须用图像hash、两机SQL和网络轨迹验收 |
| R3-02 | 严重 | W086可提前报告“双机Prepared”，而W088虚拟PLC产品尺寸读回晚于W086 | W086只到`STAGED/PREPARING_DEVICES`；W088再做PLC读回；W087负责`PREPARED_BOTH→COMMIT`和失败补偿 |
| R3-03 | 严重 | S05报警页、完整质量页、工位“保存”入口比真实Alarm/统计/方案草稿后端提前 | 调整W075/W078/W079为真实Core数据的受限只读页面，加入10条`UI_STAGE_INTEGRATION_MATRIX`；W094/G06打通草稿和报警，G07再验完整统计 |
| R3-04 | 严重 | W016 UI IPC命令先出现，W089 RBAC到S06才实现，早期修改命令缺“默认拒绝”硬约束 | W016明确无RBAC阶段只能安全只读；未知/变更IPC全部拒绝，W089后台鉴权后方逐项开放；增加SC-046负测 |
| R3-05 | 高 | `apps/aoi-core/`总图归A0，W014由A1开发；根CMake和场景目录缺单一明确Owner | 把Core装配统一交A1，CMake/scenarios/config/sim交A10；同步改`AGENT_OWNERS.json`、PLAN、11份工作令 |
| R3-06 | 高 | SC-001/002/014计划截止阶段早于关联任务实施阶段 | 纠正RelatedTaskIds与DuePhase；SC-017引用MockInference前移到S04；第三轮脚本逐条验证DuePhase>=所有关联任务Phase |
| R3-07 | 高 | 发布只强调Qt平台/OpenCV/OpenVINO，未明确Windows Qt Quick/QML运行插件闭包，而可复用样板实际是Qt Widgets | W007/W011先核查Qt Quick/QML/QuickControls并编译最小QML；W117/W118要求干净Win包Qt Quick双UI实际启动及QML import诊断；增加SC-048 |
| R3-08 | 高 | 同seed字节级事件hash如果包含UTC/PID/目录和非确定线程调度会不稳定 | W034+模拟契约明确规范化业务摘要并用因果屏障同步；增加SC-050检验改变UTC/PID后的摘要稳定性 |
| R3-09 | 高 | 模拟器可凭虚构Slot/TriggerSequence匹配，但没有充分强调这些信号不代表现役PLC接口 | W036+模拟契约明确`SIM_ONLY_SYNTHETIC`证据，真实硬件仍需FG/现场核对，身份歧义进入HOLD |
| R3-10 | 中 | 原W058偏结果字段，产线真正“结果图位图+overlay+落库”交付链表述不足 | W054/W058/W059分别要求OpenCV实际渲染、结构化overlay及原/结果双资产关联；W067只从Core获取真实结果图 |
| R3-11 | 中 | W045主从业务结果传递未依赖耐久Outbox/Inbox完成 | W045硬依赖W043，W049在业务结果和Outbox同事务完成后方通过 |
| R3-12 | 中 | W033五进程任务缺UI↔Core IPC、独立数据库和网络安全防线先决关系 | 增W016/W018/W021依赖，检查双机进程/端口/DB的真实分离 |
| R3-13 | 中 | 模拟和现场测试状态名在W104与PLAN不一致；部分RQ SupportingWBS重复引用自己PrimaryWBS | W104统一SIM/Field双轨状态，RQ-061/114清理Primary自引用并补足跨模块辅助映射 |
| R3-14 | 中 | 原44场景缺关键的双机崩溃Commit、QtQuick插件缺失、早期未授权IPC、现场门禁不自动解除 | 新增SC-045至SC-052共8个机器可判断言场景，扩展风险登记到18条 |
| R3-15 | 中 | 跨项目外部Agent未必能访问两个ChatGPT云端文件夹，原交接可能导致用未知来源资料替代 | 新增`AGENT_BOOTSTRAP_DEPS.md`：要求提供同哈希v2原始包与所需SDK只读拷贝、不得猜值/覆盖原文件 |
| R3-16 | 中 | 原两套审计未验证这些危险前置与字段语义，即使有问题也会“全通过” | 新增53条可复跑独立核查和6种故意改坏副本的验证，未来每次修改WBS先重建waves/cards，再运行审核 |

## 三、审核范围与边界

已核实的是：历史设计源文件、计划本身的依赖/责任/状态/测试场景语义、三份关键参考源文件SHA256、计划衍生CSV/Agent卡与生成器的一致性、计划中保留的安全门禁。

**尚未做、不能由计划审核推断通过的内容**：Qt 6.11.2 Windows离线SDK内是否实际含全部Quick/QML组件（W007需拆包验证）；AOI新代码编译/GUI真实启动；5进程双机/vPLC实际通信；9个模拟相机与11路图像流水线；资产压缩回读/数据库断电恢复；Windows QtQuick本机运行/Wine真实测试；114个AOI原验收用例与52个故障模拟实际执行；全部14项实机现场门禁。它们在计划中仍是`NOT_RUN/FIELD_PENDING`。

**可以把v1.1作为更严格的多Agent任务执行起点**；不能把53个静态审核PASS冒充SIM_READY或FIELD_ACCEPTED。真正签收必须按G00–G09逐项保存真实源代码、命令、输入数据、可复验日志/SQL/图像hash、截图和硬门禁证据。

## 四、复核复跑命令

```bash
python3 tools/rebuild_wbs_derived.py --check
python3 tools/audit_round1_structure.py /path/to/AOI_新一代上位机_开发设计全书_v2.0_20261015
python3 tools/audit_round2_semantics.py
python3 tools/audit_round3_independent.py --spec-root /path/to/AOI_新一代上位机_开发设计全书_v2.0_20261015 --reference-dir /path/to/reference_inputs
python3 tools/test_round3_sensitivity.py --spec-root /path/to/AOI_新一代上位机_开发设计全书_v2.0_20261015 --reference-dir /path/to/reference_inputs
sha256sum -c SHA256SUMS.txt
```

上面的 `reference_inputs/` 需含 `BASELINE_LOCK.json` 锁定的3个实际文件：v2.0完整ZIP、Qt示例模板ZIP和SDK_PARTS_MANIFEST.json。所有审核仅操作计划与临时副本，不连接现场PLC或相机。敏感性验证故意破坏的是临时副本，绝不会覆写正式原计划或两个云端只读资源库。
