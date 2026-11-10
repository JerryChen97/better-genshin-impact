param(
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [string]$SourceRef='HEAD')
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$install=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$manifest=Get-Content -LiteralPath (Join-Path $repoRoot 'LOCAL-CURRENT.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Read-Git([string[]]$GitArguments) {
    $value=& git --no-optional-locks -c safe.directory=* -C $repoRoot @GitArguments
    if($LASTEXITCODE -ne 0){throw 'Git source verification failed'}
    return ([string]($value -join "`n")).Trim()
}
function Safe-InstalledPath([string]$Relative) {
    $path=[IO.Path]::GetFullPath((Join-Path $install $Relative))
    if(-not $path.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes installation'}
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw ('Installed file is missing: '+$Relative)}
    return $path
}
function Read-LfHash([string]$Path) {
    $encoding=New-Object Text.UTF8Encoding($false,$true)
    $text=[IO.File]::ReadAllText($Path,$encoding).Replace("`r`n","`n")
    $hash=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($hash.ComputeHash($encoding.GetBytes($text)))).Replace('-','').ToLowerInvariant()}
    finally{$hash.Dispose()}
}
try{
    $branch=Read-Git @('symbolic-ref','--quiet','--short','HEAD')
    if($branch -ne $manifest.branch){throw 'Check out the canonical local/current branch'}
    $tree=Read-Git @('rev-parse','--verify',($SourceRef+':BetterGenshinImpact'))
    if($tree -ne $manifest.native.productionTree){throw 'Production source tree differs from the installed release record'}
    $integrationTree=Read-Git @('rev-parse','--verify',($SourceRef+':Integrations/AutoHoeingOneDragon'))
    if($integrationTree -ne $manifest.autoHoeing.integrationTree){throw 'JS integration source tree differs from the installed release record'}
    $null=Read-Git @('diff','--exit-code',$SourceRef,'--','BetterGenshinImpact','Integrations/AutoHoeingOneDragon')
    $extra=Read-Git @('ls-files','--others','--exclude-standard','--','BetterGenshinImpact','Integrations/AutoHoeingOneDragon')
    if($extra){throw 'Untracked native or JS integration source appeared after the recorded release'}
    $exe=Safe-InstalledPath 'BetterGI.exe'
    $version=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $exeHash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if($version -ne $manifest.native.version -or $exeHash -ne $manifest.native.sha256){throw 'Installed executable differs from the release record'}
    $files=@()
    foreach($item in $manifest.autoHoeing.files){
        $path=Safe-InstalledPath $item.file
        $actual=Read-LfHash $path
        if($actual -ne $item.lfSha256){throw ('Installed script differs from the release record: '+$item.file)}
        $files+=@{file=$item.file;passed=$true;lfSha256=$actual}
    }
    @{passed=$true;branch=$branch;sourceRef=$SourceRef;productionTree=$tree;integrationTree=$integrationTree;version=$version;exeSha256=$exeHash;scripts=$files;readOnly=$true;gameInput=$false} | ConvertTo-Json -Depth 6
    exit 0
}catch{
    @{passed=$false;error=$_.Exception.Message;readOnly=$true;gameInput=$false} | ConvertTo-Json
    exit 1
}
