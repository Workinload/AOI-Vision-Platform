# AOI Agent05（A10）W003 / C01 最新 SHA 两轮独立签审

**日期：2026-10-16（新加坡时间）｜审查身份：Agent05 / A10｜仅限 SIM_ONLY 静态事实、Schema 与隔离 Mock；不是 A0 发布签名或 C01 冻结。**

## 结论（仅针对精确 Git HEAD）

| 对象 | 精确 Git SHA | 第 1 轮 | 第 2 轮 | A10 决定 |
|---|---|---|---|---|
| W003 Draft PR #28 | `8871f1e0129b0703ecbd3938344109b745e5a237` | PASS / exit 0 | PASS / exit 0 | **PASS_STATIC_REVIEW_ONLY**；30 条为未批准逻辑 DAG，不代表 W003 已 VERIFIED |
| C01 Draft PR #29 | `dd3ace7907a913e5df49bd0a8c90f610beebcbc5` | PASS / exit 0 | PASS / exit 0 | **PASS_SCHEMA_AND_MOCK_REVIEW_ONLY / HOLD_C01_FREEZE** |

GitHub 实际 Actions（非 A0 本地结果）：[Run #38778977040](https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38778977040)。A10 Job IDs：W003 `116573910122`、C01 `116573910616`，两 Job 均 `completed/success`，正反测试和证据 Artifact 上传步骤均 `success`。测试时 A10 审核分支提交 SHA `61947104daff935190491f7110f516983247355c`；真正执行的被审源码分别锁定上述 #28、#29 SHA（`git rev-parse HEAD` 强校验）。

## W003：真实命令、输出与正反门禁

GitHub Actions 在 `/tmp/a10-upstream` 创建 **独立工作树**，而不是测试 A0 的日志文件：

```bash
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 28 --sha 8871f1e0129b0703ecbd3938344109b745e5a237 --round 1
python3 docs/reviews/verify_w003.py --repo-root . --negative
python3 docs/reviews/verify_w003_owner.py .
python3 ci/a10_w003_pinned_manifest.py --repo /tmp/a10-upstream --sha 8871f1e0129b0703ecbd3938344109b745e5a237 --round 1
# Fresh Python executions, round 2
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 28 --sha 8871f1e0129b0703ecbd3938344109b745e5a237 --round 2
python3 ci/a10_w003_pinned_manifest.py --repo /tmp/a10-upstream --sha 8871f1e0129b0703ecbd3938344109b745e5a237 --round 2
```

**每轮实际退出码 0。** 两轮均通过 21/21 原始模块 Owner 及 A7→Agent06 路由、21 模块 30 条无环逻辑依赖、DAG `CANDIDATE_UNAPPROVED`、A0 原始验证器 **7/7 故意篡改拒绝**、A10 额外 **6/6 故意篡改拒绝**。已纠正之前 DAG CSV 文本提取前缀。精确现场证据中 **8 个列入 A0 manifest 的文件 SHA256 均一致，另加 manifest 自身，共 9 个 Git Blob 一致**（每轮 9/9）；55 份原始 PLAN 镜像每轮单独 SHA256 55/55 通过。不得把逻辑 DAG 当实际调用图或冻结版本。

原样 Runner 日志：`A10_PR28_REAL_ACTIONS_LOGS_38778977040.zip`，其中 `pr28-round1.log`、`pr28-round2.log`、`pr28-manifest-round1.log`、`pr28-manifest-round2.log`。校验用 SHA256 见下方机器清单；ZIP CRC 已通过。

## C01 v0.2：六类事件与 29 项测试的真实执行

```bash
python3 -m pip install --user 'jsonschema[format]==4.23.0'
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 29 --sha dd3ace7907a913e5df49bd0a8c90f610beebcbc5 --round 1
python3 contracts/C01/test_c01_draft_contract.py
python3 contracts/C01/test_c01_v0_2_review.py
python3 -W error ci/a10_v02_event_and_consumer_review.py --repo /tmp/a10-upstream --sha dd3ace7907a913e5df49bd0a8c90f610beebcbc5 --round 1
# Same audited Git HEAD, separate interpreter round 2
python3 ci/a10_pinned_upstream_review.py --repo /tmp/a10-upstream --pr 29 --sha dd3ace7907a913e5df49bd0a8c90f610beebcbc5 --round 2
python3 -W error ci/a10_v02_event_and_consumer_review.py --repo /tmp/a10-upstream --sha dd3ace7907a913e5df49bd0a8c90f610beebcbc5 --round 2
python3 ci/a10_c01_runtime_schema.py --schema /tmp/a10-upstream/contracts/C01/CORRELATION_ENVELOPE_DRAFT.schema.json
```

**每轮真实退出码 0**，原草案 9/9 单测通过，新增 v0.2 **29/29** 单测通过，旧独立 7/7 负测试通过；A10 独立六事件 Schema 检查每轮 **12 个正例接受、26 个反例拒绝、5 项针对性削弱规则负测试拒绝**，另有 5 条明确的运行时不可证明边界。原 C01 单信封 Schema 3 正/9 反/3 规则篡改一次运行退出 0。C-01～C-12 消费者矩阵 12/12 Owner/必关决策/NOT_FROZEN 门禁匹配；这仅说明阻断事项登记完整，不代表其他 Agent 已在本 SHA 完成消费者签审。

独立检验涵盖：`WORKPIECE_RESULT` 非 SUCCEEDED（PENDING/RUNNING/ERROR/CANCELLED/TIMEOUT）绝不可 `OK`；六种事件无需伪造互不相关身份字段；Alarm `ACK != CLEAR` 各需各自收据；`PREPARED_BOTH` 要双 Core 和模拟 PLC 回执、Host 唯一及 `ReadBackMatched=true`；`ASSET AVAILABLE` 要 URI+两种 hash+大小且拒绝路径穿越；`UI DISCONNECTED` 禁止 `IsLive=true`；`AUTH_AUDIT` 是记录而非可发放真实 PLC 写许可的授权接口。

**P0/P1 环境安全陷阱（实测）：** GitHub Runner 若只安装 `jsonschema==4.23.0`，日期格式校验的可选 RFC3339 依赖缺失时，`OccurredUtc='this is not UTC'` 曾被 `FormatChecker` 错误接受，从而使 A0 的 29 项单测 1 FAIL，两个 A10 独立轮次 FAIL（见历史 [Actions #38778735805](https://github.com/Workinload/AOI-Vision-Platform/actions/runs/38778735805)）。A10 修正 **自己的 workflow 环境**，锁定 `jsonschema[format]==4.23.0`，并加入 `FormatChecker.check('bad_date_time_not_rfc3339','date-time')` 启动自检后，最新两轮全部 PASS。未来正式 CI/生产部署必须强制该格式插件，不得只凭 Schema 字段 `format` 声称已检验时戳；A0 的源文件未被擅改。

## C-01～C-12 冻结阻断与消费者兼容边界

原 C01 v0.2 已登记 C-01..12 的 12 项独立 Owner 及约束，但以下必须 **HOLD_C01_FREEZE**：

- **C-01/C-04/C-05：** 身份/Epoch/Attempt 强类型与序列化协商、实际最后质量状态机还未成为可部署公共 DTO；不能因 Schema 正确就把报警或条码视为唯一工件。
- **C-02/C-03：** FrameRef 生命周期/9 物理采集单元→11 通道、FINS 真机地址/BA/BADC/WritePolicy 现场验证未完成，PLC 生产写始终禁用。
- **C-06/C-08：** 新六事件 Schema 中 **没有独立 PERSISTED_ACK 消息合同**；现有 SQLite `:memory:` 测试只模拟事务回滚/Outbox 唯一，未证明两个主从 WAL、持久 ACK 与重启幂等。
- **C-07：** `AVAILABLE` 提供合法 metadata 不意味着 URI 路径真的在合法 root、文件 bytes 已落盘或编码/解码像素 hash 经读回；相同 RAW_FRAME 的唯一性当前仅在内存 SQLite Mock。
- **C-09：** DetectionROI、DisplayROI、TransformChain 与真实推理错误隔离尚未验证。
- **C-10：** 结构 Schema 收据字段正确，但 `CoreReadbacks` 与模拟 PLC `ParametersHash` 跨对象是否一致由 demo runtime oracle 检查，**JSON Schema 本身接受不一致的有效哈希字符串**；不存在双机真实参数读回及失败回滚证据。
- **C-11/C-12：** `UI_SNAPSHOT` 的断线非 live 可以判定；真正的游标/快照源、服务端 Auth 拒绝、报警 ACK/CLEAR 落库审计还需真实 Core/DB。`AUTH_AUDIT(Decision=ALLOW,Action=PLC_WRITE)` 是可记录的事件格式，不可代替服务端授权或解除 SIM_ONLY。

此外主线 `TASK_LEDGER.json` 已确认 W002=`VERIFIED`，仅 **LEGACY_STATIC_FACTS**；W003 仍 `IMPLEMENTED_PENDING_A10_REVIEW`，W004=`DRAFT_NOT_FROZEN`。是否将 W003 正式签署 VERIFIED 完全由 A0 单独决定；W004 不能因本次 JSON 通过而自动 VERIFIED。`OWNERSHIP.worker_activation.AOI-Agent06.routing_qa` 仍残留旧 HOLD 注释，A0 可在未来受控治理 PR 中同步为最新 PR #12 已合并状态，但不能直接篡改主线锁定记录。

## 审核权限 / 未验证项目

A10 自编的新检查器与工作流在 [Draft PR #30](https://github.com/Workinload/AOI-Vision-Platform/pull/30) 中，须由 A0 进行**独立代码复核**。`main` 不得由 A10 合并；PR #28/#29 在 A0 许可前保持 Draft。原始 PLAN/reference/SQL/RELEASE/现场 FG 没有被本审核改写，SIM_ONLY 真正硬件测试未执行；114 原始功能 TC、52 场景、Windows 原生和真实生产写一律 NOT_VERIFIED。

**签审：Agent05 / A10（角色独立质量结论，非数字证书签名）。**