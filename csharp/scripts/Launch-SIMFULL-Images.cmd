@echo off
setlocal EnableExtensions DisableDelayedExpansion
title AOI SIM_FULL - Operator PNG Inspection
set "BASE=%~dp0"
set "IMAGES=%~1"
if not defined IMAGES set "IMAGES=%AOI_SIM_IMAGE_ROOT%"
if not defined IMAGES (
  echo AOI Vision Platform / SIM_ONLY Real PNG Inspection
  echo Select the folder containing 7 CH*.png and 2 LINE*.png.
  set /p "IMAGES=Enter full image folder path: "
)
if not defined IMAGES goto missing
for %%I in ("%IMAGES%") do set "IMAGES=%%~fI"
if not exist "%IMAGES%\" goto missing
for %%N in (01 02 03 04 05 06 07) do (
  if not exist "%IMAGES%\CH%%N.png" (
    echo MISSING CH%%N.png from %IMAGES%
    goto missing
  )
)
if not exist "%IMAGES%\LINE-S-8.png" goto missing
if not exist "%IMAGES%\LINE-M-2.png" goto missing
if not exist "%BASE%Launch-SIMFULL.ps1" goto unavailable

set "AOI_SIM_IMAGE_ROOT=%IMAGES%"
set "AOI_SIM_IMAGE_LAYOUT=FLAT"
rem Real PNG inputs must run the event-driven legacy PLC shadow, not FIFO_DEMO.
rem Strict LOCAL FINS/UDP, no physical PLC or SDK connection.
set "AOI_SIM_PLC_PROFILE=LEGACY_PLC_SHADOW"
rem Old line parent convention: rows [1::2] and [2::2].
rem Field camera transforms and D1013 alias still require site acceptance.
set "AOI_SIM_LINE_ROW_MAPPING=LEGACY_DROP_ROW0"
echo.
echo SIM_ONLY real PNG folder: %AOI_SIM_IMAGE_ROOT%
echo CH01..CH07 + LINE-S-8 + LINE-M-2 validated.
echo Legacy FINS/UDP loopback: D1999, camera D1006..D1014, final D1016/D2099.
echo Nine physical frame requests, eleven logical OpenCV channel results.
echo Legacy row1/row2 stride2 split; camera mapping D1013 remains FIELD_PENDING.
echo A1 one-time output claims and A7 actual UDP ACK/readback audit enabled.
echo 5 virtual pieces will each inspect these 9 actual PNG inputs.
echo Real OpenCV contour defects can produce NG / simulated REJECT.
echo No actual camera/PLC operation; do not use for factory production.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BASE%Launch-SIMFULL.ps1" -ImageRoot "%IMAGES%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo AOI_SIM_FULL_IMAGE_LAUNCH_FAILED exitcode=%RC%
  echo See sim-full-data\launch-logs for the full launch log.
  pause
)
exit /b %RC%
:unavailable
echo Missing Launch-SIMFULL.ps1; extract the complete package.
pause
exit /b 2
:missing
echo Input image folder incomplete or unavailable.
echo Require CH01.png to CH07.png plus LINE-S-8.png and LINE-M-2.png.
echo 8-bit PNG expected. OpenCV will reject unsupported formats or broken PNG.
pause
exit /b 7
