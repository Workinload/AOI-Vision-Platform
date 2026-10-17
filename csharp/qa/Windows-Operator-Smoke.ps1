#requires -Version 5.1
<#
.SYNOPSIS
  Agent05 Windows x64 REAL DESKTOP operator smoke for a published SIM_ONLY zip.
.DESCRIPTION
  Launches the shipped native EXEs (never Avalonia Headless), proves five pieces/11
  channels from both Core IPC snapshots AND independent redacted SQLite capture,
  clicks real desktop controls using Windows UI Automation + Win32 mouse,
  saves per-window screenshots and a machine-readable result, then cleans up
  only the processes it started. DOES NOT connect to any external hardware.
.EXAMPLE
  powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageZip .\AOI_SIM11_Windows_Preview.zip
.EXAMPLE
  powershell.exe -NoProfile -File .\Windows-Operator-Smoke.ps1 -PackageRoot C:\AOI_SIM11 -KeepRunning
#>
[CmdletBinding(DefaultParameterSetName='Zip')]
param(
    [Parameter(Mandatory=$true,ParameterSetName='Zip')][string]$PackageZip,
    [Parameter(Mandatory=$true,ParameterSetName='Folder')][string]$PackageRoot,
    [string]$EvidenceRoot = (Join-Path $env:TEMP ('AOI_A5_WindowsSmoke_' + [guid]::NewGuid().ToString('N'))),
    [ValidateRange(30,300)][int]$TimeoutSeconds=110,
    [switch]$KeepRunning,
    [switch]$PauseForInspection
)

$ErrorActionPreference='Stop'
$script:checks=New-Object System.Collections.ArrayList
$script:processes=@{}
$script:evidence=[IO.Path]::GetFullPath($EvidenceRoot)
$script:sourceHash=$null
$script:desktopScreenshots=New-Object System.Collections.ArrayList
$script:dataRoot=$null
$script:masterSnapshot=$null
$script:slaveSnapshot=$null
$script:lastFailure=$null
$exitCode=1

function Record-Check([string]$Name,[string]$Status,[string]$Detail) {
    [void]$script:checks.Add([pscustomobject]@{
        step=$Name; status=$Status; detail=$Detail; utc=[DateTime]::UtcNow.ToString('o')
    })
    Write-Host ("A5_{0} {1}: {2}" -f $Status,$Name,$Detail)
}
function Require-Check([bool]$Condition,[string]$Name,[string]$Detail) {
    if (-not $Condition) {
        Record-Check $Name 'FAIL' $Detail
        throw ("{0}: {1}" -f $Name,$Detail)
    }
    Record-Check $Name 'PASS' $Detail
}
function Wait-Until([scriptblock]$Predicate,[string]$Purpose,[int]$Seconds) {
    $stop=[Diagnostics.Stopwatch]::StartNew()
    $last='not ready'
    while($stop.Elapsed.TotalSeconds -lt $Seconds) {
        foreach($name in @('vplc','slave','master')) {
            if($script:processes.ContainsKey($name) -and $script:processes[$name].HasExited) {
                throw ("{0} exited: {1}" -f $name,$script:processes[$name].ExitCode)
            }
        }
        try {
            if (& $Predicate) { return }
        } catch { $last=$_.Exception.Message }
        Start-Sleep -Milliseconds 350
    }
    throw ("Timeout waiting for {0}. Last: {1}" -f $Purpose,$last)
}
function Get-CoreSnapshot([int]$Port) {
    $tcp=[Net.Sockets.TcpClient]::new()
    try {
        $async=$tcp.BeginConnect('127.0.0.1',$Port,$null,$null)
        if(-not $async.AsyncWaitHandle.WaitOne(2000)) {
            throw ("IPC port {0} timed out" -f $Port)
        }
        $tcp.EndConnect($async)
        $tcp.ReceiveTimeout=3000
        $reader=[IO.StreamReader]::new($tcp.GetStream(),[Text.Encoding]::UTF8)
        $line=$reader.ReadLine()
        if([string]::IsNullOrWhiteSpace($line)) { throw 'IPC snapshot empty' }
        return ($line | ConvertFrom-Json)
    } finally { $tcp.Dispose() }
}
function Receive-Exact([IO.Stream]$Stream,[int]$Count) {
    $bytes=New-Object byte[] $Count
    $read=0
    while($read -lt $Count) {
        $n=$Stream.Read($bytes,$read,$Count-$read)
        if($n -le 0) { throw 'Incomplete frame preview IPC' }
        $read += $n
    }
    return ,$bytes
}
function Save-Preview([int]$Port,[string]$WorkpieceId,[string]$Channel,[string]$File) {
    $tcp=[Net.Sockets.TcpClient]::new()
    try {
        $tcp.Connect('127.0.0.1',$Port)
        $tcp.ReceiveTimeout=4000
        $tcp.SendTimeout=4000
        $stream=$tcp.GetStream()
        $request=[Text.Encoding]::ASCII.GetBytes($WorkpieceId+'|'+$Channel+[char]10)
        $stream.Write($request,0,$request.Length)
        $head=Receive-Exact $stream 4
        $length=([int]$head[0] -shl 24) -bor ([int]$head[1] -shl 16) -bor ([int]$head[2] -shl 8) -bor [int]$head[3]
        if($length -lt 16 -or $length -gt 2097152) {
            throw ("Invalid real Core preview length: {0}" -f $length)
        }
        $png=Receive-Exact $stream $length
        $prefix=[BitConverter]::ToString($png[0..7])
        if($prefix -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Preview is not a PNG' }
        [IO.File]::WriteAllBytes($File,$png)
    } finally { $tcp.Dispose() }
}
function Assert-PortUnused([int]$Port) {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port)
    try { $listener.Start() } finally { $listener.Stop() }
}
function Start-AoiProcess([string]$Name,[string]$Exe,[string]$Args) {
    $out=Join-Path $script:evidence ($Name+'.stdout.log')
    $err=Join-Path $script:evidence ($Name+'.stderr.log')
    $p=Start-Process -FilePath $Exe -ArgumentList $Args -WorkingDirectory (Split-Path $Exe) -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $script:processes[$Name]=$p
    Record-Check ('process_'+$Name) 'PASS' ("PID={0} EXE={1}" -f $p.Id,$Exe)
}
function Find-Window([string]$Name,[int]$Seconds) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt $Seconds) {
        $proc=$script:processes[$Name]
        $proc.Refresh()
        if($proc.HasExited) { throw ("{0} crashed, exit={1}" -f $Name,$proc.ExitCode) }
        $hwnd=$proc.MainWindowHandle
        if($hwnd -ne [IntPtr]::Zero -and [AoiWin]::IsWindowVisible($hwnd)) {
            $rect=New-Object AoiRect
            if([AoiWin]::GetWindowRect($hwnd,[ref]$rect) -and
                    ($rect.Right-$rect.Left) -ge 500 -and ($rect.Bottom-$rect.Top) -ge 400) {
                return $hwnd
            }
        }
        Start-Sleep -Milliseconds 300
    }
    throw ("No visible native desktop window for "+$Name)
}
function Screenshot([IntPtr]$Hwnd,[string]$Name) {
    $rect=New-Object AoiRect
    if(-not [AoiWin]::GetWindowRect($Hwnd,[ref]$rect)) { throw 'Cannot read native window position' }
    [void][AoiWin]::SetForegroundWindow($Hwnd)
    Start-Sleep -Milliseconds 400
    $screen=[Windows.Forms.SystemInformation]::VirtualScreen
    $x=[Math]::Max($rect.Left,$screen.Left)
    $y=[Math]::Max($rect.Top,$screen.Top)
    $width=[Math]::Min($rect.Right,$screen.Right)-$x
    $height=[Math]::Min($rect.Bottom,$screen.Bottom)-$y
    if($width -lt 450 -or $height -lt 300) { throw 'Window is off-screen/clipped: screenshot not proof' }
    $bmp=[Drawing.Bitmap]::new($width,$height)
    $g=[Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($x,$y,0,0,[Drawing.Size]::new($width,$height))
        $file=Join-Path $script:evidence ($Name+'.png')
        $bmp.Save($file,[Drawing.Imaging.ImageFormat]::Png)
        [void]$script:desktopScreenshots.Add($file)
    } finally { $g.Dispose(); $bmp.Dispose() }
}
function Elements([Windows.Automation.AutomationElement]$Root,[Windows.Automation.ControlType]$Type) {
    $filter=[Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,$Type)
    return $Root.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)
}
function Click-Element([IntPtr]$Window,[Windows.Automation.AutomationElement]$Element) {
    $box=$Element.Current.BoundingRectangle
    if($box.Width -lt 4 -or $box.Height -lt 4 -or $Element.Current.IsOffscreen) {
        throw ("Element not visible for real mouse click: "+$Element.Current.Name)
    }
    [void][AoiWin]::SetForegroundWindow($Window)
    Start-Sleep -Milliseconds 180
    [AoiWin]::Click([int]($box.Left+$box.Width/2),[int]($box.Top+$box.Height/2))
    Start-Sleep -Milliseconds 320
}
function Find-UIA([Windows.Automation.AutomationElement]$Root,[Windows.Automation.ControlType]$Type,[string]$Name) {
    $all=Elements $Root $Type
    foreach($el in $all) {
        if($el.Current.Name -eq $Name -and -not $el.Current.IsOffscreen) { return $el }
    }
    throw ("UI Automation cannot find visible "+$Name)
}
function Click-Tab([IntPtr]$Window,[string]$Name) {
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $tab=Find-UIA $root ([Windows.Automation.ControlType]::TabItem) $Name
    Click-Element $Window $tab
    $pattern=$tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    if(-not $pattern.Current.IsSelected) { throw 'Real click did not select tab '+$Name }
}
function Click-LocalChannel([IntPtr]$Window,[string]$Id) {
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $items=Elements $root ([Windows.Automation.ControlType]::ListItem)
    foreach($el in $items) {
        if(-not $el.Current.IsOffscreen -and $el.Current.Name -match ('(^|[^A-Z0-9])'+$Id+'([^A-Z0-9]|$)')) {
            Click-Element $Window $el
            return
        }
    }
    throw ("Cannot find click-target ListItem for "+$Id+"; request AutomationProperties.Name from Agent04")
}
function Click-RoiSave([IntPtr]$Window) {
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $edits=Elements $root ([Windows.Automation.ControlType]::Edit)
    $visible=@()
    foreach($e in $edits) {
        if(-not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Width -gt 25) { $visible+= $e }
    }
    if($visible.Count -lt 4) { throw 'Four ROI edit controls are not exposed to Windows UI Automation' }
    $fields=@($visible | Sort-Object { $_.Current.BoundingRectangle.Top }, { $_.Current.BoundingRectangle.Left } | Select-Object -Last 4)
    $fields=@($fields | Sort-Object { $_.Current.BoundingRectangle.Left })
    $values=@('0.15','0.15','0.70','0.70')
    for($i=0;$i -lt 4;$i++) {
        $el=$fields[$i]
        Click-Element $Window $el
        try {
            $pattern=$el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
            $pattern.SetValue($values[$i])
        } catch {
            [Windows.Forms.SendKeys]::SendWait('^a')
            [Windows.Forms.SendKeys]::SendWait($values[$i])
        }
    }
    $buttons=Elements $root ([Windows.Automation.ControlType]::Button)
    foreach($button in $buttons) {
        if($button.Current.Name -like '*校验并保存本机 ROI 草稿*' -and -not $button.Current.IsOffscreen) {
            Click-Element $Window $button
            return
        }
    }
    throw 'ROI save button not clickable'
}

try {
    if($env:OS -ne 'Windows_NT') { throw 'Windows x64 native desktop required (not headless/Linux)' }
    if(Test-Path -LiteralPath $script:evidence) {
        if(@(Get-ChildItem -LiteralPath $script:evidence -Force).Count -ne 0) {
            throw 'EvidenceRoot must be empty to avoid mixing prior runs'
        }
    } else { [void](New-Item -ItemType Directory -Path $script:evidence -Force) }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
[StructLayout(LayoutKind.Sequential)] public struct AoiRect {
    public int Left,Top,Right,Bottom;
}
public static class AoiWin {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out AoiRect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    public static void Click(int x,int y) {
        SetCursorPos(x,y);
        mouse_event(0x0002,0,0,0,UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(0x0004,0,0,0,UIntPtr.Zero);
    }
}
'@
    if($PSCmdlet.ParameterSetName -eq 'Zip') {
        $archive=[IO.Path]::GetFullPath($PackageZip)
        if(-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'PackageZip not found' }
        $script:sourceHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        $extract=Join-Path $script:evidence 'extracted'
        Expand-Archive -LiteralPath $archive -DestinationPath $extract
        $hostFiles=@(Get-ChildItem -LiteralPath $extract -Filter 'AOI.Host.exe' -File -Recurse)
        if($hostFiles.Count -ne 1 -or $hostFiles[0].Directory.Name -ne 'Host') {
            throw 'ZIP must contain one Host/AOI.Host.exe'
        }
        $package=$hostFiles[0].Directory.Parent.FullName
    } else {
        $package=(Resolve-Path -LiteralPath $PackageRoot).Path
    }
    $hostExe=Join-Path $package 'Host\AOI.Host.exe'
    $uiExe=Join-Path $package 'UI\AOI.Ui.Avalonia.exe'
    $diagExe=Join-Path $package 'Diagnostics\AOI.Diagnostics.Cli.exe'
    foreach($exe in @($hostExe,$uiExe,$diagExe)) {
        if(-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published Windows package missing '+$exe }
        if((Get-Item -LiteralPath $exe).Length -lt 20000) { throw 'Suspicious incomplete EXE '+$exe }
    }
    Require-Check $true 'win_package' ("3 real EXEs; zip-sha256="+$script:sourceHash)
    foreach($port in @(19610,19621,19622,19631,19632,19641,19642)) { Assert-PortUnused $port }
    $udp=[Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $udp.ExclusiveAddressUse=$true
        $udp.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,19600))
    } finally { $udp.Dispose() }
    Require-Check $true 'fresh_ports' 'All SIM_ONLY loopback ports unused; no existing Core or vPLC touched'
    $script:dataRoot=Join-Path $script:evidence 'sim-full-data'
    [void](New-Item -ItemType Directory -Path $script:dataRoot -Force)
    $env:AOI_SIM_MODE='SIM_FULL'
    $env:AOI_SIM_CHANNEL_SET='ALL_11'
    $env:AOI_SIM_ITEM_COUNT='5'
    $env:AOI_SIM_MAX_IN_FLIGHT='3'
    $env:AOI_SIM_CONFIG_TOKEN=[guid]::NewGuid().ToString('N')
    $arg=' --data "'+$script:dataRoot+'"'
    Start-AoiProcess 'vplc' $hostExe ('vplc'+$arg)
    Start-Sleep -Milliseconds 800
    Start-AoiProcess 'slave' $hostExe ('slave'+$arg)
    Start-Sleep -Milliseconds 350
    Start-AoiProcess 'master' $hostExe ('master'+$arg)
    Start-AoiProcess 'master-ui' $uiExe '--master'
    Start-AoiProcess 'slave-ui' $uiExe '--slave'
    Wait-Until {
        $script:masterSnapshot=Get-CoreSnapshot 19621
        $script:slaveSnapshot=Get-CoreSnapshot 19622
        $m=$script:masterSnapshot; $s=$script:slaveSnapshot
        if($m.simulationMode -ne 'SIM_FULL' -or $s.simulationMode -ne 'SIM_FULL') { return $false }
        if($m.realHardwareWritesEnabled -or $s.realHardwareWritesEnabled) { throw 'Core unexpectedly enabled real hardware writes' }
        if($null -eq $m.production -or $null -eq $s.production) { return $false }
        return ($m.production.total -eq 5 -and $m.production.good -eq 5 -and
            $m.production.unfinished -eq 0 -and $s.production.total -eq 5 -and
            $m.production.recent.Count -eq 5 -and $s.production.recent.Count -eq 5)
    } '5 finished 11-channel workpieces in Master and Slave read-only IPC' $TimeoutSeconds
    $m=$script:masterSnapshot; $s=$script:slaveSnapshot
    $masterIds=@($m.production.recent | ForEach-Object { $_.workpieceId } | Sort-Object -Unique)
    $slaveIds=@($s.production.recent | ForEach-Object { $_.workpieceId } | Sort-Object -Unique)
    Require-Check ($masterIds.Count -eq 5 -and (@(Compare-Object $masterIds $slaveIds)).Count -eq 0 -and
        @($m.production.recent | Where-Object { $_.capturedChannels -ne 11 }).Count -eq 0 -and
        @($s.production.recent | Where-Object { $_.capturedChannels -ne 6 }).Count -eq 0 -and
        $m.channels.Count -eq 11 -and $s.channels.Count -eq 11) 'five_items_11_channels' 'Master 5x11 Slave 5x6; consistent identities and both 11-tile snapshots'
    $journal=Join-Path $script:evidence 'sqlite-evidence.jsonl'
    $bundle=Join-Path $script:evidence 'sqlite-evidence.zip'
    & $diagExe capture $script:dataRoot $journal | Out-File -LiteralPath (Join-Path $script:evidence 'capture.log') -Encoding UTF8
    if($LASTEXITCODE -ne 0) { throw 'Native diagnostics CLI read-only SQLite capture failed' }
    $flight=(Get-Content -LiteralPath $journal -TotalCount 1 -Encoding UTF8 | ConvertFrom-Json).snapshot.nodes
    $mn=@($flight | Where-Object { $_.nodeId -eq 'master' })[0]
    $sn=@($flight | Where-Object { $_.nodeId -eq 'slave' })[0]
    Require-Check ($mn.databaseAvailable -and $sn.databaseAvailable -and
        $mn.workpieces.Count -eq 5 -and $sn.workpieces.Count -eq 5 -and
        $mn.results.Count -eq 55 -and $sn.results.Count -eq 30 -and
        @($mn.workpieces | Where-Object { $null -ne $_.final }).Count -eq 5) 'native_sqlite' 'Diagnostics.Cli read WAL: master=5/55 results; slave=5/30; 5 finalized'
    & $diagExe bundle $journal $bundle | Out-File -LiteralPath (Join-Path $script:evidence 'bundle.log') -Encoding UTF8
    if($LASTEXITCODE -ne 0) { throw 'Native SQLite evidence bundle creation failed' }
    & $diagExe verify $bundle | Out-File -LiteralPath (Join-Path $script:evidence 'verify.log') -Encoding UTF8
    Require-Check ($LASTEXITCODE -eq 0) 'sqlite_bundle' 'Redacted and SHA256-verified Core WAL evidence exported'
    Save-Preview 19631 $m.latestWorkpiece.workpieceId 'CH06' (Join-Path $script:evidence 'master-CH06-actual-ipc.png')
    Save-Preview 19632 $s.latestWorkpiece.workpieceId 'CH02' (Join-Path $script:evidence 'slave-CH02-actual-ipc.png')
    Record-Check 'frame_preview_ipc' 'PASS' 'Two actual owner-node PNG images read from Core IPC (not generated by QA)'
    $masterWindow=Find-Window 'master-ui' 35
    $slaveWindow=Find-Window 'slave-ui' 35
    Require-Check ($masterWindow -ne $slaveWindow) 'two_native_windows' ("Master hwnd={0}; Slave hwnd={1}; both visible" -f $masterWindow,$slaveWindow)
    Screenshot $masterWindow 'master-start'
    Screenshot $slaveWindow 'slave-start'
    Record-Check 'desktop_screenshots' 'PASS' 'Real visible Windows desktop captures saved separately for both GUI processes'
    foreach($page in @('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型')) {
        try {
            Click-Tab $masterWindow $page
            Screenshot $masterWindow ('tab-'+([Array]::IndexOf(@('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型'),$page)+1))
            Record-Check ('click_tab_'+$page) 'PASS' 'Real Win32 mouse click and UIA selected-state confirmed'
        } catch { Record-Check ('click_tab_'+$page) 'FAIL' $_.Exception.Message }
    }
    try {
        Click-Tab $masterWindow '11 路通道监控'
        Click-LocalChannel $masterWindow 'CH06'
        Screenshot $masterWindow 'master-selected-CH06'
        Record-Check 'click_channel_CH06' 'PASS' 'Real mouse selection of own Master camera channel'
        Click-RoiSave $masterWindow
        Wait-Until {
            $fresh=Get-CoreSnapshot 19621
            return (@($fresh.roiDrafts | Where-Object {
                $_.channelId -eq 'CH06' -and $_.revision -ge 1 -and
                [Math]::Abs($_.x-0.15) -lt 0.000001 -and
                [Math]::Abs($_.width-0.70) -lt 0.000001
            }).Count -ge 1)
        } 'real UI click -> persisted Core ROI rev1' 15
        Screenshot $masterWindow 'master-roi-saved'
        Record-Check 'roi_saved_by_real_click' 'PASS' 'Win32 mouse+ROI field edits -> Core snapshot confirms durable CH06 revision'
    } catch { Record-Check 'roi_saved_by_real_click' 'FAIL' $_.Exception.Message }
    $failed=@($script:checks | Where-Object { $_.status -eq 'FAIL' })
    $exitCode=if($failed.Count -eq 0){0}else{1}
} catch {
    $script:lastFailure=$_.Exception.ToString()
    Record-Check 'fatal' 'FAIL' $_.Exception.Message
    $exitCode=1
} finally {
    if(-not (Test-Path -LiteralPath $script:evidence)) {
        [void](New-Item -ItemType Directory -Path $script:evidence -Force)
    }
    $report=[ordered]@{
        schema='AOI.Agent05.WindowsOperatorSmoke/1'
        timestampUtc=[DateTime]::UtcNow.ToString('o')
        result=if($exitCode -eq 0){'PASS_INTERACTIVE_WINDOWS'}else{'FAIL_OR_UNVERIFIED'}
        baseBranch='migration/csharp-avalonia-20261016'
        packageZipSha256=$script:sourceHash
        evidenceRoot=$script:evidence
        simulation='SIM_FULL_LOOPBACK_ONLY'
        processIds=@{}
        desktopScreenshotFiles=@($script:desktopScreenshots)
        checks=@($script:checks.ToArray())
        lastFailure=$script:lastFailure
        keepRunning=[bool]$KeepRunning
    }
    foreach($k in $script:processes.Keys) {
        $report.processIds[$k]=$script:processes[$k].Id
    }
    ($report | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath (Join-Path $script:evidence 'operator-smoke-report.json') -Encoding UTF8

    $failedActions=@($script:checks | Where-Object { $_.status -eq 'FAIL' })
    if($failedActions.Count -gt 0) {
        $issue=@(
            '# [Agent05][Windows Native Desktop] Operator action did not pass',
            '',
            'Environment: Windows x64 interactive SIM_ONLY; source package SHA256: '+$script:sourceHash,
            'EvidenceRoot: '+$script:evidence,
            '',
            '## Repro',
            'Extract current Windows ZIP, double-click Run-Windows-Operator-Smoke.cmd.',
            'The script starts five native EXEs, checks 5x11 simulation and clicks actual UI controls.',
            '',
            '## Failed steps'
        )
        foreach($row in $failedActions) {
            $issue += ('- '+$row.step+' : '+$row.detail)
        }
        $issue += @(
            '',
            '## Attach after privacy review',
            'operator-smoke-report.json, master/slave screenshots, console stdout/stderr logs and sqlite-evidence.zip.',
            'Do not misreport Headless IPC as a real desktop click PASS.',
            'Route visual element naming problems to Agent04; Host/IPC to A0; native Windows packaging to A0/A5.'
        )
        $issue | Set-Content -LiteralPath (Join-Path $script:evidence 'operator-failure-issue.md') -Encoding UTF8
    }

    if($PauseForInspection) {
        Write-Host 'Five SIM_ONLY child processes are still running. Inspect BOTH GUI windows and screenshots.'
        Write-Host 'Press ENTER here after inspection to cleanly stop ONLY these five child processes.'
        try { [void](Read-Host 'Complete manual operator inspection') } catch {}
    }
    if(-not $KeepRunning) {
        foreach($p in $script:processes.Values) {
            try { if(-not $p.HasExited) { [void]$p.CloseMainWindow() } } catch {}
        }
        Start-Sleep -Milliseconds 750
        foreach($p in $script:processes.Values) {
            try { if(-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction Stop } } catch {}
        }
    }
    Write-Host ('A5_WINDOWS_SMOKE_REPORT='+ (Join-Path $script:evidence 'operator-smoke-report.json'))
    if($exitCode -eq 0){Write-Host 'A5_WINDOWS_INTERACTIVE_DESKTOP_PASS'}else{Write-Host 'A5_WINDOWS_INTERACTIVE_DESKTOP_FAIL'}
}
exit $exitCode
