# AOI Vision Platform — official M00-r003, GitHub-first development

当前主线基于 `AOI_Vision_Platform_M00_r003_OFFICIAL_SOURCE.zip`（SHA256 `737a140ff2da49da1428edd8152060da1818e0c3981711c105beecff6239f468`，109 个文件）。这只是 Qt 6.11.2 / C++20 / OpenCV 5.0.0 的 **Linux SIM_ONLY 双 Core + 双 UI + vPLC 健康脚手架**；不是完整 FINS、相机、视觉管线、11 图主屏、SIM_READY 或现场软件验收。后续 Git 提交与此已发布 ZIP 的字节并不自动一致。

## 新 Agent 入口
- `AGENTS.md`：版本/安全/所有权规则；`docs/agents/GIT_WORKFLOW.md`：分支、PR、任务四级门禁、A10 验证。
- `docs/agents/GIT_FIRST_PLAN_v1.2.md`：130 WBS、12 契约、验收覆盖与 Git 路线修订。
- `docs/agents/START_HERE.md`：角色启动方式；`docs/agents/CHAT_START_Agent01.md` ～ `CHAT_START_Agent06.md`：六个角色聊天各自的 Git 工作令；Agent06 (A7) 仅路由授权，独立聊天启动与 WBS 解锁仍需另行核对。
- `docs/plan/v1.1-original/`：原 PLAN v1.1 全套**逐字节只读镜像**，包括 11 份职责提示词、11 份任务卡和风险/退出门禁；不可直接改旧快照。
- `docs/agents/TASK_LEDGER.json`：**实际验证状态**。原 PLAN WBS `NOT_STARTED` 不会自动同步历史批准决议。

## 独立 Linux Debug 编译（Qt/OpenCV SDK 必须由用户提供的只读分卷还原）

```bash
export SDK=/your/external/vision_sdk
cmake -S . -B build/linux-sim -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug -DCMAKE_PREFIX_PATH="$SDK/components/qt;$SDK/components/opencv" \
  -DAOI_SIM_ONLY=ON -DAOI_BUILD_GUI=ON -DAOI_PROBE_OPENCV5=ON -DBUILD_TESTING=ON
cmake --build build/linux-sim --parallel 3
ctest --test-dir build/linux-sim --output-on-failure
QT_QPA_PLATFORM=offscreen QSG_RHI_BACKEND=software \
  python3 tests/integration/five_process_smoke.py --bin-dir build/linux-sim
```

**现实边界：** OpenCV 缺失时 CMake 可能只是警告并略过探针，不能把此退化构建算成精确 OpenCV5 验收。`main` 当前未有自动化 CI；配置 CI、私有仓库强制分支保护、Windows 原生完整闭包属于后续必须完成的任务。

## Safety

`AOI_SIM_ONLY=OFF` 在现有 M00 源码中被 CMake 显式禁止；实际设备 PLC/相机适配器尚未交付；生产写入一律禁用。现场 FG-01～FG-14 不因 GitHub 合并而变为通过。任何真实设备连接、生产授权或发布 `SIM_READY` 必须符合 WBS / Gate 和用户现场签字要求。
