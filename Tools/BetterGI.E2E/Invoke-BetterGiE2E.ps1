param(
    [ValidateSet('Plan','Contracts','Deploy','CloseGame')][string]$Mode='Plan',
    [switch]$Apply,
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [string]$Root='C:\Program Files\BetterGI',
    [string]$OutDir='C:\Program Files\BetterGI\artifacts\global-pick-deploy-20261109\e2e',
    [string]$Session2User=([Environment]::MachineName+'\BetterGI'))
$ErrorActionPreference='Stop'
$python=Join-Path $PSScriptRoot 'bgi_e2e.py'
$control=Join-Path $PSScriptRoot 'Session-Control.ps1'
$packageDir=[IO.Path]::GetFullPath($PackageDir)
$manifestFile=Join-Path $packageDir 'package-manifest.json'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$lifecycleStarted=$false
$installationVerified=$false
$rollbackFailed=$false
$testsCompleted=$false
$sdkAttempted=$false
$sdkCleanupCompleted=$false
$gameCloseAttempted=$false
$helperExe=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Run-Python([string]$Command) {
    & python -I -X utf8 $python $Command --out $OutDir --package $packageDir --root $Root
    if($LASTEXITCODE -ne 0){throw "CLI E2E command failed: $Command"}
}
function Is-Admin {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Safe-Target([string]$Relative) {
    $resolved=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Target escapes workspace'}
    return $resolved
}
function Atomic-Copy([string]$Source,[string]$Target,[bool]$Existed) {
    $temporary=$Target+'.e2e-'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        if($Existed){
            if(-not ('BgiAtomicFile' -as [type])){
                Add-Type -TypeDefinition 'public static class BgiAtomicFile { public static void Replace(string source,string target) { System.IO.File.Replace(source,target,null); } }'
            }
            [BgiAtomicFile]::Replace($temporary,$Target)
        }
        else{[IO.File]::Move($temporary,$Target)}
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
}
function Run-Session2([string]$Operation,[hashtable]$Options) {
    $nonce=[Guid]::NewGuid().ToString('N').Substring(0,12)
    $taskName='BGI-CLI-E2E-'+$Operation+'-'+$nonce
    $helperOut=Join-Path $OutDir ('helper-'+$nonce)
    New-Item -ItemType Directory -Path $helperOut | Out-Null
    $args='-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$control+'" -Action '+$Operation+' -Session 2 -OutDir "'+$helperOut+'" -Root "'+$Root+'"'
    foreach($key in $Options.Keys){$args+=' -'+$key+' "'+[string]$Options[$key]+'"'}
    $action=New-ScheduledTaskAction -Execute $helperExe -Argument $args -WorkingDirectory $Root
    $principal=New-ScheduledTaskPrincipal -UserId $Session2User -LogonType Interactive -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    try {
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
        Start-ScheduledTask -TaskName $taskName
        $limit=(Get-Date).AddSeconds(50)
        $resultFile=Join-Path $helperOut ('session-2-'+$Operation+'.json')
        while(-not(Test-Path -LiteralPath $resultFile)){
            if((Get-Date)-gt $limit){throw 'Session helper deadline expired; no force fallback'}
            Start-Sleep -Milliseconds 250
        }
        $status=Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if(-not $status.passed){throw $status.error}
        return $status
    } finally {
        if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $taskName -Confirm:$false}
    }
}
function Close-GameAfterTests {
    $script:gameCloseAttempted=$true
    $games=@(Get-Process -Name YuanShen -ErrorAction SilentlyContinue | Where-Object SessionId -eq 2)
    if($games.Count -gt 1){throw 'Multiple Session 2 games found; refuse ambiguous cleanup'}
    $closed=@()
    foreach($game in $games){
        $status=Run-Session2 'CloseGame' @{TargetPid=$game.Id;TargetStartTicks=$game.StartTime.ToUniversalTime().Ticks}
        if(-not $status.passed -or -not $status.normalExit -or $status.pid -ne $game.Id){throw 'Game-close helper did not confirm the expected normal exit'}
        $closed+=$status
    }
    $remaining=@(Get-Process -Name YuanShen -ErrorAction SilentlyContinue | Where-Object SessionId -eq 2)
    if($remaining.Count){throw 'A Session 2 game is still running after cleanup; no force fallback'}
    @{passed=$true;session=2;closed=$closed;alreadyClosed=($games.Count -eq 0);remaining=0;normalExitOnly=$true;desktopAutomation=$false;at=(Get-Date).ToString('o')} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'game-close.json') -Encoding UTF8
}
function Start-SdkGroups([string[]]$Groups) {
    # A launch may succeed before its helper receipt fails. Mark ownership before dispatch.
    $script:sdkAttempted=$true
    return Run-Session2 'Launch' @{Groups=($Groups -join '|')}
}
function Stop-PendingSdkStart {
    $state=Get-Content -LiteralPath (Join-Path $OutDir 'live-run.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $finishedFile=Join-Path $OutDir 'sdk-attempt-finished.json'
    if(Test-Path -LiteralPath $finishedFile){
        $finished=Get-Content -LiteralPath $finishedFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if($finished.runId -eq $state.runId -and $finished.nativeGroupsFinished){return}
    }
    # Stop only this run's BGI before closing its game, so a delayed cold start cannot create a game later.
    $owned=@(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'BetterGI.exe' -and $_.SessionId -eq 2 -and $_.CommandLine -and $_.CommandLine.Contains($state.runId)
    })
    if($owned.Count -gt 1){throw 'Multiple SDK startup owners; preserve processes for inspection'}
    if($owned.Count -eq 1){
        $target=Get-Process -Id $owned[0].ProcessId -ErrorAction Stop
        $closed=Run-Session2 'Close' @{TargetPid=$target.Id;TargetStartTicks=$target.StartTime.ToUniversalTime().Ticks}
        if(-not $closed.passed -or -not $closed.normalExit){throw 'Pending SDK startup did not exit normally'}
        $closed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutDir 'sdk-startup-close.json') -Encoding UTF8
    }
}
function Finish-SdkAttempt {
    if(-not $script:sdkAttempted -or $script:sdkCleanupCompleted){return}
    if(-not $script:gameCloseAttempted){Stop-PendingSdkStart;Close-GameAfterTests}
    $closeFile=Join-Path $OutDir 'game-close.json'
    if(-not(Test-Path -LiteralPath $closeFile) -or -not(Get-Content -LiteralPath $closeFile -Raw -Encoding UTF8 | ConvertFrom-Json).passed){throw 'SDK cleanup cannot confirm normal game shutdown'}
    Run-Python 'archive-live'
    $script:sdkCleanupCompleted=$true
}
try {
    $artifactRoot=[IO.Path]::GetFullPath((Join-Path $Root 'artifacts')).TrimEnd('\')+'\'
    if(-not $packageDir.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Package escapes workspace artifacts'}
    if(-not(Test-Path -LiteralPath (Join-Path $Root 'BetterGI.exe'))){throw 'Root is not an installed BetterGI workspace'}
    if($Mode -eq 'Contracts'){Run-Python 'contracts';exit 0}
    Run-Python 'plan'
    if($Mode -eq 'Plan'){
        @{mode='Plan';administrator=(Is-Admin);desktopAutomation=$false;installed=$false;nextCommand='Run this same script as administrator with -Mode Deploy -Apply'} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'executor.json') -Encoding UTF8
        exit 0
    }
    if(-not $Apply){throw 'Deploy requires explicit -Apply; no process or installed file changed'}
    if(-not(Is-Admin)){throw 'Administrator CLI required for normal cross-session restart. No process or installed file changed. Run Invoke-BetterGiE2E.ps1 -Mode Deploy -Apply in an elevated terminal.'}
    if([Diagnostics.Process]::GetCurrentProcess().SessionId -ne 1){throw 'Deployment controller must run in Session 1'}
    if($Mode -eq 'CloseGame'){
        $finished=Get-Content -LiteralPath (Join-Path $OutDir 'sdk-attempt-finished.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if(-not $finished.finished){throw 'SDK attempt has not finished; cleanup is not due yet'}
        Close-GameAfterTests
        exit 0
    }
    $manifest=Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    # Reuse completed checks only from a plan for this exact clean/pinned source.
    $reused=$false
    foreach($directory in @(Get-ChildItem -LiteralPath $packageDir -Directory | Sort-Object LastWriteTime -Descending)){
        $priorPlan=Join-Path $directory.FullName 'plan.json'
        $priorChecks=Join-Path $directory.FullName 'contracts.json'
        if((Test-Path -LiteralPath $priorPlan) -and (Test-Path -LiteralPath $priorChecks)){
            $identity=Get-Content -LiteralPath $priorPlan -Raw -Encoding UTF8 | ConvertFrom-Json
            $checks=Get-Content -LiteralPath $priorChecks -Raw -Encoding UTF8 | ConvertFrom-Json
            if($identity.passed -and $identity.sourceTree -eq $manifest.sourceTree -and $checks.sourceTree -eq $manifest.sourceTree -and $checks.sourceVerifiedBeforeAndAfter -and $checks.passed -and $checks.checks -eq $manifest.contractChecks){
                if($directory.FullName -ne [IO.Path]::GetFullPath($OutDir)){Copy-Item -LiteralPath $priorChecks -Destination (Join-Path $OutDir 'contracts.json')}
                @{source=$priorChecks;sha256=(Get-FileHash -LiteralPath $priorChecks -Algorithm SHA256).Hash;sourceCommit=$identity.sourceCommit;sourceTree=$identity.sourceTree;checks=$manifest.contractChecks} |
                    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'contracts-provenance.json') -Encoding UTF8
                $reused=$true;break
            }
        }
    }
    if(-not $reused){Run-Python 'contracts'}
    foreach($protected in $manifest.protectedFiles){
        if((Get-FileHash -LiteralPath (Safe-Target $protected.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $protected.sha256){throw ('Protected file changed after package preparation: '+$protected.file)}
    }
    foreach($item in $manifest.files){
        $live=Safe-Target $item.file
        if($item.existed){
            if((Get-FileHash -LiteralPath $live -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.beforeSha256){throw ('Installed target changed after backup: '+$item.file)}
        }elseif(Test-Path -LiteralPath $live){throw ('New target unexpectedly exists: '+$item.file)}
    }
    # New OutDir per execution prevents stale helper or test results satisfying this run.
    if(Test-Path -LiteralPath (Join-Path $OutDir 'processes-before.json')){throw 'OutDir has already been used for deployment; choose a fresh run directory'}
    $before=@(Get-Process -Name BetterGI -ErrorAction SilentlyContinue)
    if($before.Count -ne 2 -or @($before | Where-Object SessionId -eq 1).Count -ne 1 -or @($before | Where-Object SessionId -eq 2).Count -ne 1){throw 'Expected exactly one BGI in each intended session and no extra instance'}
    $before | Select-Object Id,SessionId,@{N='StartUtc';E={$_.StartTime.ToUniversalTime().ToString('o')}} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'processes-before.json') -Encoding UTF8
    $s2=$before | Where-Object SessionId -eq 2
    $lifecycleStarted=$true
    $s2close=Run-Session2 'Close' @{TargetPid=$s2.Id;TargetStartTicks=$s2.StartTime.ToUniversalTime().Ticks}
    $s1=$before | Where-Object SessionId -eq 1
    & $helperExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $control -Action Close -Session 1 -OutDir $OutDir -Root $Root -TargetPid $s1.Id -TargetStartTicks $s1.StartTime.ToUniversalTime().Ticks
    if($LASTEXITCODE -ne 0){throw 'Session 1 normal close failed'}
    # User authorized the normal S2 restart chain. Do not assume ABGI closes the game.
    $oldGames=@(Get-Process -Name YuanShen -ErrorAction SilentlyContinue | Where-Object SessionId -eq 2)
    if($oldGames.Count -gt 1){throw 'Multiple games before install; refuse ambiguous restart'}
    foreach($oldGame in $oldGames){
        $gameStatus=Run-Session2 'CloseGame' @{TargetPid=$oldGame.Id;TargetStartTicks=$oldGame.StartTime.ToUniversalTime().Ticks}
        $gameStatus | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'game-close-before-install.json') -Encoding UTF8
    }
    if(@(Get-Process -Name YuanShen -ErrorAction SilentlyContinue | Where-Object SessionId -eq 2).Count){throw 'Game did not exit normally before install'}
    if(@(Get-Process -Name BetterGI -ErrorAction SilentlyContinue).Count){throw 'BGI reappeared before install'}
    # Normal shutdown may save configuration. Record it; never replace User/config.json.
    $protectedAfterClose=@()
    foreach($protected in $manifest.protectedFiles){
        $actual=(Get-FileHash -LiteralPath (Safe-Target $protected.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if($protected.file -ne 'User/config.json' -and $actual -ne $protected.sha256){throw ('Protected file changed during shutdown: '+$protected.file)}
        $protectedAfterClose+=@{file=$protected.file;sha256=$actual;changed=($actual -ne $protected.sha256)}
    }
    $protectedAfterClose | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'protected-after-close.json') -Encoding UTF8
    foreach($item in $manifest.files){
        $source=Join-Path (Join-Path $packageDir 'package') $item.file
        if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.candidateSha256){throw 'Staged file changed'}
    }
    $installed=@()
    try {
        foreach($item in $manifest.files){Atomic-Copy (Join-Path (Join-Path $packageDir 'package') $item.file) (Safe-Target $item.file) $item.existed;$installed+=$item.file}
        Run-Python 'verify-install'
        $installationVerified=$true
    }catch{
        $copyError=$_
        foreach($relative in $installed){
            $item=$manifest.files | Where-Object file -eq $relative
            $target=Safe-Target $relative
            try {
                if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.candidateSha256){throw 'Concurrent edit; preserve target rather than overwrite during rollback'}
                if($item.existed){Atomic-Copy (Join-Path (Join-Path $packageDir 'backup-before-install') $relative) $target $true}
                else{Remove-Item -LiteralPath $target}
            }catch{$rollbackFailed=$true}
        }
        if($rollbackFailed){throw 'Install failed and rollback was incomplete; do not launch a mixed installation'}
        throw $copyError
    }
    Run-Python 'prepare-live'
    $live=Get-Content -LiteralPath (Join-Path $OutDir 'live-run.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $s2launch=Start-SdkGroups $live.groups
    & $helperExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $control -Action Launch -Session 1 -OutDir $OutDir -Root $Root
    if($LASTEXITCODE -ne 0){throw 'Session 1 launch failed'}
    @{installed=$true;liveStatus='running';sourceCommit=$manifest.sourceCommit;exeSha256=($manifest.files | Where-Object file -eq 'BetterGI.exe').candidateSha256;desktopAutomation=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'deployment.json') -Encoding UTF8
    Run-Python 'live'
    $testsCompleted=$true
    Run-Python 'verify-install'
    $protectedFinal=@()
    foreach($protected in $manifest.protectedFiles){
        $actual=(Get-FileHash -LiteralPath (Safe-Target $protected.file) -Algorithm SHA256).Hash.ToLowerInvariant()
        $protectedFinal+=@{file=$protected.file;sha256=$actual;changed=($actual -ne $protected.sha256)}
        if($protected.file -ne 'User/config.json' -and $actual -ne $protected.sha256){throw ('Protected file changed during SDK E2E: '+$protected.file)}
    }
    $protectedFinal | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'protected-final.json') -Encoding UTF8
    Finish-SdkAttempt
    Get-Process -Name BetterGI,YuanShen,auto-bgi -ErrorAction SilentlyContinue | Select-Object Name,Id,SessionId,StartTime |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'processes-after.json') -Encoding UTF8
    @{passed=$true;installed=$true;gameClosed=$true;desktopAutomation=$false;worldPickupReceipt='not exercised';at=(Get-Date).ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'complete.json') -Encoding UTF8
}catch{
    $originalError=$_.Exception.Message
    $recovery=@()
    $gameCloseError=$null
    if($sdkAttempted){
        try{Finish-SdkAttempt}catch{$gameCloseError=$_.Exception.Message}
    }
    # Reopen any normally closed instance after a failed stage. Never launch mixed files.
    if($lifecycleStarted -and -not $rollbackFailed){
        foreach($session in @(2,1)){
            if(-not @(Get-Process -Name BetterGI -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session).Count){
                try {
                    if($session -eq 2){$null=Run-Session2 'Launch' @{}}
                    else{& $helperExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File $control -Action Launch -Session 1 -OutDir $OutDir -Root $Root;if($LASTEXITCODE -ne 0){throw 'Session 1 recovery launch failed'}}
                    $recovery+=@{session=$session;reopened=$true}
                }catch{$recovery+=@{session=$session;reopened=$false;error=$_.Exception.Message}}
            }
        }
    }
    @{passed=$false;error=$originalError;installationVerified=$installationVerified;rollbackFailed=$rollbackFailed;recovery=$recovery;gameCloseAttempted=$gameCloseAttempted;gameCloseError=$gameCloseError;at=(Get-Date).ToString('o');desktopAutomation=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'controller-error.json') -Encoding UTF8
    Write-Error $originalError
    exit 1
}
