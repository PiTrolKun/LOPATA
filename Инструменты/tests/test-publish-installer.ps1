$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch = Join-Path $root ('_tmp/publish-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$installer = Join-Path $scratch 'LOPATA_Setup_0.1.53-beta.exe'
[IO.File]::WriteAllBytes($installer, [byte[]](1,2,3,4))
$notes = Join-Path $scratch 'notes.md'
Set-Content -LiteralPath $notes -Value 'Test only; no external commands.'
$hash = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant()
[ordered]@{ schemaVersion=1; version='0.1.53-beta'; payloadVersion='0.1.53-beta'; sourceDirty=$false;
    sourceCommit=('a'*40); fileName=[IO.Path]::GetFileName($installer); size=4; sha256=$hash } |
    ConvertTo-Json | Set-Content -LiteralPath "$installer.build.json"
$global:UpdatePublishCommands = [Collections.Generic.List[string]]::new()
$global:UpdatePublishHash = $hash
$global:UpdatePublishBadHash = $false
function global:gh {
    $global:LASTEXITCODE = 0
    $line = $args -join ' '
    $global:UpdatePublishCommands.Add($line)
    if ($line -match '^api repos/.+/commits/') { return ('a'*40) }
    if ($line -match '^api repos/.+/releases/tags/') {
        return @{ id=53; tag_name='v0.1.53-beta'; draft=$true; prerelease=$true; assets=@(
            @{name='LOPATA_Setup_0.1.53-beta.exe';size=4;digest=('sha256:' + $(if($global:UpdatePublishBadHash){'b'*64}else{$global:UpdatePublishHash}))},
            @{name='lopata-update.json';digest=('sha256:'+(Get-FileHash -LiteralPath (Join-Path $scratch 'release-0.1.53-beta/lopata-update.json')).Hash.ToLowerInvariant())}) } | ConvertTo-Json -Depth 5
    }
    if ($line -match '^api repos/.+/releases\?') {
        # Simulate multiple pages, older lines and current-line baseline.
        $items = foreach ($version in @('0.0.99-beta','0.1.1-beta','0.1.51-beta','0.1.52-beta')) {
            @{id=$version; tag_name="v$version"; draft=$false; prerelease=$true;
              assets=@(@{name="LOPATA_Setup_$version.exe";size=4})}
        }
        return ConvertTo-Json -InputObject @(@($items[0..1]), @($items[2..3])) -Depth 6
    }
}
try {
    & "$root/Инструменты/publish-installer.ps1" -InstallerPath $installer -NotesPath $notes
    if (@($global:UpdatePublishCommands | Where-Object { $_ -match '^release ' }).Count) { throw 'Preview performed a mutation.' }
    $global:UpdatePublishCommands.Clear()
    & "$root/Инструменты/publish-installer.ps1" -InstallerPath $installer -NotesPath $notes -Publish -PruneInstallers
    $commands = $global:UpdatePublishCommands -join "`n"
    if ($commands -notmatch 'release create .+--draft --prerelease' -or $commands -notmatch 'release edit .+--draft=false') { throw 'Draft workflow missing.' }
    if ($commands -match 'delete-asset v0.1.1-beta' -or $commands -match 'delete-asset v0.1.52-beta') { throw 'Retention removed baseline or recent installer.' }
    if ($commands -notmatch 'delete-asset v0.0.99-beta' -or $commands -notmatch 'delete-asset v0.1.51-beta') { throw 'Retention did not identify expired installers.' }
    $global:UpdatePublishCommands.Clear(); $global:UpdatePublishBadHash=$true
    $rejected=$false
    try { & "$root/Инструменты/publish-installer.ps1" -InstallerPath $installer -NotesPath $notes -Publish }
    catch { $rejected=$true }
    if (-not $rejected -or ($global:UpdatePublishCommands -join "`n") -match 'release edit') { throw 'Corrupt upload was published.' }
    Write-Host 'PASS: preview, draft publication, checksum rejection, multi-page retention; no GitHub mutations.'
}
finally { Remove-Item Function:\gh }
