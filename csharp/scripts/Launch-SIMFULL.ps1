#requires -Version 5.1
<#
.SYNOPSIS
  AOI SIM_FULL five-process launcher with real readiness, diagnostics and log capture.
.DESCRIPTION
  Starts vPLC -> Slave Core -> Master Core -> Master UI -> Slave UI and only reports success
  after each Core IPC endpoint is actually accepting connections and every child process is
  still alive. Everything is written to a log file under sim-full-data\launch-logs.

  Never deletes, overwrites or restores production data. If a previous SIM_FULL SQLite WAL is
  present the launcher holds and exits non-zero so the operator can archive it manually.

  Exit codes:
    0  all five processes started and every readiness probe passed
    2  package incomplete (missing executable) or launcher script missing
    3  previous SIM_FULL WAL present - hold, nothing started, nothing deleted
    4  a required local port is already in use - another run is active
    5  Core IPC readiness timeout
    6  a child process exited unexpectedly during startup
    7  real-PNG image folder invalid or incomplete
    8  UI process exited after starting
    9  launch log could not be created
.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Launch-SIMFULL.ps1
.EXAMPLE
  .\Launch-SIMFULL.ps1 -ImageRoot 'D:\samples\AOI_PNG' -ReadyTimeoutSeconds 60
#>
[CmdletBinding()]
param(
    [string]$ImageRoot = $env:AOI_SIM_IMAGE_ROOT,
    [ValidateRange(5, 600)][int]$ReadyTimeoutSeconds = 45,
    [ValidateRange(1, 60)][int]$UiAliveSeconds = 5
)

$ErrorActionPreference = 'Stop'
$script:Base = $PSScriptRoot
$script:Data = Join-Path $script:Base 'sim-full-data'
$script:Started = New-Object System.Collections.ArrayList
$script:LogPath = $null

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Level, $Message
    Write-Host $line
    if ($script:LogPath) {
        try { Add-Content -Path $script:LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

function New-LaunchLog {
    $packageRoot = Split-Path $script:Base -Parent
    $candidates = @($script:Data, (Join-Path $packageRoot 'sim-full-data'), $env:TEMP)
    foreach ($dir in $candidates) {
        try {
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
            $logDir = Join-Path $dir 'launch-logs'
            if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
            $path = Join-Path $logDir ('launch-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
            Set-Content -Path $path -Value ('AOI SIM_FULL launch log ' + (Get-Date -Format 'o')) -Encoding UTF8
            return $path
        }
        catch { continue }
    }
    return $null
}

function Stop-StartedProcesses {
    foreach ($entry in $script:Started) {
        $proc = $entry.Process
        try {
            if ($proc -and -not $proc.HasExited) {
                Write-Log ('stopping ' + $entry.Name + ' (pid ' + $proc.Id + ') started by this launcher') 'WARN'
                $proc.Kill()
                $proc.WaitForExit(5000) | Out-Null
            }
        }
        catch { Write-Log ('could not stop ' + $entry.Name + ': ' + $_.Exception.Message) 'WARN' }
    }
}

function Test-TcpPortFree {
    param([int]$Port)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if ($task.Wait(600) -and $client.Connected) { return $false }
        return $true
    }
    catch { return $true }
    finally { $client.Close() }
}

function Wait-TcpReady {
    param([int]$Port, [int]$TimeoutSeconds, [string]$Label)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $task = $client.ConnectAsync('127.0.0.1', $Port)
            if ($task.Wait(800) -and $client.Connected) {
                Write-Log ($Label + ' ready on 127.0.0.1:' + $Port + ' after ' +
                    [int]((Get-Date) - $deadline.AddSeconds(-$TimeoutSeconds)).TotalSeconds + 's')
                return $true
            }
        }
        catch { }
        finally { $client.Close() }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Start-AoiProcess {
    param([string]$Name, [string]$FilePath, [string]$Arguments, [string]$WorkingDirectory)
    Write-Log ('starting ' + $Name + ': ' + $FilePath + ' ' + $Arguments)
    $proc = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -PassThru
    if (-not $proc) { throw ('Start-Process returned no process for ' + $Name) }
    $null = $script:Started.Add([pscustomobject]@{ Name = $Name; Process = $proc })
    return $proc
}

function Assert-Alive {
    param([System.Diagnostics.Process]$Process, [string]$Name, [int]$ExitCode)
    if ($Process.HasExited) {
        Write-Log ($Name + ' exited during startup with code ' + $Process.ExitCode) 'ERROR'
        Stop-StartedProcesses
        exit $ExitCode
    }
}

function Test-ImageFolder {
    param([string]$Root)
    if ([string]::IsNullOrWhiteSpace($Root)) { return $true }
    if (-not (Test-Path $Root)) {
        Write-Log ('image folder not found: ' + $Root) 'ERROR'
        return $false
    }
    foreach ($n in 1..7) {
        $f = Join-Path $Root ('CH{0:D2}.png' -f $n)
        if (-not (Test-Path $f)) { Write-Log ('missing ' + $f) 'ERROR'; return $false }
    }
    foreach ($line in @('LINE-S-8.png', 'LINE-M-2.png')) {
        $f = Join-Path $Root $line
        if (-not (Test-Path $f)) { Write-Log ('missing ' + $f) 'ERROR'; return $false }
    }
    Write-Log ('real PNG folder validated: ' + $Root + ' (CH01..CH07 + LINE-S-8 + LINE-M-2)')
    return $true
}

# ---------------------------------------------------------------- preflight
try { $script:LogPath = New-LaunchLog } catch { $script:LogPath = $null }
if (-not $script:LogPath) {
    Write-Host 'ERROR: launch log could not be created; refusing to start blind.' -ForegroundColor Red
    exit 9
}
Write-Log ('launcher: ' + $PSCommandPath)
Write-Log ('package : ' + $script:Base)
Write-Log ('log     : ' + $script:LogPath)

$hostExe = Join-Path $script:Base 'Host\AOI.Host.exe'
$uiExe = Join-Path $script:Base 'UI\AOI.Ui.Avalonia.exe'
foreach ($required in @($hostExe, $uiExe)) {
    if (-not (Test-Path $required)) {
        Write-Log ('package incomplete, missing: ' + $required) 'ERROR'
        Write-Log 'Extract the entire Windows x64 package; a partial copy cannot start.' 'ERROR'
        exit 2
    }
}
Write-Log 'dependency check passed: Host\AOI.Host.exe + UI\AOI.Ui.Avalonia.exe present'

foreach ($wal in @((Join-Path $script:Data 'master\production.db'), (Join-Path $script:Data 'slave\production.db'))) {
    if (Test-Path $wal) {
        Write-Log ('previous SIM_FULL WAL present: ' + $wal) 'ERROR'
        Write-Log 'SIM_FULL_RESTART_HOLD: nothing was started and nothing was deleted.' 'ERROR'
        Write-Log 'Archive or move sim-full-data manually before starting a new independent batch.' 'ERROR'
        exit 3
    }
}
Write-Log ('previous production WAL: none under ' + $script:Data)

$udpPort = 19600
$tcpPorts = @(19610, 19621, 19622, 19651)
$busy = @()
foreach ($p in $tcpPorts) { if (-not (Test-TcpPortFree $p)) { $busy += $p } }
if ($busy.Count -gt 0) {
    Write-Log ('local ports already in use: ' + ($busy -join ', ')) 'ERROR'
    Write-Log 'Another AOI run is active. Close all five processes before starting a new one.' 'ERROR'
    exit 4
}
Write-Log ('port check passed: ' + ($tcpPorts -join ', ') + ' free; vPLC UDP ' + $udpPort + ' is local-only')

if (-not (Test-ImageFolder -Root $ImageRoot)) { exit 7 }

if (-not (Test-Path $script:Data)) { New-Item -ItemType Directory -Force -Path $script:Data | Out-Null }

$env:AOI_SIM_MODE = 'SIM_FULL'
$env:AOI_SIM_ITEM_COUNT = '5'
$env:AOI_SIM_MAX_IN_FLIGHT = '3'
$env:AOI_SIM_CHANNEL_SET = 'ALL_11'
if (-not $env:AOI_SIM_CONFIG_TOKEN) { $env:AOI_SIM_CONFIG_TOKEN = [guid]::NewGuid().ToString('N') }
if (-not [string]::IsNullOrWhiteSpace($ImageRoot)) {
    $env:AOI_SIM_IMAGE_ROOT = $ImageRoot
    $env:AOI_SIM_IMAGE_LAYOUT = 'FLAT'
    $env:AOI_SIM_PLC_PROFILE = 'LEGACY_PLC_SHADOW'
    $env:AOI_SIM_LINE_ROW_MAPPING = 'LEGACY_DROP_ROW0'
    Write-Log 'real PNG profile: LEGACY_PLC_SHADOW, line row mapping LEGACY_DROP_ROW0 (FIELD_PENDING)'
}
Write-Log 'SIM_ONLY enforced: 127.0.0.1 only, real PLC/camera writes disabled, 5 items max 3 in flight'

# ---------------------------------------------------------------- start
$sw = [Diagnostics.Stopwatch]::StartNew()
$vplc = Start-AoiProcess -Name 'vPLC' -FilePath $hostExe -Arguments ('vplc --data "' + $script:Data + '"') -WorkingDirectory (Join-Path $script:Base 'Host')
Start-Sleep -Milliseconds 1500
Assert-Alive -Process $vplc -Name 'vPLC' -ExitCode 6
Write-Log 'vPLC alive after 1.5s (UDP 19600 loopback only)'

$slave = Start-AoiProcess -Name 'Slave Core' -FilePath $hostExe -Arguments ('slave --data "' + $script:Data + '"') -WorkingDirectory (Join-Path $script:Base 'Host')
if (-not (Wait-TcpReady -Port 19622 -TimeoutSeconds $ReadyTimeoutSeconds -Label 'Slave Core IPC')) {
    Write-Log ('Slave Core IPC 19622 not ready within ' + $ReadyTimeoutSeconds + 's') 'ERROR'
    Stop-StartedProcesses; exit 5
}
Assert-Alive -Process $slave -Name 'Slave Core' -ExitCode 6

$master = Start-AoiProcess -Name 'Master Core' -FilePath $hostExe -Arguments ('master --data "' + $script:Data + '"') -WorkingDirectory (Join-Path $script:Base 'Host')
if (-not (Wait-TcpReady -Port 19621 -TimeoutSeconds $ReadyTimeoutSeconds -Label 'Master Core snapshot IPC')) {
    Write-Log ('Master Core snapshot IPC 19621 not ready within ' + $ReadyTimeoutSeconds + 's') 'ERROR'
    Stop-StartedProcesses; exit 5
}
if (-not (Wait-TcpReady -Port 19651 -TimeoutSeconds $ReadyTimeoutSeconds -Label 'Master report IPC')) {
    Write-Log ('Master report IPC 19651 not ready within ' + $ReadyTimeoutSeconds + 's') 'ERROR'
    Stop-StartedProcesses; exit 5
}
Assert-Alive -Process $master -Name 'Master Core' -ExitCode 6
Write-Log ('both Cores ready after ' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + 's')

$masterUi = Start-AoiProcess -Name 'Master UI' -FilePath $uiExe -Arguments '--master' -WorkingDirectory (Join-Path $script:Base 'UI')
Start-Sleep -Milliseconds 800
$slaveUi = Start-AoiProcess -Name 'Slave UI' -FilePath $uiExe -Arguments '--slave' -WorkingDirectory (Join-Path $script:Base 'UI')

Start-Sleep -Seconds $UiAliveSeconds
Assert-Alive -Process $masterUi -Name 'Master UI' -ExitCode 8
Assert-Alive -Process $slaveUi -Name 'Slave UI' -ExitCode 8
Write-Log ('both UIs alive after ' + $UiAliveSeconds + 's')

$final = @($slave, $master, $masterUi, $slaveUi, $vplc) | Where-Object { $_ -and -not $_.HasExited }
Write-Log ''
Write-Log ('AOI_SIM_FULL_LAUNCH_PASS processes=' + $final.Count + '/5 elapsed=' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + 's')
foreach ($entry in $script:Started) {
    $state = 'alive'
    if ($entry.Process.HasExited) { $state = 'EXITED code ' + $entry.Process.ExitCode }
    Write-Log ('  ' + $entry.Name.PadRight(12) + ' pid=' + $entry.Process.Id + '  ' + $state)
}
Write-Log ('data   : ' + $script:Data)
Write-Log ('log    : ' + $script:LogPath)
Write-Log 'Real PLC and camera control remain DISABLED. This is SIM_ONLY local validation.'
exit 0
