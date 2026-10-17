@echo off
setlocal EnableExtensions
title AOI-Vision-Platform SIM11 launcher
set "BASE=%~dp0"
set "DATA=%BASE%sim-data"
set "AOI_SIM_CHANNEL_SET=ALL_11"
for /f "delims=" %%G in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')"') do set "AOI_SIM_CONFIG_TOKEN=%%G"
if not defined AOI_SIM_CONFIG_TOKEN echo SIM_ONLY_ROI_WRITE_DISABLED - PowerShell session token unavailable
if not exist "%BASE%Host\AOI.Host.exe" goto missing
if not exist "%BASE%UI\AOI.Ui.Avalonia.exe" goto missing
if not exist "%BASE%Diagnostics\AOI.Diagnostics.Cli.exe" goto missing
if not exist "%DATA%" mkdir "%DATA%"
echo ===============================================================
echo AOI Vision Platform SIM_ONLY - 9 virtual devices / 11 channels
echo Master, Slave and PLC are LOCAL SIMULATION PROCESSES only.
echo 127.0.0.1 local test only. NO REAL PLC OR CAMERA CONNECTION.
echo ===============================================================
echo [1/5] Virtual PLC on 127.0.0.1:19600
start "AOI SIM Virtual PLC" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" vplc --data "%DATA%"
timeout /t 1 /nobreak >nul
echo [2/5] Slave Core TCP 19610 / IPC 19622
start "AOI SIM Slave Core" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" slave --data "%DATA%"
echo [3/5] Master Core IPC 19621
start "AOI SIM Master Core" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" master --data "%DATA%"
timeout /t 2 /nobreak >nul
echo [4/5] Master Avalonia window
start "AOI SIM Master UI" /D "%BASE%UI" "%BASE%UI\AOI.Ui.Avalonia.exe" --master
echo [5/5] Slave Avalonia window
start "AOI SIM Slave UI" /D "%BASE%UI" "%BASE%UI\AOI.Ui.Avalonia.exe" --slave
echo.
echo Startup requested. If a process fails, inspect its console window.
echo PLC is read-only; operator ROI edits create SIM_ONLY drafts ONLY (not applied to production).
echo Snapshot and asset data: "%DATA%"
echo Close ALL five SIM processes before starting another run.
echo An existing run uses fixed local ports; repeated launches will conflict.
echo The current fixture simulates one workpiece per data directory.
echo.
exit /b 0
:missing
echo ERROR: Package incomplete. Extract the entire zip before launching.
pause
exit /b 2
