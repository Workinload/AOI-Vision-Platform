#requires -Version 5.1
<#
.SYNOPSIS
  Product release -> dual-machine safe activation -> five-process run -> evidence smoke.
.DESCRIPTION
  Closes the one automation gap the existing A5 Windows operator smoke does not cover: it drives
  a SIM_ONLY product solution through bootstrap -> publish -> dual-machine activate -> real
  five-process run -> screenshot -> WAL/IPC evidence, and fails loudly at the first step that
  does not produce its documented marker.

  Reuses, not reimplements:
    * csharp/scripts/Launch-SIMFULL.ps1   (five-process start + readiness + log)
    * csharp/src/AOI.Solutions.Cli        (bootstrap / publish / activate / status)
    * csharp/qa/Windows-Operator-Smoke.ps1 (kept for the deeper UIA click suite)

  SIM_ONLY: loopback vPLC only, no real PLC or camera writes, real_writes stays 0.
  Exit codes:
    0  activation + five-process run + evidence all verified
    10 bootstrap failed        11 publish failed       12 dual activation failed
    13 five-process launch failed   14 no production recorded   15 workpieces carried another release
    16 package incomplete
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [string]$SolutionId = 'SIM_RELEASE_SMOKE',
    [string]$Actor = 'SIM_OPERATOR_SMOKE',
    [ValidateRange(30, 600)][int]$ProductionTimeoutSeconds = 180,
    [string]$EvidenceRoot = (Join-Path $env:TEMP ('AOI_ReleaseSmoke_' + [guid]::NewGuid().ToString('N'))),
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$script:root = [IO.Path]::GetFullPath($PackageRoot)
$script:evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$script:launcher = Join-Path $script:root 'Launch-SIMFULL.ps1'
$script:recipes = Join-Path $script:root 'Recipes\AOI.Solutions.Cli.exe'
$script:data = Join-Path $script:root 'sim-full-data'

function Say { param([string]$m, [string]$lvl = 'INFO') Write-Host ((Get-Date -Format 'HH:mm:ss') + ' [' + $lvl + '] ' + $m) }

foreach ($p in @($script:launcher, $script:recipes, (Join-Path $script:root 'Host\AOI.Host.exe'), (Join-Path $script:root 'UI\AOI.Ui.Avalonia.exe'))) {
    if (-not (Test-Path $p)) { Say ('package incomplete, missing ' + $p) 'ERROR'; exit 16 }
}
New-Item -ItemType Directory -Force -Path $script:evidence | Out-Null
Say ('package : ' + $script:root)
Say ('evidence: ' + $script:evidence)

if (-not (Test-Path $script:data)) { New-Item -ItemType Directory -Force -Path $script:data | Out-Null }
foreach ($node in @('master', 'slave')) {
    if (Test-Path (Join-Path $script:data ($node + '\production.db'))) {
        Say ('previous ' + $node + ' production WAL present; archive the WHOLE sim-full-data before a release smoke') 'ERROR'
        exit 14
    }
}

function Invoke-Recipe {
    param([string[]]$Arguments)
    $out = & $script:recipes @Arguments 2>&1 | Out-String
    $script:LastRecipeExitCode = $LASTEXITCODE
    ($out.Trim() -split '\r?\n') | ForEach-Object { Say ('  recipe> ' + $_) }
    return $out
}

Say 'step 1/6 product bootstrap'
$b = Invoke-Recipe @('bootstrap', $script:data, $SolutionId, 'SIM Release Smoke Product', $Actor)
if ($script:LastRecipeExitCode -ne 0 -or $b -notmatch 'SIM_PRODUCT_BOOTSTRAP_OK') { Say 'bootstrap did not report SIM_PRODUCT_BOOTSTRAP_OK' 'ERROR'; exit 10 }

Say 'step 2/6 publish immutable release'
$p = Invoke-Recipe @('publish', $script:data, $SolutionId, $Actor)
$m = [regex]::Match($p, 'SIM_PRODUCT_PUBLISHED release=(\S+) sha256=(\S+)')
if ($script:LastRecipeExitCode -ne 0 -or -not $m.Success) { Say 'publish did not report SIM_PRODUCT_PUBLISHED' 'ERROR'; exit 11 }
$releaseId = $m.Groups[1].Value
Say ('published release ' + $releaseId)

Say 'step 3/6 dual-machine safe activation'
$a = Invoke-Recipe @('activate', $script:data, $releaseId, $Actor)
if ($script:LastRecipeExitCode -ne 0 -or $a -notmatch 'SIM_PRODUCT_DUAL_COMMITTED_HOLD' -or
    $a -notmatch 'nodes=2' -or $a -notmatch 'real_writes=0') { Say 'activation did not reach DUAL_COMMITTED_HOLD' 'ERROR'; exit 12 }
$e = [regex]::Match($a, 'epoch=(\d+)')
$epoch = $null
if ($e.Success) { $epoch = [int]$e.Groups[1].Value }
Say ('dual activation committed, epoch=' + $epoch + ', real_writes=0')

Say 'step 4/6 product status readback'
$s = Invoke-Recipe @('status', $script:data)
$masterMatch = [regex]::Match($s, 'master=(\S+) m_epoch=(\d+)')
$slaveMatch = [regex]::Match($s, 'slave=(\S+) s_epoch=(\d+)')
if ($script:LastRecipeExitCode -ne 0 -or -not $masterMatch.Success -or
    -not $slaveMatch.Success -or
    $masterMatch.Groups[1].Value -ne $releaseId -or
    $slaveMatch.Groups[1].Value -ne $releaseId -or
    [int]$masterMatch.Groups[2].Value -ne $epoch -or
    [int]$slaveMatch.Groups[2].Value -ne $epoch -or
    $s -notmatch 'real_writes=0') {
    Say 'dual-Core status did not prove the SAME committed ReleaseId/Epoch with zero real PLC writes' 'ERROR'
    exit 12
}

function Stop-SmokeProcesses {
    # Exact package directory boundary, not prefix wildcards such as AOI*.
    $prefix = $script:root.TrimEnd('\') + '\'
    Get-Process AOI.Ui.Avalonia, AOI.Host -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Path -and $_.Path.StartsWith($prefix,
                [System.StringComparison]::OrdinalIgnoreCase)
        } | Stop-Process -Force
}

Say 'step 5/6 starting five processes through Launch-SIMFULL.ps1'
$launchOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script:launcher 2>&1 | Out-String
$launcherExit = $LASTEXITCODE
($launchOut.Trim() -split '\r?\n') | ForEach-Object { Say ('  launch> ' + $_) }
Set-Content -Path (Join-Path $script:evidence 'launch.log') -Value $launchOut -Encoding UTF8
if ($launcherExit -ne 0 -or $launchOut -notmatch 'AOI_SIM_FULL_LAUNCH_PASS') { Say 'five-process launch failed' 'ERROR'; exit 13 }

Say 'step 6/6 waiting for real production under the activated release'
$deadline = (Get-Date).AddSeconds($ProductionTimeoutSeconds)
$snapshot = $null
while ((Get-Date) -lt $deadline) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', 19621)
        $reader = New-Object IO.StreamReader($client.GetStream())
        $line = $reader.ReadLine()
        $client.Close()
        if ($line) {
            $candidate = $line | ConvertFrom-Json
            if ($candidate.production -and $candidate.production.total -ge 5) { $snapshot = $candidate; break }
        }
    }
    catch { }
    Start-Sleep -Milliseconds 1500
}
if (-not $snapshot) {
    Say ('no production recorded within ' + $ProductionTimeoutSeconds + 's') 'ERROR'
    if (-not $KeepRunning) { Stop-SmokeProcesses }
    exit 14
}
if ($snapshot.simulationMode -ne 'SIM_FULL' -or $snapshot.realHardwareWritesEnabled -ne $false) {
    Say 'Core snapshot does NOT prove SIM_FULL and disabled real hardware writes' 'ERROR'
    if (-not $KeepRunning) { Stop-SmokeProcesses }
    exit 15
}

$recent = @($snapshot.production.recent | Where-Object { $null -ne $_ })
$wrong = @($recent | Where-Object { $_.releaseId -ne $releaseId })
$identities = @($recent | ForEach-Object { $_.workpieceId } | Select-Object -Unique)
if ($recent.Count -lt 5 -or $identities.Count -ne $recent.Count -or
    @($identities | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    Say 'production total alone is NOT proof: need 5 distinct per-WorkpieceId release records' 'ERROR'
    if (-not $KeepRunning) { Stop-SmokeProcesses }
    exit 15
}
Say ('production total=' + $snapshot.production.total + ' good=' + $snapshot.production.good + ' unfinished=' + $snapshot.production.unfinished)
Say ('workpieces carrying activated release: ' + ($recent.Count - $wrong.Count) + '/' + $recent.Count)
if ($wrong.Count -gt 0) {
    Say ('workpieces on another release: ' + (($wrong | ForEach-Object { $_.releaseId }) -join ', ')) 'ERROR'
    if (-not $KeepRunning) { Stop-SmokeProcesses }
    exit 15
}

$shot = Join-Path $script:evidence 'desktop.png'
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $bnd = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $bnd.Width, $bnd.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bnd.Left, $bnd.Top, 0, 0, $bmp.Size)
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Say ('screenshot: ' + $shot)
}
catch { Say ('screenshot failed: ' + $_.Exception.Message) 'WARN' }

$summary = [pscustomobject]@{
    result = 'PASS'
    solutionId = $SolutionId
    releaseId = $releaseId
    epoch = $epoch
    productionTotal = $snapshot.production.total
    productionGood = $snapshot.production.good
    releasesSeen = @($recent | ForEach-Object { $_.releaseId } | Select-Object -Unique)
    realWritesEnabled = $snapshot.realHardwareWritesEnabled
    simulationMode = $snapshot.simulationMode
    evidenceRoot = $script:evidence
}
$jsonPath = Join-Path $script:evidence 'release-smoke.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path $jsonPath -Encoding UTF8
Say ('SIM_RELEASE_SMOKE_PASS release=' + $releaseId + ' epoch=' + $epoch + ' total=' + $snapshot.production.total)

if (-not $KeepRunning) {
    Stop-SmokeProcesses
    Say 'five processes stopped (only binaries inside this exact package root)'
}
exit 0
