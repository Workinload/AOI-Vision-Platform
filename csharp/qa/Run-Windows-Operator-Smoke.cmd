@echo off
setlocal EnableExtensions
title AOI Agent05 Windows Native Operator Smoke
if not exist "%~dp0Windows-Operator-Smoke.ps1" (
    echo ERROR: Windows-Operator-Smoke.ps1 is missing from this package.
    pause
    exit /b 2
)
echo AOI SIM_ONLY native Windows acceptance - 5 real processes and two desktops.
echo This runs 5 virtual workpieces with 11 channels and captures operator evidence.
echo No PLC or camera outside 127.0.0.1 is contacted.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Windows-Operator-Smoke.ps1" -PackageRoot "%~dp0" -PauseForInspection
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" (
    echo Operator smoke found a failure or unverified UI action. Inspect operator-smoke-report.json
    echo and screenshots in the EvidenceRoot printed above. Five child processes were shut down after inspection.
) else (
    echo NATIVE DESKTOP PASS. Screenshots and SQLite evidence saved. Five child processes shut down.
)
echo Press any key to close this report.
pause
exit /b %RESULT%
