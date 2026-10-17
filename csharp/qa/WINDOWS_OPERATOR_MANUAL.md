# Agent05 / Issue #88: real Windows operator acceptance

Windows interactive SIM_ONLY smoke, NOT a Headless test or field acceptance.

1. On a Windows 10/11 x64 desktop with .NET 10 runtime, extract the complete latest AOI Windows artifact into a writable folder. Close earlier AOI SIM processes.
2. Double-click Run-Windows-Operator-Smoke.cmd in the extracted folder. It starts three real Host EXEs and two Avalonia GUI EXEs with fresh SIM_FULL data (5 workpieces, 11 channels); never any real PLC or camera.
3. The program verifies Master/Slave IPC, 5 workpieces, 55+30 persisted channel results, real Core PNG previews, separate desktop windows, mouse clicks of five pages, and the GUI ROI save action. Screenshots, logs, a redacted SQLite evidence ZIP and operator-smoke-report.json are stored at the printed EvidenceRoot.
4. Inspect BOTH visible Master and Slave windows. The .cmd pauses after automated checks so you can inspect both live windows, then closes only its five child processes when you press Enter.
5. If a click or screenshot fails, read the exact failing step in operator-smoke-report.json. Attach generated operator-failure-issue.md plus the relevant screenshots/logs to an A0/Agent04 issue; do not mark interactive PASS.

## Manual fallback if Windows UI Automation misses a control

On BOTH windows physically click these existing tabs: 11 路通道监控 -> 45 条 PLC 工程映射 -> 14 项现场验证门禁 -> HOLD / 异常 -> 产品方案 / 换型. Check page content, select Master CH06 and Slave CH02 in each equipment panel and confirm their genuine images. Type ROI (X=0.15,Y=0.15,width=0.70,height=0.70), click the GUI save button on BOTH desktops and confirm that each corresponding Core persisted its revision. Attach real dated screenshots and Core ROI state. Manual evidence does not convert an automated FAIL into automated PASS.

## Direct PowerShell invocation

After extracting: powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageRoot .

From ZIP: powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageZip .\AOI_SIM11_Windows_Preview.zip

Exit 0 = actual interactive Windows native PASS including screenshots/clicks. Exit 1 = failure or unverified desktop. Default terminates only its own five child processes; the double-click runner pauses for inspection then stops its five child processes.

Screenshots may capture sensitive desktop information and are stored LOCALLY, never automatically uploaded. Review content before sharing. REAL hardware writes and 14 field gates remain disabled.
