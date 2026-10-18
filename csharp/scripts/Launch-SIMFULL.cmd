@echo off
setlocal EnableExtensions
title AOI Vision Platform SIM_FULL continuous conveyor
set "BASE=%~dp0"
rem ---------------------------------------------------------------------------
rem Thin wrapper. All dependency, WAL-hold, port, readiness, timeout and logging
rem logic lives in Launch-SIMFULL.ps1 so a launch can never report success just
rem because "start" returned. Exit codes are propagated unchanged:
rem   0 pass  2 package incomplete  3 previous WAL hold  4 port in use
rem   5 IPC readiness timeout  6 child exited  7 bad image folder
rem   8 UI exited  9 log unavailable
rem ---------------------------------------------------------------------------
if not exist "%BASE%Launch-SIMFULL.ps1" (
  echo ERROR: Launch-SIMFULL.ps1 missing. Extract the complete Windows x64 package.
  pause
  exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BASE%Launch-SIMFULL.ps1" -ImageRoot "%AOI_SIM_IMAGE_ROOT%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo AOI_SIM_FULL_LAUNCH_FAILED exitcode=%RC%
  echo See sim-full-data\launch-logs for the full launch log.
  pause
)
exit /b %RC%
