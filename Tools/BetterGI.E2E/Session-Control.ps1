param(
    [Parameter(Mandatory=$true)][ValidateSet('Close','Launch','CloseGame')][string]$Action,
    [Parameter(Mandatory=$true)][int]$Session,
    [Parameter(Mandatory=$true)][string]$OutDir,
    [string]$Root='C:\Program Files\BetterGI',
    [int]$TargetPid=0,
    [long]$TargetStartTicks=0,
    [string]$Groups='')
$ErrorActionPreference='Stop'
$statusFile=Join-Path $OutDir ("session-$Session-$Action.json")
function Start-VerifiedBgi([hashtable]$LaunchOptions) {
    $started=Start-Process @LaunchOptions
    if($started.WaitForExit(5000)){throw ('BGI exited during startup; exitCode='+$started.ExitCode)}
    return $started
}

try {
    if ([Diagnostics.Process]::GetCurrentProcess().SessionId -ne $Session) { throw 'Wrong interactive session' }
    if ($Action -eq 'CloseGame') {
        $target=Get-Process -Id $TargetPid -ErrorAction Stop
        if ($target.ProcessName -ne 'YuanShen' -or $target.SessionId -ne $Session -or
            [math]::Abs($target.StartTime.ToUniversalTime().Ticks-$TargetStartTicks) -gt 10000000) { throw 'Game process identity changed' }
        if (-not $target.CloseMainWindow()) { throw 'Game refused normal close; no force fallback' }
        if (-not $target.WaitForExit(30000)) { throw 'Game did not close in 30 seconds; no force fallback' }
        $result=@{passed=$true;action=$Action;session=$Session;pid=$TargetPid;normalExit=$true;at=(Get-Date).ToString('o')}
    } elseif ($Action -eq 'Close') {
        $target=Get-Process -Id $TargetPid -ErrorAction Stop
        if ($target.ProcessName -ne 'BetterGI' -or $target.SessionId -ne $Session -or
            [math]::Abs($target.StartTime.ToUniversalTime().Ticks-$TargetStartTicks) -gt 10000000) { throw 'Process identity changed' }
        # Normal process shutdown only. No UI Automation, mouse/keyboard input or kill fallback.
        $closeAccepted=$target.CloseMainWindow()
        if(-not $closeAccepted){
            # A tray-hidden main window is excluded by Process.MainWindowHandle.
            # ASCII-only source also works in Windows PowerShell 5's legacy encoding.
            Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class BgiHiddenNormalClose {
 public delegate bool EnumProc(IntPtr window,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
 [DllImport("user32.dll",SetLastError=true)] static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
 public static bool Close(uint process) {
  IntPtr selected=IntPtr.Zero;int count=0;
  EnumWindows((window,data)=>{uint owner;GetWindowThreadProcessId(window,out owner);if(owner!=process)return true;
   var title=new StringBuilder(256);var cls=new StringBuilder(256);GetWindowText(window,title,256);GetClassName(window,cls,256);
   if(GetWindow(window,4)==IntPtr.Zero&&cls.ToString().StartsWith("HwndWrapper[BetterGI;")&&
      (title.ToString().Contains("BetterGI")||title.ToString()=="\u66f4\u597d\u7684\u539f\u795e")){selected=window;count++;}return true;},IntPtr.Zero);
  return count==1&&PostMessage(selected,0x0010,IntPtr.Zero,IntPtr.Zero);
 }
}
'@
            $closeAccepted=[BgiHiddenNormalClose]::Close([uint32]$TargetPid)
        }
        if (-not $closeAccepted) { throw 'Normal close was refused; process left alive, no force kill' }
        if (-not $target.WaitForExit(30000)) { throw 'Process did not close in 30 seconds; no force kill' }
        $result=@{passed=$true;action=$Action;session=$Session;pid=$TargetPid;normalExit=$true;at=(Get-Date).ToString('o')}
    } else {
        if (@(Get-Process -Name BetterGI -ErrorAction SilentlyContinue | Where-Object SessionId -eq $Session).Count) { throw 'Session already has BGI' }
        $launch=@{FilePath=(Join-Path $Root 'BetterGI.exe');WorkingDirectory=$Root;WindowStyle='Hidden';PassThru=$true}
        if ($Groups) {
            $groupNames=@($Groups.Split('|') | Where-Object { $_ })
            foreach($name in $groupNames){if($name -notmatch '^_E2E_GlobalPickup_[A-Za-z0-9_]+$'){throw 'Only generated E2E groups may be launched'}}
            $launch.ArgumentList='--startGroups '+(($groupNames | ForEach-Object {'"'+$_+'"'}) -join ' ')
        }
        $started=Start-VerifiedBgi $launch
        $result=@{passed=$true;action=$Action;session=$Session;pid=$started.Id;groups=$Groups;at=(Get-Date).ToString('o')}
    }
    $temporary=$statusFile+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statusFile -Force
    exit 0
} catch {
    $temporary=$statusFile+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    @{passed=$false;action=$Action;session=$Session;error=$_.Exception.Message;at=(Get-Date).ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statusFile -Force
    exit 1
}
