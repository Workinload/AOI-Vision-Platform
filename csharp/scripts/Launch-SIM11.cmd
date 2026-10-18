@echo off
setlocal EnableExtensions
title AOI-Vision-Platform SIM11 launcher
set "BASE=%~dp0"
rem ---------------------------------------------------------------------------
rem Legacy SIM11 profile: SIM_LIGHT, one workpiece per data directory (sim-data),
rem 11 logical channels, LOCAL 127.0.0.1 vPLC only. Behaviour is unchanged from the
rem original launcher - it is the same five processes - but every start now goes
rem through Launch-SIMFULL.ps1 so a build can no longer report success after a
rem child failed. Exit codes are propagated unchanged:
rem   0 pass  2 package incomplete  3 previous sim-data WAL hold  4 port in use
rem   5 IPC readiness timeout  6 child exited  8 UI exited  9 log unavailable
rem ---------------------------------------------------------------------------
if not exist "%BASE%Launch-SIMFULL.ps1" (
  echo ERROR: Launch-SIMFULL.ps1 missing. Extract the complete Windows x64 package.
  pause
  exit /b 2
)
set "AOI_SIM_CHANNEL_SET=ALL_11"
for /f "delims=" %%G in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')"') do set "AOI_SIM_CONFIG_TOKEN=%%G"
if not defined AOI_SIM_CONFIG_TOKEN echo SIM_ONLY_ROI_WRITE_DISABLED - PowerShell session token unavailable
echo ===============================================================
echo AOI Vision Platform SIM_ONLY - SIM_LIGHT 9 virtual devices / 11 channels
echo Master, Slave and PLC are LOCAL SIMULATION PROCESSES only.
echo 127.0.0.1 local test only. NO REAL PLC OR CAMERA CONNECTION.
echo Data directory: %BASE%sim-data   (one workpiece per directory)
echo ===============================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BASE%Launch-SIMFULL.ps1" -SimMode SIM_LIGHT -DataRoot sim-data -ItemCount 1 -MaxInFlight 1 -AdditionalRequired "Diagnostics\AOI.Diagnostics.Cli.exe"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo AOI_SIM11_LAUNCH_FAILED exitcode=%RC%
  echo See sim-data\launch-logs for the full launch log.
  pause
)
exit /b %RC%
