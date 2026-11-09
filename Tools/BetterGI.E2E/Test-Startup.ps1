param([string]$ReportDir=(Join-Path $env:TEMP 'BetterGI-E2E-reports'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Session-Control.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Session helper parse failed'}
$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Start-VerifiedBgi'},$true)
. ([ScriptBlock]::Create($node.Extent.Text))
$script:exited=$false;$script:observations=0
$script:fake=[PSCustomObject]@{Id=7001;ExitCode=123}
$script:fake | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($milliseconds) if($milliseconds -ne 5000){throw 'Startup observation budget changed'};$script:observations++;return $script:exited}
function Start-Process {param($FilePath,$WorkingDirectory,$WindowStyle,$PassThru) if($WindowStyle -ne 'Hidden'){throw 'Launch must stay hidden'};return $script:fake}
$launch=@{FilePath='fixture';WorkingDirectory='fixture';WindowStyle='Hidden';PassThru=$true}
if((Start-VerifiedBgi $launch).Id -ne 7001){throw 'Living process was not returned'}
$script:exited=$true;$rejected=$false
try{Start-VerifiedBgi $launch}catch{$rejected=$_.Exception.Message -match 'BGI exited during startup; exitCode=123'}
if(-not $rejected -or $script:observations -ne 2){throw 'Early crash incorrectly reported as successful startup'}
@{passed=$true;scenarios=2;scope='Real startup-verification helper with process adapter; no application or game launched';desktopAutomation=$false;gameInput=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'startup-selftest.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $ReportDir 'startup-selftest.json') -Encoding UTF8
