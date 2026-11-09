param([string]$ReportDir=(Join-Path $env:TEMP 'BetterGI-E2E-reports'))
New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
# Actual NTFS operations in a new temporary directory; no installation files touched.
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-BetterGiE2E.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Controller syntax error'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Atomic-Copy'},$true)
. ([ScriptBlock]::Create($definition.Extent.Text))
$folder=Join-Path ([IO.Path]::GetTempPath()) ('bgi-e2e-atomic-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$source=Join-Path $folder 'source.bin';$target=Join-Path $folder 'target.bin'
try {
    [IO.File]::WriteAllText($source,'candidate')
    [IO.File]::WriteAllText($target,'old')
    Atomic-Copy $source $target $true
    if([IO.File]::ReadAllText($target) -ne 'candidate'){throw 'Existing-target replacement failed'}
    $new=Join-Path $folder 'new.bin';Atomic-Copy $source $new $false
    if([IO.File]::ReadAllText($new) -ne 'candidate'){throw 'New-target creation failed'}
    [IO.File]::WriteAllText($target,'concurrent-sentinel')
    try{Atomic-Copy $source $target $false;throw 'Unexpected existing-target overwrite'}catch{
        if($_.Exception.Message -eq 'Unexpected existing-target overwrite'){throw}
    }
    if([IO.File]::ReadAllText($target) -ne 'concurrent-sentinel'){throw 'Failed create changed existing target'}
    if(@(Get-ChildItem -LiteralPath $folder -Filter '*.tmp').Count){throw 'Owned temp files leaked'}
    $report=@{passed=$true;scenarios=3;scope='Real Windows PowerShell 5 / NTFS atomic replacement, create, and collision refusal in temporary fixtures';installedFilesTouched=$false;desktopAutomation=$false}
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'atomic-install-selftest.json') -Encoding UTF8
    $report | ConvertTo-Json
} finally {
    Get-ChildItem -LiteralPath $folder -File | Remove-Item
    Remove-Item -LiteralPath $folder
}
