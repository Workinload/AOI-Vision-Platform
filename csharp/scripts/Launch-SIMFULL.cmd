@echo off
setlocal EnableExtensions
title AOI Vision Platform SIM_FULL continuous conveyor
set "BASE=%~dp0"
set "DATA=%BASE%sim-full-data"
set "AOI_SIM_CHANNEL_SET=ALL_11"
for /f "delims=" %%G in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')"') do set "AOI_SIM_CONFIG_TOKEN=%%G"
if not defined AOI_SIM_CONFIG_TOKEN echo SIM_ONLY_ROI_WRITE_DISABLED - PowerShell session token unavailable
set "AOI_SIM_MODE=SIM_FULL"
set "AOI_SIM_ITEM_COUNT=5"
set "AOI_SIM_MAX_IN_FLIGHT=3"
if not exist "%BASE%Host\AOI.Host.exe" goto missing
if not exist "%BASE%UI\AOI.Ui.Avalonia.exe" goto missing
if exist "%DATA%\master\production.db" goto hold
if exist "%DATA%\slave\production.db" goto hold
if not exist "%DATA%" mkdir "%DATA%"
echo AOI SIM_FULL: 5 products / maximum 3 virtual concurrent in-flight
echo LOCAL 127.0.0.1 VPLC ONLY. REAL PLC AND CAMERA CONTROL DISABLED.
start "AOI SIMFULL Virtual PLC" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" vplc --data "%DATA%"
timeout /t 1 /nobreak >nul
start "AOI SIMFULL Slave Core" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" slave --data "%DATA%"
start "AOI SIMFULL Master Core" /D "%BASE%Host" "%BASE%Host\AOI.Host.exe" master --data "%DATA%"
timeout /t 2 /nobreak >nul
start "AOI SIMFULL Master UI" /D "%BASE%UI" "%BASE%UI\AOI.Ui.Avalonia.exe" --master
start "AOI SIMFULL Slave UI" /D "%BASE%UI" "%BASE%UI\AOI.Ui.Avalonia.exe" --slave
echo SIM_FULL running. ROI edits save only local revisioned drafts, never change active release.
echo Retain sim-full-data for evidence; do not auto-continue a failed physical queue.
exit /b 0
:hold
echo SIM_FULL_RESTART_HOLD: previous Core WAL detected.
echo Review and archive prior sim-full-data manually before starting a NEW TEST.
pause
exit /b 3
:missing
echo Missing executable: extract the entire Windows x64 preview ZIP.
pause
exit /b 2
