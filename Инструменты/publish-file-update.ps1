param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$Publish,
    [switch]$ResumeDraft
)
$ErrorActionPreference = 'Stop'
$repo = 'PiTrolKun/LOPATA'
function Invoke-UpdateGitHub {
    param([string[]]$Arguments)
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0])" }
    return $result
}
$folder = (Resolve-Path -LiteralPath $PackageDirectory).Path
$receipt = Get-Content -LiteralPath (Join-Path $folder 'file-update.build.json') -Raw | ConvertFrom-Json
if ($receipt.standBuild) { throw 'An isolated stand bundle must never be published.' }
$manifestPath = Join-Path $folder 'lopata-files.json'
if ($receipt.schemaVersion -ne 1 -or $receipt.sourceDirty -ne $false -or
    $receipt.version -notmatch '^\d+\.\d+\.\d+(-beta)?$' -or $receipt.sourceCommit -notmatch '^[a-f0-9]{40}$' -or
    (Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant() -ne $receipt.manifestSha256) {
    throw 'A file update must come from clean committed sources and an unchanged signed build.'
}
$bundle = & (Join-Path $PSScriptRoot 'get-update-bundle.ps1') -ManifestPath $manifestPath -Version $receipt.version -SourceCommit $receipt.sourceCommit
$version = $receipt.version; $tag = "v$version"
$notes = @($bundle.Manifest.notes | Where-Object version -eq $version)
if ($notes.Count -ne 1) { throw 'Current release notes are missing.' }
$notesPath = Join-Path $folder 'release-notes.md'
[IO.File]::WriteAllText($notesPath, $notes[0].text, [Text.UTF8Encoding]::new($false))
$releases = Invoke-UpdateGitHub @('api', "repos/$repo/releases?per_page=100", '--paginate', '--slurp') |
    Out-String | ConvertFrom-Json | ForEach-Object { $_ } | ForEach-Object { $_ }
$existing = $releases | Where-Object tag_name -eq $tag | Select-Object -First 1
if ($existing -and (!$ResumeDraft -or !$existing.draft)) { throw 'The version already exists; only an explicitly resumed draft may be continued.' }
if ($ResumeDraft -and (!$existing -or $existing.target_commitish -ne $receipt.sourceCommit)) { throw 'The draft does not match the source commit.' }
Write-Host "File update ${version}: $($bundle.Assets.Count) assets; source $($receipt.sourceCommit)"
if (!$Publish) { Write-Host 'Preview only. No GitHub changes.'; return }
$commit = Invoke-UpdateGitHub @('api', "repos/$repo/commits/$($receipt.sourceCommit)", '--jq', '.sha')
if ($commit -ne $receipt.sourceCommit) { throw 'The exact source commit must be available on GitHub.' }
$verifiedReleases = @{}
foreach ($package in $bundle.Reused) {
    $oldTag = ([uri]$package.url).Segments[-2].TrimEnd('/')
    if (!$verifiedReleases.ContainsKey($oldTag)) {
        $verifiedReleases[$oldTag] = Invoke-UpdateGitHub @('api', "repos/$repo/releases/tags/$oldTag") | Out-String | ConvertFrom-Json
    }
    $release = $verifiedReleases[$oldTag]
    $asset = @($release.assets | Where-Object name -eq $package.id)
    if ($release.draft -or $asset.Count -ne 1 -or $asset[0].size -ne $package.size -or $asset[0].digest -ne "sha256:$($package.sha256)") {
        throw "Referenced package is unavailable: $($package.id)"
    }
}
if (!$existing) {
    $create = @('release', 'create', $tag, '--repo', $repo, '--target', $receipt.sourceCommit,
        '--title', "LOPATA $version", '--notes-file', $notesPath, '--draft')
    if ($version.EndsWith('-beta')) { $create += '--prerelease' }
    Invoke-UpdateGitHub $create
}
$id = Invoke-UpdateGitHub @('release', 'view', $tag, '--repo', $repo, '--json', 'databaseId', '--jq', '.databaseId')
if ($id -notmatch '^\d+$') { throw 'Cannot identify the draft.' }
$draft = Invoke-UpdateGitHub @('api', "repos/$repo/releases/$id") | Out-String | ConvertFrom-Json
foreach ($path in $bundle.Assets) {
    $name = [IO.Path]::GetFileName($path)
    $asset = @($draft.assets | Where-Object name -eq $name)
    if ($asset.Count) {
        if ($asset.Count -ne 1 -or $asset[0].digest -ne ('sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant())) {
            throw "Draft contains a different asset: $name. No overwrite performed."
        }
    }
    else { Invoke-UpdateGitHub @('release', 'upload', $tag, $path, '--repo', $repo) }
}
$draft = Invoke-UpdateGitHub @('api', "repos/$repo/releases/$id") | Out-String | ConvertFrom-Json
foreach ($path in $bundle.Assets) {
    $file = Get-Item -LiteralPath $path
    $asset = @($draft.assets | Where-Object name -eq $file.Name)
    if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or
        $asset[0].digest -ne ('sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant())) {
        throw "Upload verification failed: $($file.Name). The release remains a draft."
    }
}
Invoke-UpdateGitHub @('release', 'edit', $tag, '--repo', $repo, '--draft=false')
Write-Host "Published: https://github.com/$repo/releases/tag/$tag"
# Never remove old releases or packages: retained manifests can still reference them.
