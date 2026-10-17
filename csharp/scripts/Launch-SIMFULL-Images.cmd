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
if not exist "%BASE%Launch-SIMFULL.cmd" goto unavailable
if exist "%BASE%sim-full-data\master\production.db" goto previous
if exist "%BASE%sim-full-data\slave\production.db" goto previous
set "AOI_SIM_IMAGE_ROOT=%IMAGES%"
set "AOI_SIM_IMAGE_LAYOUT=FLAT"
echo.
echo SIM_ONLY real PNG folder: %AOI_SIM_IMAGE_ROOT%
echo CH01..CH07 + LINE-S-8 + LINE-M-2 validated.
echo 5 virtual pieces will each inspect these 9 actual PNG inputs.
echo Real OpenCV contour defects can produce NG / simulated REJECT.
echo No actual camera/PLC operation; do not use for factory production.
call "%BASE%Launch-SIMFULL.cmd"
exit /b %ERRORLEVEL%
:previous
echo Previous virtual production evidence found: sim-full-data.
echo Archive the previous folder manually for a NEW independent offline batch.
pause
exit /b 3
:unavailable
echo Missing Launch-SIMFULL.cmd; extract the complete package.
pause
exit /b 2
:missing
echo Input image folder incomplete or unavailable.
echo Require CH01.png to CH07.png plus LINE-S-8.png and LINE-M-2.png.
echo 8-bit PNG expected. OpenCV will reject unsupported formats or broken PNG.
pause
exit /b 1
