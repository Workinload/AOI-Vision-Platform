# Agent05 · Issue #88 · Windows 原生操作验收（SIM_ONLY）

这是新版 **7 页面 Avalonia 工作台**的可执行 Windows x64 验收入口，不是 Headless 测试，也不是现场设备安全授权。

## 运行

1. 在 Windows 10/11 x64、有桌面登录会话和 .NET 10 运行时的电脑上，完整解压 GitHub Actions 构建的 `AOI_SIM11_Windows_Preview`。
2. 确保本机没有其他占用 `127.0.0.1:19600/19610/19621/19622/19631/19632/19641/19642` 的 AOI 测试进程。
3. 双击 **Run-Windows-Operator-Smoke.cmd**。它启动真实的 vPLC、Master Core、Slave Core、Master GUI、Slave GUI 共五个 Windows EXE，跑完 SIM_FULL 5 件 11 路模拟检测。绝不连接真实 PLC 或相机。
4. 自动以真实 Windows 鼠标依次操作 **主从两个窗口**的 7 个左侧导航：**生产总览 → 整机设备地图 → 11 路图集 → 历史 / HOLD → PLC 工程 → 产品方案 / ROI → 生产日报 / 周报**。每一步要确认 Avalonia 所属 TabItem 的实际选中状态并截图，而非仅记录按钮执行。
5. 在第 6 页选中 Master **CH06**、Slave **CH02**，填写归一化 ROI `X=0.15, Y=0.15, Width=0.70, Height=0.70`，点击真实的“校验并持久保存 SIM_ONLY ROI 草稿”，必须通过 Core IPC 校验对应设备通道的 `rev1`，以及本地 `sim-config.sqlite` 持久化。
6. 自动输出证据目录：`operator-smoke-report.json`、两套可见窗口截图、14 张页面操作截图、`master-roi-saved.png`、`slave-roi-saved.png`、原生 Core PNG、redacted WAL/SQLite bundle、日志、失败 Issue 草稿。验收脚本不会把截图自动上传公共仓库。
7. 双击入口会暂停供操作员人工浏览双窗口；按 Enter 后仅清理脚本自己启动的五个进程。

命令行也支持：

```powershell
powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageRoot .
powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageZip .\AOI_SIM11_Windows_Preview.zip
```

## 结果解释

- `0 / PASS_INTERACTIVE_WINDOWS`：每步真实操作、Core 数据及桌面大小条件均达标。
- `1 / FAIL_OR_UNVERIFIED`：实际步骤失败，参见报告逐项失败原因和截图。
- `3 / PARTIAL_OPERATOR_GUI_1080P_NOT_VERIFIED`：真实启动、检测、鼠标/UIA 和 ROI 保存可能通过，但当前 Windows CI 虚拟显示器无法提供目标 1920×1080 页面可见性。**不得当作 1080p 操作验收通过。**
- `-TryCi1080p` 只允许在明确的 GitHub CI 环境中尝试切换 1920×1080 显示模式并恢复旧设置；用户本机双击不触发切屏。

如果 GitHub hosted Runner 只有 1024×768，脚本会临时平移自己的测试窗口；这不等同于在正式操作员 1080p 工作站验收全部布局。

**约束：** ROI 是 Core 离线草稿，不自行发布生产方案；真实 PLC、相机、机械及物理输出始终禁用。该测试不改变 A0 的 Domain/Contracts/Host 接口，不需要另外启动一个 Mock Core。
