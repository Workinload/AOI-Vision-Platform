@echo off
setlocal EnableExtensions EnableDelayedExpansion
title AOI SIM_ONLY product recipe preflight
set "BASE=%~dp0"
set "DATA=%BASE%sim-full-data"
set "CLI=%BASE%Recipes\AOI.Solutions.Cli.exe"
if not exist "%CLI%" goto missing
if exist "%DATA%\master\production.db" goto hold
if exist "%DATA%\slave\production.db" goto hold
if exist "%DATA%\master\sim-full-cycle.db" goto hold
if exist "%DATA%\master\sim-product-node.sqlite" goto hold
if not exist "%DATA%" mkdir "%DATA%"
echo SIM_ONLY OFFLINE PRODUCT PREPARATION. NO REAL PLC OR CAMERA WRITES.
"%CLI%" bootstrap "%DATA%" SIM_PRODUCT_A "Test SIM Product A" sim_engineer
if errorlevel 1 goto failed
"%CLI%" tune "%DATA%" SIM_PRODUCT_A ST05 1 110 0.1 sim_engineer
if errorlevel 1 goto failed
"%CLI%" publish "%DATA%" SIM_PRODUCT_A sim_engineer > "%DATA%\published.log"
if errorlevel 1 goto failed
for /f "tokens=2 delims= " %%A in ('findstr /B "SIM_PRODUCT_PUBLISHED" "%DATA%\published.log"') do (
  for /f "tokens=2 delims==" %%B in ("%%A") do set "RELEASE=%%B"
)
if not defined RELEASE goto failed
"%CLI%" activate "%DATA%" "!RELEASE!" sim_engineer
if errorlevel 1 goto failed
"%CLI%" status "%DATA%"
if errorlevel 1 goto failed
echo Recipe prepared: !RELEASE!
echo Open Launch-SIMFULL.cmd for a new 5-piece synthetic production test.
echo A6 SafeHold is NOT removed. REAL hardware operations stay forbidden.
exit /b 0
:hold
echo ACTIVATION_BLOCKED: a prior SIM_FULL database or prepared release already exists.
echo Review old evidence; do not automatically reuse or delete the production folder.
pause
exit /b 3
:failed
echo SIM_PRODUCT_SETUP_FAILED. Inspect the previous error and keep WAL evidence.
pause
exit /b 1
:missing
echo Recipe CLI missing. Extract the complete Windows release archive.
pause
exit /b 2
