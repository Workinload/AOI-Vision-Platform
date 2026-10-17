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
    [switch]$PauseForInspection,
    [switch]$TryCi1080p
)

$ErrorActionPreference='Stop'
$script:checks=New-Object System.Collections.ArrayList
$script:processes=@{}
$script:evidence=[IO.Path]::GetFullPath($EvidenceRoot)
$script:sourceHash=$null
$script:desktopScreenshots=New-Object System.Collections.ArrayList
$script:uiaDumped=@{}
$script:dataRoot=$null
$script:masterSnapshot=$null
$script:slaveSnapshot=$null
$script:lastFailure=$null
$script:originalDisplay=$null
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
function Start-AoiProcess([string]$Name,[string]$Exe,[string]$ArgumentLine) {
    $out=Join-Path $script:evidence ($Name+'.stdout.log')
    $err=Join-Path $script:evidence ($Name+'.stderr.log')
    $p=Start-Process -FilePath $Exe -ArgumentList $ArgumentLine -WorkingDirectory (Split-Path $Exe) -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
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
    for($attempt=1;$attempt -le 4;$attempt++) {
        try {
            return $Root.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)
        } catch {
            if($attempt -eq 4) { throw }
            Start-Sleep -Milliseconds (150*$attempt)
        }
    }
}
function Click-Element([IntPtr]$Window,[Windows.Automation.AutomationElement]$Element) {
    # Win2025 hosted desktop is only ~920x664; Avalonia's natural 1580x940
    # window puts its entire TabControl/ROI below or to the right of the monitor.
    # Pan THAT TEST WINDOW, never resize a control or forge UIA state.
    $bounds=$Element.Current.BoundingRectangle
    if($bounds.Width -lt 4 -or $bounds.Height -lt 4) {
        throw ("Element has no click geometry: "+$Element.Current.Name)
    }
    $area=[Windows.Forms.SystemInformation]::WorkingArea
    $centerX=[int]($bounds.Left+$bounds.Width/2)
    $centerY=[int]($bounds.Top+$bounds.Height/2)
    $dx=0; $dy=0
    $marginX=60; $marginTop=45; $marginBottom=55
    if($centerX -gt $area.Right-$marginX) { $dx=($area.Right-$marginX)-$centerX }
    elseif($centerX -lt $area.Left+$marginX) { $dx=($area.Left+$marginX)-$centerX }
    if($centerY -gt $area.Bottom-$marginBottom) { $dy=($area.Bottom-$marginBottom)-$centerY }
    elseif($centerY -lt $area.Top+$marginTop) { $dy=($area.Top+$marginTop)-$centerY }
    if($dx -ne 0 -or $dy -ne 0) {
        $r=New-Object AoiRect
        if(-not [AoiWin]::GetWindowRect($Window,[ref]$r)) { throw 'Native window disappeared during operator click' }
        if(-not [AoiWin]::MoveWindow($Window,($r.Left+$dx),($r.Top+$dy),($r.Right-$r.Left),($r.Bottom-$r.Top),$true)) {
            throw 'Cannot pan oversized Avalonia window into actual desktop'
        }
        Write-Host ("A5_WINDOW_PAN dx={0} dy={1} target={2}" -f $dx,$dy,$Element.Current.Name)
        Start-Sleep -Milliseconds 400
    }
    $bounds=$Element.Current.BoundingRectangle
    $centerX=[int]($bounds.Left+$bounds.Width/2)
    $centerY=[int]($bounds.Top+$bounds.Height/2)
    if($centerX -lt $area.Left+5 -or $centerX -gt $area.Right-5 -or
        $centerY -lt $area.Top+5 -or $centerY -gt $area.Bottom-5) {
        throw ("Target remains outside real Windows desktop: "+$Element.Current.Name)
    }
    [void][AoiWin]::SetForegroundWindow($Window)
    Start-Sleep -Milliseconds 150
    [AoiWin]::Click($centerX,$centerY)
    Start-Sleep -Milliseconds 340
}
function Dump-UIA([IntPtr]$Window) {
    $key=$Window.ToInt64().ToString()
    if($script:uiaDumped.ContainsKey($key)) { return }
    $script:uiaDumped[$key]=$true
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $all=$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $output=New-Object System.Collections.Generic.List[string]
    for($i=0;$i -lt $all.Count -and $i -lt 250;$i++) {
        try {
            $el=$all.Item($i)
            $a=$el.Current
            $b=$a.BoundingRectangle
            $output.Add(('{0:000} type={1} name="{2}" offscreen={3} rect={4},{5},{6},{7}' -f
                $i,$a.ControlType.ProgrammaticName,$a.Name,$a.IsOffscreen,
                [int]$b.Left,[int]$b.Top,[int]$b.Width,[int]$b.Height))
        } catch { }
    }
    $path=Join-Path $script:evidence ('uia-'+$key+'.txt')
    $output | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host ('A5_UIA_DUMP window={0} count={1} file={2}' -f $key,$all.Count,$path)
    foreach($line in @($output | Where-Object { $_ -match 'Tab|Button|ListItem|11 |45 |HOLD|ROI|产品|通道' } | Select-Object -First 28)) {
        Write-Host ('A5_UIA_ENTRY '+$line)
    }
}
function Find-UIA([Windows.Automation.AutomationElement]$Root,[Windows.Automation.ControlType]$Type,[string]$Name) {
    $filter=[Windows.Automation.AndCondition]::new(@(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$Type),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name)
    ))
    for($attempt=1;$attempt -le 4;$attempt++) {
        try {
            $item=$Root.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
            if($null -ne $item) {return $item}
        } catch {if($attempt -eq 4){throw}}
        Start-Sleep -Milliseconds (180*$attempt)
    }
    throw ("UIA element missing: "+$Name)
}
function Click-Workspace([IntPtr]$Window,[int]$Index) {
    # Merged Agent04 (Issue #87) replaced five tabs with SIX sidebar buttons.
    $names=@('生产总览','整机设备地图','11 路图集','历史 / HOLD','PLC 工程','产品方案 / ROI')
    $headers=@('生产总览','整机设备地图','11 路图集','历史与报警','PLC 工程','产品方案与 ROI')
    if($Index -lt 0 -or $Index -ge $names.Count){throw "Invalid workspace index $Index"}
    $rect=New-Object AoiRect
    if(-not [AoiWin]::GetWindowRect($Window,[ref]$rect)){throw 'Window disappeared'}
    $area=[Windows.Forms.SystemInformation]::WorkingArea
    [void][AoiWin]::MoveWindow($Window,$area.Left+6,$area.Top+6,
          $rect.Right-$rect.Left,$rect.Bottom-$rect.Top,$true)
    [void][AoiWin]::SetForegroundWindow($Window)
    Start-Sleep -Milliseconds 220
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $name=$names[$Index]
    $buttons=Elements $root ([Windows.Automation.ControlType]::Button)
    $target=$null
    foreach($button in $buttons) {
        if($button.Current.Name -like ('*'+$name) -and
           $button.Current.BoundingRectangle.Left -lt $area.Left+280) {
            $target=$button
            break
        }
    }
    if($null -eq $target){throw ("Left navigation button not exposed: "+$name)}
    $method='MOUSE'
    Click-Element $Window $target
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $tab=Find-UIA $root ([Windows.Automation.ControlType]::TabItem) $headers[$Index]
    $pattern=$tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    if(-not $pattern.Current.IsSelected) {
        $method='UIA_INVOKE'
        $invoke=$target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Start-Sleep -Milliseconds 350
    }
    if(-not $pattern.Current.IsSelected){throw ("Operator nav failed: "+$name)}
    Write-Host ("A5_WORKSPACE_SELECTED index={0} method={1} page={2}" -f $Index,$method,$headers[$Index])
    return $method
}
function Click-LocalChannel([IntPtr]$Window,[string]$Id) {
    # FindFirst narrows native UIAutomation traversal: FindAll(Text) across 11
    # tiles + PLC table occasionally returns provider COM E_FAIL on Windows.
    # A real Windows mouse event still selects the target; the subsequent
    # node-owned Core ROI revision check proves the correct channel was chosen.
    $filter=[Windows.Automation.AndCondition]::new(@(
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text),
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty,$Id)
    ))
    for($attempt=1;$attempt -le 5;$attempt++) {
        try {
            $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
            $nameElement=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
            if($null -eq $nameElement) { throw ("No actual Text peer for "+$Id) }
            $win=New-Object AoiRect
            if(-not [AoiWin]::GetWindowRect($Window,[ref]$win)) {
                throw 'Target GUI window disappeared'
            }
            $labelX=$nameElement.Current.BoundingRectangle.Left
            if($labelX -gt $win.Left+($win.Right-$win.Left)*0.65) {
                throw ("CHxx match appears in PLC table rather than left equipment map: "+$Id)
            }
            Click-Element $Window $nameElement
            return
        } catch {
            if($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds (190*$attempt)
        }
    }
}
function Click-RoiSave([IntPtr]$Window) {
    $root=[Windows.Automation.AutomationElement]::FromHandle($Window)
    $edits=Elements $root ([Windows.Automation.ControlType]::Edit)
    $visible=@()
    foreach($e in $edits) {
        if($e.Current.BoundingRectangle.Width -gt 25) { $visible+= $e }
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
        if($button.Current.Name -like '*校验并保存本机 ROI 草稿*') {
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
[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
public struct AoiDisplayMode {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra;
    public int dmFields,dmPositionX,dmPositionY,dmDisplayOrientation,dmDisplayFixedOutput;
    public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency;
    public int dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight;
}
[StructLayout(LayoutKind.Sequential)] public struct AoiRect {
    public int Left,Top,Right,Bottom;
}
public static class AoiWin {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device,int mode,ref AoiDisplayMode value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref AoiDisplayMode value,int flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out AoiRect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool repaint);
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
    if($TryCi1080p) {
        # Real Windows runner defaults to a tiny 1024x768 virtual screen; AOI's
        # operator workstation targets 1920x1080. Only attempt to change mode
        # inside GitHub CI; restore the former mode in the outer finally.
        if($env:GITHUB_ACTIONS -ne 'true') {
            throw 'TryCi1080p is restricted to GitHub hosted SIM_ONLY QA'
        }
        try {
            $original=[AoiDisplayMode]::new()
            $original.dmSize=[Runtime.InteropServices.Marshal]::SizeOf([type][AoiDisplayMode])
            if(-not [AoiWin]::EnumDisplaySettings($null,-1,[ref]$original)) {
                throw 'Windows virtual display mode enumeration unavailable'
            }
            $modes=0
            $changed=$false
            if($original.dmPelsWidth -ge 1920 -and $original.dmPelsHeight -ge 1080) {
                $changed=$true
            } else {
                for($n=0;$n -lt 512;$n++) {
                    $mode=[AoiDisplayMode]::new()
                    $mode.dmSize=[Runtime.InteropServices.Marshal]::SizeOf([type][AoiDisplayMode])
                    if(-not [AoiWin]::EnumDisplaySettings($null,$n,[ref]$mode)) { break }
                    $modes++
                    if($mode.dmPelsWidth -eq 1920 -and $mode.dmPelsHeight -eq 1080 -and
                        $mode.dmBitsPerPel -ge 24) {
                        $mode.dmFields=0x00180000 # DM_PELSWIDTH | DM_PELSHEIGHT
                        $result=[AoiWin]::ChangeDisplaySettings([ref]$mode,0)
                        if($result -eq 0) {
                            $script:originalDisplay=$original
                            Start-Sleep -Seconds 2
                            $changed=$true
                        } else {
                            Write-Host ("A5_WINDOWS_1080P_MODE_REJECTED error="+$result)
                        }
                        break
                    }
                }
            }
            $bounds=[Windows.Forms.SystemInformation]::VirtualScreen
            $screenInfo=("actual={0}x{1} supported_modes={2}" -f $bounds.Width,$bounds.Height,$modes)
            if($changed -and $bounds.Width -ge 1920 -and $bounds.Height -ge 1080) {
                Record-Check 'native_desktop_resolution' 'PASS' $screenInfo
            } else {
                Record-Check 'native_desktop_resolution' 'WARN' ($screenInfo+'; 1080p operator rendering NOT verified on this runner')
            }
        } catch {
            Record-Check 'native_desktop_resolution' 'WARN' $_.Exception.Message
        }
    }
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
    try {
        # Equipment map is independent of the separate PLC tab; verify ROI even if
        # current Avalonia TabControl has inaccessible/collapsed headers.
        Click-LocalChannel $masterWindow 'CH06'
        Start-Sleep -Milliseconds 900
        Screenshot $masterWindow 'master-selected-CH06'
        Click-RoiSave $masterWindow
        Wait-Until {
            $fresh=Get-CoreSnapshot 19621
            return (@($fresh.roiDrafts | Where-Object {
                $_.channelId -eq 'CH06' -and $_.revision -ge 1 -and
                [Math]::Abs($_.x-0.15) -lt 0.000001 -and
                [Math]::Abs($_.width-0.70) -lt 0.000001
            }).Count -ge 1)
        } 'real UI click -> persisted Core ROI rev1' 15
        Record-Check 'click_channel_CH06' 'PASS' 'Actual GUI selection proven by persisted Master-owned CH06 ROI'
        Screenshot $masterWindow 'master-roi-saved'
        Record-Check 'roi_saved_by_real_click' 'PASS' 'Win32 mouse+ROI field edits -> Core snapshot confirms durable CH06 revision'
    } catch { Record-Check 'roi_saved_by_real_click' 'FAIL' $_.Exception.Message }
    try {
        Click-LocalChannel $slaveWindow 'CH02'
        Start-Sleep -Milliseconds 900
        Screenshot $slaveWindow 'slave-selected-CH02'
        Click-RoiSave $slaveWindow
        Wait-Until {
            $fresh=Get-CoreSnapshot 19622
            return (@($fresh.roiDrafts | Where-Object {
                $_.channelId -eq 'CH02' -and $_.revision -ge 1 -and
                [Math]::Abs($_.y-0.15) -lt 0.000001 -and
                [Math]::Abs($_.height-0.70) -lt 0.000001
            }).Count -ge 1)
        } 'Slave ROI saved from real GUI button into its own Core' 15
        Record-Check 'click_slave_channel_CH02' 'PASS' 'Actual GUI selection proven by persisted Slave-owned CH02 ROI'
        Screenshot $slaveWindow 'slave-roi-saved'
        Record-Check 'slave_roi_saved_by_real_click' 'PASS' 'Slave GUI editing -> Slave Core persisted CH02 revision'
    } catch { Record-Check 'slave_roi_saved_by_real_click' 'FAIL' $_.Exception.Message }
    foreach($page in @('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型')) {
        try {
            $method=Click-Tab $masterWindow $page
            Screenshot $masterWindow ('tab-'+([Array]::IndexOf(@('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型'),$page)+1))
            Record-Check ('click_tab_'+$page) 'PASS' ('Real Avalonia tab selected via '+$method+'; UIAutomation selected-state confirmed')
        } catch { Record-Check ('click_tab_'+$page) 'FAIL' $_.Exception.Message }
    }
    foreach($page in @('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型')) {
        try {
            $method=Click-Tab $slaveWindow $page
            Screenshot $slaveWindow ('slave-tab-'+([Array]::IndexOf(@('11 路通道监控','45 条 PLC 工程映射','14 项现场验证门禁','HOLD / 异常','产品方案 / 换型'),$page)+1))
            Record-Check ('slave_click_tab_'+$page) 'PASS' ('Slave Avalonia tab selected via '+$method+'; UIAutomation selected-state confirmed')
        } catch { Record-Check ('slave_click_tab_'+$page) 'FAIL' $_.Exception.Message }
    }
    $failed=@($script:checks | Where-Object { $_.status -eq 'FAIL' })
    # The hosted runner may have an interactive HWND yet no way to render an
    # operator-size desktop. UIA TabItem.Selected + screenshots at 1024x768
    # are NOT proof that every 1080p page is visibly operable.
    $unverified1080=$TryCi1080p -and @($script:checks | Where-Object {
        $_.step -eq 'native_desktop_resolution' -and $_.status -ne 'PASS'
    }).Count -gt 0
    if($failed.Count -gt 0) { $exitCode=1 }
    elseif($unverified1080) {
        Record-Check 'operator_1080p_visible_pages' 'NOT_VERIFIED' 'Windows runner cannot display the required 1920x1080 native operator page; GUI mouse+ROI tests passed but visual acceptance remains pending'
        $exitCode=3
    } else { $exitCode=0 }
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
        result=if($exitCode -eq 0){'PASS_INTERACTIVE_WINDOWS'}elseif($exitCode -eq 3){'PARTIAL_OPERATOR_GUI_1080P_NOT_VERIFIED'}else{'FAIL_OR_UNVERIFIED'}
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

    if($null -ne $script:originalDisplay) {
        try {
            $original=$script:originalDisplay
            $changed=[AoiWin]::ChangeDisplaySettings([ref]$original,0)
            Write-Host ('A5_WINDOWS_DISPLAY_RESTORE status='+$changed)
        } catch {
            Write-Host ('A5_WINDOWS_DISPLAY_RESTORE_FAILED '+$_.Exception.Message)
        }
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
    if($exitCode -eq 0) { Write-Host 'A5_WINDOWS_INTERACTIVE_DESKTOP_PASS' }
    elseif($exitCode -eq 3) { Write-Host 'A5_WINDOWS_DESKTOP_PARTIAL_REAL_GUI_AND_ROI_PASS_1080P_NOT_VERIFIED' }
    else { Write-Host 'A5_WINDOWS_INTERACTIVE_DESKTOP_FAIL' }
}
exit $exitCode
