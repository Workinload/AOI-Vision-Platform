# AOI Git-first v1.2 — 操作级 PR / WBS / QA 工作协议（A0 审核草案）

## 1. 连接与主线

- Repo: `Workinload/AOI-Vision-Platform`（PRIVATE）；所有新工作从**实际当前** `main` 提交开始；PR 提交前重新读取主线并检查是否过时。
- 不允许使用旧 ZIP 的 `base_archive_sha256` 代替 PR 基线；必要时利用 GitHub 的 Blob / Tree / Commit / Branch / PR API 上传。工具缺失要报 BLOCKED，不要求用户反复手工搬 ZIP。
- 每个 Agent 仅限自己的文件路径。禁止向 `main` 直接写功能源码、变更安全基线或暗中改动另一个 Agent 的模块；主线合并只由 A0 在记录检查证据后执行。
- 不因五个 ChatGPT 子聊天而生成五个 GitHub 用户。**GitHub 提交者可能都显示同一个 Workinload 身份**；不能把自己发起/批准的 GitHub Review 当作真正独立 QA，独立性以 A10 另一个上下文、真实复跑日志和 A0 核签来保证。

## 2. 原子任务与分支

| 聊天 | 职责 | 推荐分支前缀 | 文件所有权 |
|---|---|---|---|
| A0 | 架构、领域 ID、合同、版本、基线 | `architecture/Wnnn-topic` 或 `governance/topic` | `docs/agents/OWNERSHIP.json` 内 A0 |
| Agent01 | A1+A5 | `agent/01/Wnnn-topic` | A1+A5 |
| Agent02 | A2+A3 | `agent/02/Wnnn-topic` | A2+A3 |
| Agent03 | A4+A6 | `agent/03/Wnnn-topic` | A4+A6 |
| Agent04 | A8+A9 | `agent/04/Wnnn-topic` | A8+A9 |
| Agent05 | A10 | `qa/05/Wnnn-topic` | A10 |
| Agent06（用户已批准，待新聊天启动） | A7，方案+部署 | `agent/06/Wnnn-topic` | A7（MOD-13/14）；CR-A0-004 登记需独立审查 |

用户已同意新增 Agent06，A0 以 CR-A0-004 仅授权 A7 人员路由；在 Agent06 实际新聊天启动前，状态必须仍为 `PENDING_NEW_CHAT_START`。W083～W087 的正式 `DependsOn` 仍全部生效；未 VERIFIED 时可做隔离 BranchDev/Mock/Draft PR，但不可并入 main 或签署 WBS。A0 不再默认代理 A7；若另行代理需独立 CR。

每 PR 尽量只做 1～3 个相关 WBS；任务 Issue 中指明 `Owner`, `Reviewer`, `DependsOn`, `RQ`, `TC`, `ExpectedFiles`, `Evidence`；跨所有权提交必须有 **CR ID + 原 Owner 同意 + A0 批准 + A10 独立回归**。

## 3. 四层门禁（不可混淆）

**A. 允许编码 / `BRANCH_DEV`：** 可以在独立分支研究、编写适配器/Mock、非生产接口桩、做单元测试并开放 Draft PR，即使某些业务依赖未 VERIFIED。写明 `NOT_INTEGRATION_READY`，不得借此改变正式 WBS ledger 或启用现场设备。

**B. 允许集成 / `MERGE_READY`：** 所有正式 WBS `DependsOn` 在受控 `TASK_LEDGER.json` 中 VERIFIED；Owner 路径合法、C01/版本契约具备、真实测试证据与 A10 单独复核，A0 检查并合并。没有经过所需 QA 的 PR 不进入 main。已在分支实现但依赖未完成的工作保持 Draft，不伪造 VERIFIED 状态。

**C. 允许版本发布 / `RELEASE_READY`：** A0 在确定的主线提交上生成新 immutable SOURCE ZIP、重算 SHA/manifest、独立完成 Linux/Windows 相应门禁、A10 对确切发行候选独立签审、A0 后置回归。`RELEASE.json`/源文件哈希清单只在此阶段变更。源码并入 Git 不意味着 `SIM_READY`；SIM_READY 另需 W001～W122 和 G00～G09 全量证据。

**D. 允许接入现场 / `FIELD_ACCEPTED`：** 只在 W123～W130、14 项 FG 全部被有权现场人员核验签署后按逐站安全规程执行，绝不可由 Git/Agent/CI 审批代替。模拟期始终禁写真实设备。

## 4. PR 合并材料与测试诚实性

PR 必须提供：TaskId、Owner、当前 base commit SHA、`git diff --name-only`、合同版本、前置 VERIFIED ledger 证据、RQ/TC/Scenario IDs、真实命令/退出码、测试日志/图像/DB/协议证据 SHA、失败项/未测项、风险与回退、是否 Draft、是否跨所有权。A10 对关键测试须用独立工作树在确切 head SHA 重跑。

**合并前至少检查**：路径所有权、受保护基线不变（`reference/`, `BASELINE_LOCK.json` 等）、非现场写与本机回环隔离、修改命令 deny-by-default、Core/UI 独立运行、错误不自动 OK、契约兼容、编译/单测（有 SDK 时）、场景专项负测。缺 SDK 的 CI 可以 SKIP 并阻断对应合并门禁，**不可将 SKIP 解释为 PASS**。

PR 如改公共接口：提交 ADR / `ContractChangeRequest`、兼容解法、消费者影响矩阵、正反 schema 测试，A0 先冻结 C01，涉及 A10 测试脚本不得由 A10 自签。

## 5. GitHub 真实保护能力与应急流程

截至 2026-10-15 独立检查，当前个人私有仓库的 Rulesets GET 返回 403：`Upgrade to GitHub Pro or make this repository public to enable this feature`；Branch-protection GET 返回 `Resource not accessible by integration`。**不得声称分支保护或强制 PR 门禁已配置成功。** `CODEOWNERS`/PR 模板只是辅助，现阶段需要 A0 不直接推 main 的操作纪律和 A10 独立报告。不得为取得规则功能公开 AOI 私有源码。

正式 GitHub Actions CI 也尚未存在；需要 Agent05 按 `docs/reviews/A0_CI_SPEC_GIT_FIRST.md` 单独提交 ".github/workflows/" 补丁，A0 核准，真实运行后才能称为 CI PASS。分支保护正式生效需要用户/仓库管理员另行确认权限/订阅。

## 6. 离线 SDK、证据与基线快照

Qt/OpenCV/OpenVINO 离线 SDK、现场 PLC 项目、生产配置、私密凭据不得提交仓库。正式构建环境必须重建 SDK cache 并比对分卷 SHA、ABI 和锁定版本，Windows native 与 Wine 的证据分别记录。

`reference/plan_v1.1/` 和 v2 源快照仍严格只读；`docs/plan/v1.1-original/` 是完整 55 文件只读副本。`FILE_HASHES.json`/`SHA256SUMS.txt` 是最近发行基线的“过去事实”，普通 PR 改源码以后不更新它们：下次 A0 `package_source.py` 生成 ZIP 时才自洽重算。CI 当前应直接对 HEAD `git ls-files` 计算工作树差异并检查变更所有权，不应让某个历史发布包的快照清单错误地认证每个 Git 开发提交。

## 7. 失败与回滚

发生安全或识别异常停止合并并标注失败；不得更改测试脚本让原测试结果表面变绿。先回滚到已验收主线/提交，A0 创建修复 Issue，A10 独立重跑。对 Source ZIP 与 Git 提交之间必须保持明确双向映射；只靠 GitHub PR 的绿色图标不算实际 AOI 检测闭环。
