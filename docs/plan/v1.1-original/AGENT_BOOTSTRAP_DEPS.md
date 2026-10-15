# AI-Agent 源资料/离线 SDK 交接约束（v1.1）

这份多 Agent PLAN 不是完整源码工程或 SDK 安装包。接手开发者需要得到**两份独立只读输入**；没有输入时只能完成规划/桩接口，不能凭空补写已知PLC/SDK接口细节。

1. 正式项目事实：`/AOI开发项目/01_完整交付ZIP/AOI_完整开发设计全书_v2.0_20261015.zip`。SHA256 必须等于 `BASELINE_LOCK.json` 中的 `aoi_spec_v2_zip.sha256`。解压到单独 `reference/v2.0/`，禁止执行其中旧 EXE/DLL/PLC 工程。先读 `README.md`、`AGENTS.md`、`00_项目基线/`、`02_设备与PLC/`、`07_可机读清单/`、`06_实施与验收/62_现场待核对台账.md`。
2. 工具资源：`/工具开发` 为独立只读云端库。优先取 Qt 6.11.2 Windows 5 分卷（`06_离线SDK/02_Qt6_Windows`）、OpenCV 5.0.0/OpenVINO 2025.4.1 4 分卷（`06_离线SDK/03_OpenCV5_OpenVINO`）、配套 `restore_sdk.py`、`SDK_PARTS_MANIFEST.json`，以及Linux Pe SDK中真正需要的组件。必须先核每片 SHA256 和完整组 hash；还原到独立缓存；绝不重新打包后冒充源包。
3. Qt 模板 `VisionForge_Qt6_YOLO11_ONNX_已修复源码与QA.zip` 只是 Widgets/C++17 参考，正式 AOI 必须是 C++20/QtQuick/QML。W007/W011 必须实际确认 Windows Qt Quick/Qt Quick Controls 2 和其QML imports，并真正编译一个最小 QML 应用；不能仅凭 SDK 清单认为它可用。
4. 无法访问 ChatGPT 云端 `/AOI开发项目` 或 `/工具开发` 的外部 Agent，需要由用户或项目协调者明确分享**同一 SHA256 的只读 ZIP/所需SDK片**。不要让Agent自己网上搜索不明版本、猜测PLC地址、替换SDK导致ABI混用。
5. 新工程、SQLite、临时模拟图像、真实Qt GUI截图和Windows发布目录全部在新的 `AOI-Vision-Platform/` 仓库及独立 `build/`、`runs/`、`evidence/` 中创建。不得向两个只读云端资料库写入项目产物。

第一条真实开工任务仍是 `W001`。它应核实此处全部来源并保存 `BASELINE_LOCK.json` 的新工作副本及命令、退出码和hash。本PLAN随附来源SHA256快照不等于任何后续 Agent 已完成重核。
