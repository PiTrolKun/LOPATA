param([Parameter(Mandatory)][string]$VerifiedBundleDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch = Join-Path $root ('_tmp/file-publish-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$source = Get-Content -LiteralPath (Join-Path $VerifiedBundleDirectory 'file-update.build.json') -Raw | ConvertFrom-Json
if ($source.standBuild) { throw 'Use a locally built normal bundle, not a stand payload.' }
foreach ($file in Get-ChildItem -LiteralPath $VerifiedBundleDirectory -File | Where-Object { $_.Name -eq 'lopata-files.json' -or $_.Extension -eq '.zip' }) {
    New-Item -ItemType HardLink -Path (Join-Path $scratch $file.Name) -Target $file.FullName | Out-Null
}
# The simulated clean receipt exists only in this disposable fixture. All gh calls below are intercepted.
$source.sourceDirty = $false
$source | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $scratch 'file-update.build.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $scratch 'TEST_ONLY_DO_NOT_PUBLISH.txt'), 'Mock publication fixture; never publish this directory.')
$global:FilePublishCommands = [Collections.Generic.List[string]]::new()
$global:FilePublishAssets = [Collections.Generic.List[object]]::new()
$global:FilePublishBadHash = $false
$global:FilePublishResume = $false
function global:gh {
    $global:LASTEXITCODE = 0
    $line = $args -join ' '
    $global:FilePublishCommands.Add($line)
    if ($line -match '^api repos/.+/commits/') { return $source.sourceCommit }
    if ($line -match '^release view ') { return '999999' }
    if ($line -match '^release upload ') {
        $file = Get-Item -LiteralPath $args[3]
        $global:FilePublishAssets.Add(@{name=$file.Name;size=$file.Length;digest=('sha256:' + (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant())})
        return
    }
    if ($line -match '^api repos/.+/releases/999999$') {
        $assets = @($global:FilePublishAssets | ForEach-Object {
            @{name=$_.name;size=$_.size;digest=$(if ($global:FilePublishBadHash) { 'sha256:' + ('0'*64) } else { $_.digest })}
        })
        return @{id=999999;draft=$true;assets=$assets} | ConvertTo-Json -Depth 5
    }
    if ($line -match '^api repos/.+/releases\?') {
        $items = if ($global:FilePublishResume) { @(@{id=999999;tag_name=('v'+$source.version);draft=$true;target_commitish=$source.sourceCommit}) } else { @() }
        return ConvertTo-Json -InputObject @($items) -Depth 6
    }
    if ($line -match '^release (create|edit) ') { return }
    throw "Unexpected mocked GitHub call: $line"
}
try {
    $script = Join-Path $root 'Инструменты/publish-file-update.ps1'
    & $script -PackageDirectory $scratch
    if (@($global:FilePublishCommands | Where-Object { $_ -match '^release ' }).Count) { throw 'Preview performed a mutation.' }
    $global:FilePublishCommands.Clear()
    & $script -PackageDirectory $scratch -Publish
    $commands = $global:FilePublishCommands -join "`n"
    if ($commands -notmatch 'release create .+--draft' -or $commands -notmatch 'release edit .+--draft=false') { throw 'Draft workflow is missing.' }
    if ($commands -match 'delete|--clobber') { throw 'Publication attempted destructive cleanup.' }
    $global:FilePublishResume = $true; $global:FilePublishCommands.Clear()
    & $script -PackageDirectory $scratch -Publish -ResumeDraft
    if (($global:FilePublishCommands -join "`n") -match 'release create|release upload') { throw 'Resume did not reuse uploaded assets.' }
    $global:FilePublishBadHash = $true; $global:FilePublishCommands.Clear()
    $rejected = $false
    try { & $script -PackageDirectory $scratch -Publish -ResumeDraft } catch { $rejected = $true }
    if (!$rejected -or ($global:FilePublishCommands -join "`n") -match 'release edit') { throw 'A mismatching asset was published.' }
    Write-Host 'PASS: signed file bundle, metadata preview, draft publication, exact resume, corruption rejection; no network or GitHub mutations.'
}
finally { Remove-Item Function:\gh }
