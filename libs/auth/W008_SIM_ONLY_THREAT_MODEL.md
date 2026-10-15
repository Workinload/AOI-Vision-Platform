# W008 · SIM_ONLY / 真实 I/O 隔离安全威胁模型（Agent04 / A9）

**工单**：[Issue #37](https://github.com/Workinload/AOI-Vision-Platform/issues/37) · 2026-10-16  
**状态**：`OWNER_IMPLEMENTED_PENDING_A10`；**不能**据此声称 W008 VERIFIED、W021 实际出站隔离或 W089 正式授权已实现。  
**唯一执行基线**：GitHub `main` `cf11c7d436910c2d5da4566572b65c5d13c86fa4`；`docs/agents/TASK_LEDGER.json` W002=VERIFIED，`scope=LEGACY_STATIC_FACTS_AND_UNCERTAINTY_REGISTER_ONLY`。历史任务卡原始状态只读。  
**只读事实**：`docs/reviews/W002_A0_FACT_LOCK_REVIEW.md`、`reference/spec_v2_machine_readable/plc_signal_registry.csv`、`field_verification_gates.csv`、`docs/plan/v1.1-original/WBS_TASKS_130.csv`。旧机寄存器含义尚有现场不确定性：D1000/D1030，D1013 共享/清零别名，D2000..D2019 条码长度、D1016/D2099 出料行为。**不能**因 W002 静态验收将这些地址作为真实可写入信号。

## 1. 资产、攻击者与安全边界

资产：人员与生产设备的安全、PLC 输出寄存器与联锁、相机/机器人接线、工件及主从身份、不可变版本、报警/审计记录。攻击者/故障源：恶意或误操作的 UI 客户端、过期会话、错误配置、同网段攻击者、重放的 IPC/PeerLink 消息、DNS/路由/端口转发变化、设备掉线、审计磁盘故障和旧 PLC 地址冲突。

**边界 A（Qt UI → Core）**：UI/本地 IPC 全部视为不可信输入；UI 置灰并不是授权。正式后端认证、会话和 RBAC（W089）未解锁，现有 M00 Core 只是健康检查的只读 IPC；不要把本测试中的 `Session` 对象当作可从网络反序列化的真实授权。

**边界 B（Core → 虚拟设备适配器）**：只允许编译期 SIM_ONLY 驱动和经后端校验的模拟端点。W008 的 `Policy` 只接收 Mock `SimSink`，不包含任何 socket、相机 SDK、PLC 驱动或可执行物理写入函数。固定的 `W008_MEMORY_VPLC` / `127.0.0.1:29731` **只是没有绑定端口的测试标识**，不是生产配置。网络层硬保证另由 A2 的 W021（IPv4/IPv6 DNS 解析+路由否决和 OS egress）、W026（真实 WritePolicy）完成。

**边界 C（授权变更 → 现场运行）**：仅 A0/现场有权人员按 14 项 FG 审核签署后，才能讨论将来 Field 模式。现有 14 FG 全为 FIELD_PENDING；UI 管理员、单人审批、历史地址簿、`--force`、ClaimedSitePermit **没有**现场授权效力。W008/本 PR 永远不包含物理写入许可。

**边界 D（命令 → 审计）**：未知操作、会话失效、重放、陈旧读回均拒绝；每次尝试写入先要求审计写入成功，再允许仅内存模拟写入。审计失败或模拟审计链检测到破坏，拒绝当前操作。Mock 的 FNV64 链是**非密码学**损坏检测、**非持久审计**，绝不能冒充 W019 的耐久签名审计或 W043 持久去重。

**边界 E（报警 ACK/CLEAR）**：ACK 仅确认看见报警，不能置 `active=false`；CLEAR 要求已 ACK、真实根因已消失、源端读回可信且授权独立。当前 `Alarm` 只由测试注入内存状态，不是对真实设备的健康保证。

## 2. 攻击场景与处理矩阵

| 风险编号 | 向量 / STRIDE | 失败后果 | 必须默认拒绝的守卫 | 负测证据 |
|---|---|---|---|---|
| T01 | 未知 PLC、域名替换、另类回环表示（S/T） | 意外访问现场设备 | 仅固定模拟 ID+数字 IP+端口，禁 DNS/重定向 | `unrecognized_plc_id`, `endpoint_reject_*`, `deterministic_endpoint_mutation_fuzz` |
| T02 | 伪装物理 PLC / 相机（S/E） | 真实 I/O 接入 | PhysicalPlc、Camera、WritePhysical **无允许分支** | `physical_*`, `admin_cannot_write_physical` |
| T03 | 管理员或维护参数绕过（E） | 取消安全联锁 | 管理员不等于现场许可，拒绝任意 override/siteClaim | `admin_cannot_claim_site_signoff`, `maintenance_override_denied` |
| T04 | 非法角色/伪造 UI role（S/E） | 越权模拟写入 | 仅信任已注入的服务器会话+显式模拟 Capability | `viewer_forged_admin_role`, `admin_needs_explicit_sim_capability` |
| T05 | 会话过期/撤销（S） | 已退出用户持续控制 | 每次指令重新检查认证、撤销、单调时间截止 | `anonymous`, `revoked`, `exact_expiry_boundary` |
| T06 | Core/设备断线（D） | 陈旧绿色状态/失控指令 | 任一侧离线拒绝操作，禁止旧读回授信 | `core_disconnected`, `device_disconnected` |
| T07 | 相同消息/Nonce/错误 Epoch 重放（T/R） | 重复或跨产品执行 | 消息 ID+hash、Nonce、会话 Epoch 去重与拒绝冲突 | `replay_*`, `stale_epoch`, `future_epoch` |
| T08 | 时间/读回过期（T） | 使用老参数误写 | 单调读回年龄超限拒绝，不接受“界面已经刷新” | `expired_readback`, `clear_stale_readback_rejected` |
| T09 | ACK 被当作 CLEAR（T/R） | 真故障被显示为正常 | ACK 保留活跃故障；CLEAR 需根因恢复、ACK、权限和可信读回 | `ack_*`, `clear_*` |
| T10 | 审计落盘失败/篡改（R/T） | 无证据的变更 | mock 审计写前置；失败时本次 I/O 与报警修改均取消 | `audit_*`, `denial_is_audited_not_applied` |
| T11 | 假 IPC 命令/恶意字段/日志注入（T/E） | 解析器绕过 | 未知 opcode、非法 trace/hash/nonce 默认拒绝 | `unknown_command`, `newline_injected_trace`, `nonhex_payload_digest` |
| T12 | 旧 PLC 别名的误用（T/I） | D1013 清零、条码/NG 冲突 | 历史 D1000/D1013/D1030 等永不等价虚拟端点 | `legacy_alias_rejected_*` |

## 3. 实现和可以证实的结果

- `libs/auth/sim_only_policy.hpp`：独立 C++20、无外部依赖的**准备性**拒绝策略；不导出 C01 DTO/ABI、不接入 A1 Core、A2 PLC、A3 相机或 A8 UI。拒绝状态是明确 `Reason`，不会把 Deny 表达为成功。
- `tests/unit/security/w008_sim_only_policy_test.cpp`：真正编译并运行的正/负测试，单一内存写接收器统计允许次数；审计故障/篡改注入。恶意输入只进入 C++ 内存函数，**不创建 TCP/UDP/Unix socket**。
- `tests/unit/security/run_w008_local.sh`：Debug/优化+UBSan 两轮重建、执行、收集日志与 SHA256，不借用旧的 PR #26/#32 测试。
- 不宣称由内存函数确保整个运行进程无法出站；真实强制安全要求 W021 对进程命名空间、防火墙、DNS/IPv6、socket 使用和代理/端口转发给出可复现隔离证据，A10 独立审查。

## 4. 正式后续条件与风险台账

1. **A0/C01**：正式命令 DTO、会话与角色来源、认证和授权拒绝错误码、EventId/TraceId、审计/AlarmEnvelope、设备来源及 PayloadHash 计算方式仍未冻结；本文件不创建私有伪公共协议。
2. **A2/W021/W026**：出站物理连接在 OS/构建层根除的端到端测试、IPv4/IPv6/域名重定向、容器网络逃逸、持久参数读回和 vPLC FINS 报文测试；本 PR 无授权代改。
3. **A9/W019/W089/W092**：可信身份签发、双人审批、跨重启持久 replay ledger、耐久不可变审计、源端故障真实 CLEAR、崩溃恢复、证据脱敏，须按原始依赖分别实现。
4. **现场 FG**：全部 14 项仍待现场有权人员签署；W002 STATIC VERIFIED 绝不等同 `FIELD_ACCEPTED`。
5. **跨进程结论**：此任务仅能验收 W008 **威胁模型与安全策略负向证据**，不能验收实时 Core/PLC 功能、Windows native、全系统网络隔离或生产安全认证。

**交付门禁**：待 Agent05/A10 针对确切 PR HEAD 独立双轮测试、审阅风险后，由 A0 才能决定 W008 的正式状态。Draft PR 不自行合并。
