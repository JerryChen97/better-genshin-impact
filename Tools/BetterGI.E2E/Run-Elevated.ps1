param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [ValidateSet('Deploy','CloseGame')][string]$Mode='Deploy',
    [string]$OutDir='C:\Program Files\BetterGI\artifacts\global-pick-deploy-20261109\e2e-live')
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevated runner is not an administrator'}
@{administrator=$true;session=[Diagnostics.Process]::GetCurrentProcess().SessionId;pid=$PID;at=(Get-Date).ToString('o')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir 'elevated-runner.json') -Encoding UTF8
Start-Transcript -LiteralPath (Join-Path $OutDir 'elevated-console.log') -Force | Out-Null
try {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File (Join-Path $PSScriptRoot 'Invoke-BetterGiE2E.ps1') -Mode $Mode -Apply -OutDir $OutDir -PackageDir $PackageDir
    $result=$LASTEXITCODE
    @{exitCode=$result;at=(Get-Date).ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutDir 'runner-exit.json') -Encoding UTF8
} finally { Stop-Transcript | Out-Null }
exit $result
