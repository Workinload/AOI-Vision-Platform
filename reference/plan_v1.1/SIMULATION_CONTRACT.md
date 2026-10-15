# 模拟器、仿真数据与进程启动契约 v1（实施规格，未实现）

## 1. 必须提供的启动入口

计划实现 `simctl` 统一 CLI，命令**示意**（本 PLAN 包中并无可执行的 AOI 软件）：

```bash
# 起 MasterCore / SlaveCore / QtUI-Master / QtUI-Slave / vPLC 五进程
simctl up --config config/sim-factory.json --scenario scenarios/happy_two_items.yaml --seed 20261015
simctl status --json
simctl inject --type peer_disconnect --host slave --duration-ms 60000
simctl inject --type camera_drop_frame --device HikSlaveLeftEdge --at-item 2
simctl stop --process slave-core
simctl start --process slave-core
simctl evidence --out evidence/runs/<run-id>
simctl down
```

API 契约：`up` 退出0只有在进程启动及健康检查通过时；`status` 每个进程有 `pid,role,port,db_path,health,release_id,epoch,connection_quality,uptime`；`inject` 返回准确生效事件 `fault_id/seed/trigger_ts`，未注入返回非零；`evidence` 必须包含数据库、协议轨迹、日志、Raw/Result assets HASH、UI截图/进程记录的可验证目录；`down` 必须能安全停止、不清空历史证据。`reset` 需要显式危险确认、限于模拟工作目录。

## 2. 进程网络和目录区隔

基线示例：vPLC 监听 `127.0.0.1:19600/UDP`；Master/Slave Core 使用两个独立的 loopback TCP 端点传 PeerLink 和 IPC，模拟防火墙默认限制外网、禁止任何用户传任意物理 PLC 地址到驱动。避免与现场 PLC `192.168.1.50:9600` 配置混用；SIM profile 不能包含现场 PLC 的有效生产写 endpoint。以下是**示例端口**，最终由 ConfigSchema 可配置并执行冲突扫描；每次仿真目录 `/runs/<run-id>/master/{db,assets,logs}`、`slave/{db,assets,logs}` 和 `vplc/{state,logs}` 互不共享可写文件。

自动检查：DB 两个不同可写路径，PeerLink 与 UI IPC 是 OS socket，两个 role 从**同一二进制**启动且角色不同，系统关掉 Slave 时 Master 仍运行，重新启动 Slave 能从自身 Outbox/Inbox 断点恢复。可选容器 namespace 方式是强化项，不是没有权限时虚构 PASS 的证据。

## 3. 硬件能力 / 结果槽位表

| Host | 模拟物理采集设备 | 物理工位 | 输出 ChannelId | 对应 CamDone 旧参考 |
|---|---|---|---|---|
| MASTER | HikMasterLocate（面阵） | 上料定位 | CH01 | D1006 |
| SLAVE | HikSlaveFrontTab（面阵） | ST03 正面极耳 | CH02 | D1008 |
| SLAVE | HikSlaveFrontEdge（面阵） | ST03 前短边 | CH03 | D1007 |
| SLAVE | HikSlaveBackTab（面阵） | ST04 反面极耳 | CH04 | D1010 |
| SLAVE | HikSlaveLeftEdge（面阵） | ST04 左长边 | CH05 | D1009 |
| MASTER | HikMasterRightEdge（面阵） | ST05 右长边 | CH06 | D1011 |
| MASTER | HikMasterRearEdge（面阵） | ST06 后短边 | CH07 | D1012 |
| SLAVE | ItekSlaveBack（**一套线扫硬件**） | ST09 反面线扫 | CH08、CH09（共一原帧、同 D1013） | D1013 ⚠别名待核 |
| MASTER | ItekMasterFront（**一套线扫硬件**） | ST10 正面线扫 | CH10、CH11（共一原帧、同 D1014） | D1014 |

**静态图源而非实机证据**：旧线扫 8200×15000×8bit；A/B 按 [1::2]、[2::2] 各产生 7500/7499 行且丢第0行；反面先水平镜像、两个结果再各转180，正面 split1 转180 split2不转。模型/参数模拟值允许由产品 fixture 配置，但必须可通过 `fixture_sha256` 还原。

## 4. 模拟数据要求

`FixtureManifest` 至少 `fixture_id, scenario_id, seed, simulation_method, created_utc, device_id, station_id, channel_id, workpiece_id, epoch, release_id, capture_id, attempt_no, frame_id, image_dimensions, pixel_format, image_sha256, expected_processing_status, expected_quality_status, generated_defect_boxes`。真图如来自用户须保留来源及授权说明；合成图标 `synthetic=true`。

按优先顺序准备 A 正常 2 件、B 1 路 NG、C 1 路 MISSING、D 重拍、E 同条码两物料、F 旧 Epoch 迟到、G 线扫损坏、H 断链补偿、I 主从换型一半失败。每个 fixture 明确这是什么**模拟预期**，不要把 `expected_defect_boxes` 冒充 AI 模型实测输出。`SimulationInference` 应读 frozen fixture 或由固定 seed 给出一致输出，**真正 OpenCV 算法的 ROI/方向/像素哈希另行真实测**。

## 5. 时钟、故障、证据

Scenario Driver 分出 `wall-clock`（日志 UTC 与跨进程观察）与 `virtual-clock`（某些可控测试 deadline），任何跨进程虚拟时间推进必须经过确定性屏障/事件序列而非直接 `sleep(1)` 取代硬触发语义。性能测试仍使用真实单调时间，不能凭虚拟时钟称 123MB 压缩达到厂内节拍。

故障注入可从 `simctl` 指定事件序号/WorkpieceId/设备ID/概率/seed；必须输出可检索的故障事件而不只在内存变量中修改。收尾时所有事件放在 `EventEnvelope` 数据中，最小只读展示可从 `trace_id` 跳到 Camera → Station → Channel → Master/Slave PeerLink → vPLC Txn → FinalResult → Alarm。

## 6. SIM 环境端点校验示例

本包 `CONFIG/sim_profile.example.json` 只是范例，真实项目必须通过 Schema 和启动验证；`ui_is_mock` 不可作为放宽安全的开关。所有外部网络异常情况都必须拒绝启动带写权限的模拟网关。A2 负责 simulator 配置守卫，A10 负责从 OS socket/pcap 层独立查证。

## 7. 独立复判补充的决定性约束（v1.1）

- 场景日志的可复算业务摘要只选 `WorkpieceId/Epoch/ReleaseId/ChannelId/CaptureId/AttemptNo/Step/ResultCode/assetDecodedHash/causalSequence`，排除无法确定的 `OccurredUtc/PID/随机临时目录/真实线程调度`；并以虚拟时钟事件屏障同步进程，保证因果可重复，不能用sleep竞争运气。
- SIM_ONLY可以注入 `SimSlotOccupancy/SimStationTriggerSequence/SimCycleEpoch` 以建立可测关联，但必须标注“仿真数据，现役PLC未证”；不确定时隔离帧并HOLD。
- 所有UI↔Core修改命令在RBAC完整上线前遵循 deny-by-default；异常路径和未识别SignalId不能调用PLC写网关。
- 方案 `STAGED → PREPARING_DEVICES → READBACK_DEVICE → READBACK_PLC → PREPARED_BOTH → COMMIT`，W086不能提前将准备成功标成可投产。
- vPLC写入和Master/Slave PeerLink实际网络连接都要提供可复验socket/报文追踪，单纯端口名/配置JSON不是运行证据；外网/真实设备阻断若受平台权限限制必须记NOT_VERIFIED。
