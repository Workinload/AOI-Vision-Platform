#requires -Version 5.1
<#
.SYNOPSIS
  Negative tests for the SIM_FULL launcher diagnostics.
.DESCRIPTION
  Every case builds an isolated sandbox copy of Launch-SIMFULL.ps1 with dummy package files
  and asserts the documented exit code. No AOI Core, PLC or UI process is started by these
  cases, and no production data is ever created, deleted or modified.
  Case 1 missing UI executable          -> 2
  Case 2 previous SIM_FULL WAL present  -> 3  (and the WAL file must still exist afterwards)
  Case 3 required local port occupied   -> 4
  Case 4 real PNG folder incomplete     -> 7
  Case 5 invalid executable create      -> 6 (unexpected Start-Process exception must not report success)
  Case 6 orphaned SQLite -wal only       -> 3 (nothing overwritten)
#>
[CmdletBinding()]
param([string]$EvidenceRoot = (Join-Path $env:TEMP ('AOI_LauncherDiag_' + [guid]::NewGuid().ToString('N'))))

$ErrorActionPreference = 'Stop'
$script:source = Join-Path $PSScriptRoot 'Launch-SIMFULL.ps1'
if (-not (Test-Path $script:source)) { throw ('launcher not found: ' + $script:source) }
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$results = New-Object System.Collections.ArrayList
function New-Sandbox {
    param([string]$Name)
    $dir = Join-Path $evidence $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($sub in @('Host', 'UI')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir $sub) | Out-Null
    }
    Set-Content -Path (Join-Path $dir 'Host\AOI.Host.exe') -Value 'dummy' -Encoding ascii
    Set-Content -Path (Join-Path $dir 'UI\AOI.Ui.Avalonia.exe') -Value 'dummy' -Encoding ascii
    Copy-Item $script:source (Join-Path $dir 'Launch-SIMFULL.ps1') -Force
    return $dir
}
function Invoke-Launcher {
    param([string]$Sandbox, [string[]]$Extra = @())
    $scriptPath = Join-Path $Sandbox 'Launch-SIMFULL.ps1'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) + $Extra
    $out = & powershell.exe @argList 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
}
function Add-Result {
    param([string]$Case, [int]$Expected, [int]$Actual, [bool]$Pass, [string]$Note)
    $null = $results.Add([pscustomobject]@{ Case = $Case; Expected = $Expected; Actual = $Actual; Pass = $Pass; Note = $Note })
    $flag = 'FAIL'
    if ($Pass) { $flag = 'PASS' }
    Write-Host ('[' + $flag + '] ' + $Case + ' expected=' + $Expected + ' actual=' + $Actual + ' ' + $Note)
}

# ---- Case 1: incomplete package
$sb = New-Sandbox 'case1-missing-ui'
Remove-Item (Join-Path $sb 'UI\AOI.Ui.Avalonia.exe') -Force
$r = Invoke-Launcher $sb
Add-Result 'missing UI executable' 2 $r.ExitCode ($r.ExitCode -eq 2) 'package incomplete'
$null = New-Item -ItemType Directory -Force -Path (Join-Path $evidence 'logs')

# ---- Case 2: previous WAL hold
$sb = New-Sandbox 'case2-wal-hold'
$walDir = Join-Path $sb 'sim-full-data\master'
New-Item -ItemType Directory -Force -Path $walDir | Out-Null
$wal = Join-Path $walDir 'production.db'
Set-Content -Path $wal -Value 'previous-evidence' -Encoding ascii
$r = Invoke-Launcher $sb
$walKept = Test-Path $wal
Add-Result 'previous SIM_FULL WAL hold' 3 $r.ExitCode ($r.ExitCode -eq 3) ('wal kept=' + $walKept)
if (-not $walKept) { throw 'launcher deleted previous production WAL - forbidden' }

# ---- Case 3: port occupied
$sb = New-Sandbox 'case3-port-busy'
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 19621)
$listener.Start()
try {
    $r = Invoke-Launcher $sb
    Add-Result 'required port occupied' 4 $r.ExitCode ($r.ExitCode -eq 4) 'port 19621 busy'
}
finally { $listener.Stop() }

# ---- Case 4: incomplete image folder
$sb = New-Sandbox 'case4-bad-images'
$img = Join-Path $evidence 'images-incomplete'
New-Item -ItemType Directory -Force -Path $img | Out-Null
Set-Content -Path (Join-Path $img 'CH01.png') -Value 'x' -Encoding ascii
$r = Invoke-Launcher $sb @('-ImageRoot', ('"' + $img + '"'))
Add-Result 'invalid real PNG folder' 7 $r.ExitCode ($r.ExitCode -eq 7) 'only CH01.png present'

# ---- Case 5: an executable file exists but is not a runnable native binary.
# This tests the fail-closed exceptional cleanup path; it must exit 6,
# never report AOI_SIM_FULL_LAUNCH_PASS and never leave any AOI child.
$sb = New-Sandbox 'case5-broken-executable'
$r = Invoke-Launcher $sb
$ok = ($r.ExitCode -eq 6 -and
    $r.Output -match 'AOI_SIM_FULL_LAUNCH_ERROR' -and
    $r.Output -notmatch 'AOI_SIM_FULL_LAUNCH_PASS')
Add-Result 'invalid native executable cannot strand launcher' 6 $r.ExitCode $ok 'exception translated to exit 6'

# ---- Case 6: SQLite orphan WAL without a production.db main file.
# A disk-crash remnant is not a new safe production data directory.
$sb = New-Sandbox 'case6-orphan-wal'
$walDir = Join-Path $sb 'sim-full-data\slave'
New-Item -ItemType Directory -Force -Path $walDir | Out-Null
$orphan = Join-Path $walDir 'production.db-wal'
Set-Content -Path $orphan -Value 'orphaned-previous-wire-evidence' -Encoding ascii
$r = Invoke-Launcher $sb
$kept = ((Test-Path $orphan) -and
    ((Get-Content $orphan -Raw) -match 'orphaned-previous-wire-evidence'))
Add-Result 'orphan SQLite WAL cannot be mistaken for fresh data' 3 $r.ExitCode ($r.ExitCode -eq 3 -and $kept) ('raw WAL intact=' + $kept)


# ---- Case 5: SIM11 profile (SIM_LIGHT, sim-data) missing Diagnostics executable
$sb = New-Sandbox 'case5-sim11-missing-diagnostics'
$r = Invoke-Launcher $sb @('-SimMode', 'SIM_LIGHT', '-DataRoot', 'sim-data', '-ItemCount', '1', '-MaxInFlight', '1', '-AdditionalRequired', 'Diagnostics\AOI.Diagnostics.Cli.exe')
Add-Result 'SIM11 missing Diagnostics executable' 2 $r.ExitCode ($r.ExitCode -eq 2) 'SIM_LIGHT profile'

# ---- Case 6: SIM11 profile holds on its own sim-data WAL and never touches sim-full-data
$sb = New-Sandbox 'case6-sim11-wal-hold'
New-Item -ItemType Directory -Force -Path (Join-Path $sb 'Diagnostics') | Out-Null
Set-Content -Path (Join-Path $sb 'Diagnostics\AOI.Diagnostics.Cli.exe') -Value 'dummy' -Encoding ascii
$simData = Join-Path $sb 'sim-data\master'
New-Item -ItemType Directory -Force -Path $simData | Out-Null
$wal = Join-Path $simData 'production.db'
Set-Content -Path $wal -Value 'sim-light-evidence' -Encoding ascii
$fullMarker = Join-Path $sb 'sim-full-data\master'
New-Item -ItemType Directory -Force -Path $fullMarker | Out-Null
Set-Content -Path (Join-Path $fullMarker 'production.db') -Value 'sim-full-evidence' -Encoding ascii
$r = Invoke-Launcher $sb @('-SimMode', 'SIM_LIGHT', '-DataRoot', 'sim-data', '-ItemCount', '1', '-MaxInFlight', '1', '-AdditionalRequired', 'Diagnostics\AOI.Diagnostics.Cli.exe')
$keptSim = ((Get-Content $wal -Raw) -match 'sim-light-evidence')
$keptFull = ((Get-Content (Join-Path $fullMarker 'production.db') -Raw) -match 'sim-full-evidence')
Add-Result 'SIM11 sim-data WAL hold is isolated from sim-full-data' 3 $r.ExitCode ($r.ExitCode -eq 3 -and $keptSim -and $keptFull) ('sim-data intact=' + $keptSim + ' sim-full-data intact=' + $keptFull)

# ---- Case 7: SIM11 profile refuses a busy Core port exactly like SIM_FULL
$sb = New-Sandbox 'case7-sim11-port-busy'
New-Item -ItemType Directory -Force -Path (Join-Path $sb 'Diagnostics') | Out-Null
Set-Content -Path (Join-Path $sb 'Diagnostics\AOI.Diagnostics.Cli.exe') -Value 'dummy' -Encoding ascii
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 19622)
$listener.Start()
try {
    $r = Invoke-Launcher $sb @('-SimMode', 'SIM_LIGHT', '-DataRoot', 'sim-data', '-ItemCount', '1', '-MaxInFlight', '1', '-AdditionalRequired', 'Diagnostics\AOI.Diagnostics.Cli.exe')
    Add-Result 'SIM11 requires free Slave IPC port' 4 $r.ExitCode ($r.ExitCode -eq 4) 'port 19622 busy'
}
finally { $listener.Stop() }

$failed = @($results | Where-Object { -not $_.Pass })
$summary = Join-Path $evidence 'launcher-diagnostics.json'
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $summary -Encoding UTF8
Write-Host ''
Write-Host ('AOI_LAUNCHER_DIAGNOSTICS total=' + $results.Count + ' failed=' + $failed.Count)
Write-Host ('evidence: ' + $summary)
if ($failed.Count -gt 0) { exit 1 }
exit 0
