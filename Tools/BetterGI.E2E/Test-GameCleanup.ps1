param([string]$ReportDir=(Join-Path $env:TEMP 'BetterGI-E2E-reports'))
New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
# Simulated process/task adapters; never sends a close message to any real process.
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-BetterGiE2E.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Controller syntax error'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Close-GameAfterTests'},$true)
. ([ScriptBlock]::Create($definition.Extent.Text))
$finish=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Finish-SdkAttempt'},$true)
. ([ScriptBlock]::Create($finish.Extent.Text))
foreach($name in @('Start-SdkGroups','Stop-PendingSdkStart')){
    $helper=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    . ([ScriptBlock]::Create($helper.Extent.Text))
}
$OutDir=Join-Path ([IO.Path]::GetTempPath()) ('bgi-e2e-cleanup-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutDir | Out-Null
$script:calls=0;$script:behavior='close';$checks=0
function Assert([bool]$Condition,[string]$Description){if(-not $Condition){throw $Description};$script:checks++}
function Get-Process {param($Name,$ErrorAction,$Id) if($Id -eq 9001){return [PSCustomObject]@{Id=9001;StartTime=$script:started;SessionId=2}};return $script:games}
function Get-CimInstance {param($ClassName) return $script:bgis}
function Run-Session2 {param($Operation,$Options)
    if($Operation -eq 'Launch'){$script:games=@($target);$script:bgis=@([PSCustomObject]@{Name='BetterGI.exe';SessionId=2;ProcessId=9001;CommandLine='--startGroups _E2E_GlobalPickup_fixture_before'});throw 'Launch receipt failed after start'}
    if($Operation -eq 'Close'){$script:stoppedBgi++;$script:bgis=@();return @{passed=$true;normalExit=$true;pid=9001}}
    $script:calls++
    Assert ($Operation -eq 'CloseGame' -and $Options.TargetPid -eq 7001) 'Only the exact game may be closed'
    Assert ($Options.TargetStartTicks -eq $script:started.ToUniversalTime().Ticks) 'Start timestamp must be bound'
    if($script:behavior -eq 'reject'){return @{passed=$false;normalExit=$false;pid=7001}}
    $script:games=@($script:games | Where-Object SessionId -ne 2)
    if($script:behavior -eq 'replace'){$script:games+=@{Id=7003;SessionId=2;StartTime=$script:started}}
    return @{passed=$true;normalExit=$true;pid=7001}
}
function Expect-Failure([string]$Text){
    try{Close-GameAfterTests}catch{Assert ($_.Exception.Message.Contains($Text)) ('Expected '+$Text);return}
    throw 'Unexpected cleanup success'
}
$script:started=[DateTime]::UtcNow
$target=[PSCustomObject]@{Id=7001;SessionId=2;StartTime=$script:started}
try {
    $script:games=@();Close-GameAfterTests
    Assert ($script:calls -eq 0) 'Already-closed game must not dispatch a task'
    Assert ((Get-Content -LiteralPath (Join-Path $OutDir 'game-close.json') -Raw | ConvertFrom-Json).alreadyClosed) 'Already closed must be explicit'
    $script:games=@($target,[PSCustomObject]@{Id=8001;SessionId=1;StartTime=$script:started});Close-GameAfterTests
    Assert ($script:calls -eq 1 -and $script:games[0].SessionId -eq 1) 'Other session must remain untouched'
    $script:games=@($target,$target);Expect-Failure 'Multiple Session 2 games'
    Assert ($script:calls -eq 1) 'Ambiguous games must not dispatch tasks'
    $script:games=@($target);$script:behavior='reject';Expect-Failure 'did not confirm'
    $script:games=@($target);$script:behavior='replace';Expect-Failure 'still running'
    @{runId='fixture';groups=@('_E2E_GlobalPickup_fixture_before','_E2E_GlobalPickup_fixture_after')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'live-run.json') -Encoding UTF8
    @{runId='fixture';finished=$true;nativeGroupsFinished=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'sdk-attempt-finished.json') -Encoding UTF8
    $script:archiveCalls=0
    function Run-Python {param($Command) Assert ($Command -eq 'archive-live') 'Only owned test fixtures may be archived';if($script:archiveReject){throw 'Native group is still active'};$script:archiveCalls++}
    $script:sdkAttempted=$false;$script:sdkCleanupCompleted=$false;$script:gameCloseAttempted=$false
    $beforeCalls=$script:calls;Finish-SdkAttempt
    Assert ($script:calls -eq $beforeCalls -and $script:archiveCalls -eq 0) 'No cleanup before SDK attempt'
    foreach($success in @($true,$false)){
        Remove-Item -LiteralPath (Join-Path $OutDir 'game-close.json') -ErrorAction SilentlyContinue
        $script:sdkAttempted=$true;$script:sdkCleanupCompleted=$false;$script:gameCloseAttempted=$false;$script:testsCompleted=$success
        $script:behavior='close';$script:games=@($target);$prior=$script:archiveCalls
        Finish-SdkAttempt
        Assert ($script:games.Count -eq 0 -and $script:sdkCleanupCompleted -and $script:archiveCalls -eq $prior+1) 'SDK success or failure both close and archive'
    }
    Remove-Item -LiteralPath (Join-Path $OutDir 'game-close.json') -ErrorAction SilentlyContinue
    $script:sdkAttempted=$true;$script:sdkCleanupCompleted=$false;$script:gameCloseAttempted=$false;$script:games=@($target);$script:behavior='reject';$prior=$script:archiveCalls
    try{Finish-SdkAttempt;throw 'Unexpected cleanup success'}catch{Assert ($_.Exception.Message -ne 'Unexpected cleanup success') 'Refused normal close must fail cleanup'}
    Assert ($script:archiveCalls -eq $prior -and -not $script:sdkCleanupCompleted) 'Failed close never archives or reports success'
    Remove-Item -LiteralPath (Join-Path $OutDir 'game-close.json') -ErrorAction SilentlyContinue
    $script:sdkAttempted=$true;$script:sdkCleanupCompleted=$false;$script:gameCloseAttempted=$false;$script:games=@($target);$script:behavior='close';$script:archiveReject=$true
    try{Finish-SdkAttempt;throw 'Unexpected active-group archive'}catch{Assert ($_.Exception.Message -eq 'Native group is still active') 'Unfinished group must be reported'}
    Assert ($script:games.Count -eq 0 -and -not $script:sdkCleanupCompleted) 'Game still closes when fixtures cannot be archived; never report success'
    Remove-Item -LiteralPath (Join-Path $OutDir 'game-close.json') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $OutDir 'sdk-attempt-finished.json') -ErrorAction SilentlyContinue
    $script:sdkAttempted=$false;$script:sdkCleanupCompleted=$false;$script:gameCloseAttempted=$false;$script:games=@();$script:behavior='close';$script:archiveReject=$true;$script:stoppedBgi=0
    try{Start-SdkGroups @('_E2E_GlobalPickup_fixture_before');throw 'Unexpected launch success'}catch{
        Assert ($_.Exception.Message -eq 'Launch receipt failed after start' -and $script:sdkAttempted) 'Launch must be marked before helper receipt fails'
        try{Finish-SdkAttempt}catch{Assert ($_.Exception.Message -eq 'Native group is still active') 'Unfinished launch fixtures stay for inspection'}
    }
    Assert ($script:stoppedBgi -eq 1 -and $script:games.Count -eq 0) 'Uncertain launch quiesces only its BGI then normally closes its game'
    Assert (-not $script:sdkCleanupCompleted) 'Unconfirmed group completion never reports cleanup success'
    $report=@{passed=$true;checks=$checks;scenarios=11;scope='Cleanup controller with simulated process/task adapters; no real process closed';desktopAutomation=$false;gameInput=$false}
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'cleanup-selftest.json') -Encoding UTF8
    $report | ConvertTo-Json
} finally {
    Get-ChildItem -LiteralPath $OutDir -File | Remove-Item
    Remove-Item -LiteralPath $OutDir
}
