# A0 双轮 GitHub 迁移架构/需求审计报告 — 2026-10-15

**结论：REPLAN_REQUIRED，但原 v2.0 技术与业务目标可保留；不能保证最终“完美满足”，须通过可执行验收。** 本文件只对计划与源码结构做审计，不把静态引用检查冒充正式 114 TC 的执行。

## 基线来源（实算）

- M00-r003 官方源码 ZIP `737a140ff2da49da1428edd8152060da1818e0c3981711c105beecff6239f468`，109 文件；已在私有 GitHub `main` 导入且远端 109/109 blob SHA 一致。
- PLAN v1.1 ZIP `154f632edc3d6352c6d57a95b7c1b6e8d038aa8782a34d1d571538c791a87ffe`，55 文件；本草案镜像保持各文件字节一致。
- 原始 v2.0 ZIP `de0f36f5c270e07abedc0e317d8517c8f5e0d86a5a42897120d42b3a1f5dec47`，仅作为只读设计/机读事实。

## 第一轮：可机读结构交叉检查（源代码脚本见 A0 独立审计证据）

- WBS **130/130 唯一**，`DependsOn` **300 条边/37 层无环**；各任务号 W001..W130 完整。当前 W001 已限定 SOURCE_PROVENANCE_ONLY VERIFIED，官方下一批 Ready：`W002/W003/W005/W007`。原始 PLAN 的全部 NOT_STARTED 是**只读历史模板**而非当前状态。
- 原设计 **114 RQ/114 TC**、其中 **78 P0/36 P1**，全部原计划的 PrimaryWBS 和 TestCaseId 静态引用有效；**52 场景、10 UI、11 GATE**引用有效。**114 TC 和 52 场景并未由本审计执行。**
- 21 模块各一主 Owner，A7 仍负责 MOD-13 SolutionManager / MOD-14 DeploymentManager、W083～W087（5 项），目前仅有 5 个 Agent 子聊天，**A7 无执行者**。
- 45 PLC 生产许可全 FALSE；14 FG 全 FALSE，7 BLOCKED / 7 OPEN。D1000/D1030、D1013、D2000 长度、D1016 分流不能因迁移被暗自解决。
- 当前源码 109 文件，按 `OWNERSHIP.json` 除 immutable/file manifests 外恰有 **3 个无 Owner**：`.gitignore`, `tests/patch/test_zip_protocol.py`, `tests/unit/opencv5_link_probe.cpp`。本 PR 的 A0 所有权元数据变更会明确分配并经 A10 复核。

## 第二轮：GitHub 实际能力/旧工作令交叉审计

| ID | 严重度 | 实际发现 | 处理动作 | 后续负责人 |
|---|---|---|---|---|
| GH-01 | CRITICAL | 根 AGENTS / README / 五份 CHAT_START 与 ZIP_PROTOCOL 均锁 M00-r001 / 手工 ZIP，与现已导入 GitHub 的 main 冲突 | 更新为 commit-SHA/分支/PR 工作令，保留 ZIP 仅作发布/灾备 | A0，本 PR |
| GH-02 | CRITICAL | 正式仓库只锁定 PLAN 部分节选，缺 11 份 prompts、11 份任务卡、EXIT/RISK等；远端 Agent 不能只靠仓库执行完整 WBS | Git `docs/plan/v1.1-original/` 镜像全部 55 文件+逐文件 SHA，不修改原 zip | A0，本 PR |
| GH-03 | CRITICAL | `contracts/C00_DRAFT.md` 与 PLAN §4.2 的 C-07～C-12 **编号/含义不一致**，C08 在两份文件的 Owner 甚至分别为 A7/A6 | 在 W004 以 PLAN 为规范冻结 C01+版本/compat schema；C00 仅做历史草案 | A0，后续 C01 PR |
| GH-04 | HIGH | A7 两模块/五 WBS 无执行聊天，双机产品方案链将卡死 | 成立 Agent06 专责或 A0 scoped CR 暂代+独立 A10 | A0 / A7 待定 |
| GH-05 | HIGH | 3 个已有路径无 Owner，且 `.github/workflows/`、A7 配置等新增路径未登记 | 更新 OWNERSHIP.json，A10 独立检查 owner 和变更白名单 | A0，本 PR |
| GH-06 | CRITICAL | GitHub PRIVATE Rulesets 403 `Upgrade to GitHub Pro or make this repository public`; branch-protection GET 403 integration denied | 不使仓库公开；宣告技术上未保护；A0 严守 PR 合并、A10 复测；可日后评估支持强制保护的账户/计划 | GitHub admin/user |
| GH-07 | CRITICAL | 远端无 `.github/workflows`，无 GitHub Actions 项目 CI；缺 Linux/Windows 构建发布闭环 | A10 实际提交 workflow PR，设置缺 SDK 阻断和真 GUI/协议负测 | Agent05/A10 |
| GH-08 | HIGH | `FILE_HASHES.json`, `SHA256SUMS.txt`, `RELEASE.json` 是 M00-r003 封存状态；日常 Git commit 变更后必然不再映射旧 release ZIP | 禁止把旧清单当 HEAD 证明；A0 发布新正式版才生成新封存清单 | A0/A10 |
| GH-09 | HIGH | v2/PLAN 原设计中 A7 Owner、任务卡与 M00 当前 Chat 所有权有差异 | 明确设计 Owner、实施 Agent、GitHub login 和 QA Reviewer 四类身份，不能混同 | A0 |
| GH-10 | HIGH | Agent05 A10 同时写测试+自己测，GitHub 同账户不能由 PR 自我 Approved 构成独立审查 | 自有路径需 A0 独立评审；受影响业务代码 A10 使用全新工作树执行 QA | A0/A10 |
| GH-11 | HIGH | Agent03/A10 的 SQLite 原始 v2 草案允许重复 RAW_FRAME 父资产、AVAILABLE 为空 URI/hash | 在 C01/A6 schema ADR 解决唯一索引+事务/崩溃恢复，不允许直上旧 DDL | A0+A6 |
| GH-12 | HIGH | offscreen GUI 的中文字符出现方框；截图结构通过并不证明工位操作可读 | 加 CJK fallback 与 2K/1080p 真 QML 布局、Windows native UI 验收 | Agent04+A10 |
| GH-13 | HIGH | 当前只有 6 项 M00 CTest 是健康脚手架，不能等同 114TC或 FINS/11路/设备图/2K等实现 | 四级门禁、继续分阶段 G00..G09 和 52 场景证据 | 全员 |
| GH-14 | MEDIUM | 根 `README.md` 曾错误指向不存在的 `docs/agents/OWNERSHIP.md` | 修正为机器实际存在的 OWNERSHIP.json | A0，本 PR |

## 两轮审核后的判断

**计划的广度完整，不等于具体细节/实施确定性完整。** 37 波次无环/114 TC 映射证明结构可实施，不证明 PLC 地址、硬件 ABI、中文 QML、持久事务、主从换型故障恢复、图像内存与产品质量算法正确。将 Git-first 作为“协作升级”，不修改现场安全、逻辑依赖或 WBS 验收的硬门槛。

**准许并行编码，严格控制合并与发布。** 原 PLAN 已有第 10 节 Git 设计，因此这次是执行路径统一，不是重做架构。A10 仍需独立签审本草案；没有通过就不合并，不通过 CI就不发布。

## 原始 PLAN 的三轮真执行复核（Git 工作流变更不覆盖历史审计）

在**临时镜像**上实际执行原始 PLAN 三套程序（没有修改 v1.1 原包或原始文件）：

- `tools/audit_round1_structure.py`：**40/40 PASS，退出码 0**，额外与展开 v2.0 验证 RQ、TC、模块/FG 和 WBS 源目录引用。
- `tools/audit_round2_semantics.py`：**33/33 PASS，退出码 0**，覆盖模拟安全、逻辑关系与 QA 文字语义。
- `tools/audit_round3_independent.py`：**53/53 PASS，退出码 0**，实核 v2.0/Qt 参考模板/SDK manifest SHA，重算 37 波次与 52 场景、10 UI 关联和随附生成器漂移。
- 对本次 Git 治理草案独立运行 `validate_git_governance.py`：初次 **67/67 PASS，退出码 0**，包括 55 原始 PLAN 文件逐一 SHA 一致、109 源码原始文件不删、冻结源码锁不变及新增路径所有权唯一。追加本证据摘要和 JSON 后须再次复跑检查。

这些都是**设计/静态治理核验**；没有执行 114 条 AOI 正式功能测试、52 个仿真场景、Windows native 或真实生产 I/O。
