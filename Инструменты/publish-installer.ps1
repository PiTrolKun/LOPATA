param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$NotesPath,
    [switch]$Publish,
    [switch]$PruneInstallers,
    [switch]$ResumeDraft
)

$ErrorActionPreference = 'Stop'
$repo = 'PiTrolKun/LOPATA'
$root = Split-Path -Parent $PSScriptRoot
function Invoke-GitHub {
    param([string[]]$Arguments)
    # These api calls use GET only. Never retry mutations with an unknown outcome.
    $readOnlyApi = $Arguments[0] -eq 'api' -and
        @($Arguments | Where-Object { $_ -cmatch '^(-X|-f|-F|--method|--field|--raw-field|--input)(=|$)' }).Count -eq 0
    $attempts = if ($readOnlyApi) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $result = & gh @Arguments
        if ($LASTEXITCODE -eq 0) { return $result }
        if ($attempt -lt $attempts) {
            Write-Warning "GitHub read failed; retry $attempt/$($attempts - 1)."
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    throw "GitHub command failed: $($Arguments[0])"
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$notes = (Resolve-Path -LiteralPath $NotesPath).Path
$receipt = Get-Content -LiteralPath "$installer.build.json" -Raw | ConvertFrom-Json
if ($receipt.standBuild) { throw 'An isolated stand installer must never be published.' }
if ($receipt.schemaVersion -ne 1 -or $receipt.version -notmatch '^\d+\.\d+\.\d+(-beta)?$') {
    throw 'Only stable or beta releases can be published; dev builds are internal.'
}
if ($receipt.sourceDirty -ne $false -or $receipt.payloadVersion -ne $receipt.version) {
    throw 'Build receipt must refer to clean committed sources and a matching payload version. Rebuild first.'
}
if ($receipt.sourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid source commit.' }
$file = Get-Item -LiteralPath $installer
if ($file.Name -ne "LOPATA_Setup_$($receipt.version).exe" -or $receipt.fileName -ne $file.Name -or
    $file.Length -ne $receipt.size -or $file.Length -ge 2GB -or
    (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $receipt.sha256) {
    throw 'Installer does not match the build receipt, or exceeds the GitHub asset size limit.'
}
$tag = "v$($receipt.version)"
$bundle = $null
if ($receipt.fileManifest) {
    if ((Get-FileHash -LiteralPath $receipt.fileManifest).Hash.ToLowerInvariant() -ne $receipt.fileManifestSha256) {
        throw 'Signed file manifest changed after installer build.'
    }
    $bundle = & (Join-Path $PSScriptRoot 'get-update-bundle.ps1') -ManifestPath $receipt.fileManifest `
        -Version $receipt.version -SourceCommit $receipt.sourceCommit
    $releaseNote = @($bundle.Manifest.notes | Where-Object version -eq $receipt.version)
    if ($releaseNote.Count -ne 1 -or $releaseNote[0].text.Trim() -ne [IO.File]::ReadAllText($notes).Trim()) {
        throw 'Publication notes must match the notes shown by the signed update.'
    }
}
elseif ([version]($receipt.version -replace '-beta$','') -ge [version]'0.2.42') {
    throw 'This release requires a signed file update bundle.'
}
# Read the complete paginated list as one JSON array.
$releases = @(Invoke-GitHub @('api', "repos/$repo/releases?per_page=100", '--paginate', '--slurp') |
    Out-String | ConvertFrom-Json | ForEach-Object { $_ })
$releases = @($releases | ForEach-Object { $_ })
$existing = $releases | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
if ($existing -and (-not $ResumeDraft -or -not $existing.draft)) { throw "Release $tag already exists. Only an explicit ResumeDraft may continue an unpublished release." }
if ($ResumeDraft -and (-not $existing -or $existing.target_commitish -ne $receipt.sourceCommit)) { throw 'Draft must exist and refer to the exact build commit.' }
Write-Host "Version: $($receipt.version); source: $($receipt.sourceCommit)"
Write-Host "Installer: $installer; size: $($file.Length); SHA256: $($receipt.sha256)"
Write-Host "Release notes: $notes"

if (-not $Publish) {
    Write-Host 'Preview only. To publish this exact installer, run again with -Publish.'
    return
}

# Ensure the exact source commit is available in the public repository before creating the release.
$remoteCommit = Invoke-GitHub @('api', "repos/$repo/commits/$($receipt.sourceCommit)", '--jq', '.sha')
if ($remoteCommit -ne $receipt.sourceCommit) { throw 'Source commit is not available on GitHub.' }
if ($bundle) {
    $referencedReleases = @{}
    foreach ($package in $bundle.Reused) {
        $oldTag = ([uri]$package.url).Segments[-2].TrimEnd('/')
        if (-not $referencedReleases.ContainsKey($oldTag)) {
            $referencedReleases[$oldTag] = Invoke-GitHub @('api', "repos/$repo/releases/tags/$oldTag") | Out-String | ConvertFrom-Json
        }
        $oldRelease = $referencedReleases[$oldTag]
        $oldAsset = @($oldRelease.assets | Where-Object name -eq $package.id)
        if ($oldRelease.draft -or $oldAsset.Count -ne 1 -or $oldAsset[0].size -ne $package.size -or $oldAsset[0].digest -ne "sha256:$($package.sha256)") {
            throw "Referenced package is unavailable or changed: $($package.id). No release published."
        }
    }
}
$manifestDirectory = Join-Path ([IO.Path]::GetDirectoryName($installer)) "release-$($receipt.version)"
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
$manifest = Join-Path $manifestDirectory 'lopata-update.json'
[ordered]@{ schemaVersion = 1; version = $receipt.version; fileName = $file.Name; size = $file.Length;
    sha256 = $receipt.sha256; sourceCommit = $receipt.sourceCommit } |
    ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8

$create = @('release', 'create', $tag, '--repo', $repo, '--target', $receipt.sourceCommit,
    '--title', "LOPATA $($receipt.version)", '--notes-file', $notes, '--draft')
if ($receipt.version.EndsWith('-beta')) { $create += '--prerelease' }
if (-not $existing) { Invoke-GitHub $create }
# Drafts do not necessarily resolve through /releases/tags/{tag}. Resolve their database ID first.
$releaseId = Invoke-GitHub @('release', 'view', $tag, '--repo', $repo, '--json', 'databaseId', '--jq', '.databaseId')
if ($releaseId -notmatch '^\d+$') { throw 'Cannot identify draft release.' }
$release = Invoke-GitHub @('api', "repos/$repo/releases/$releaseId") | Out-String | ConvertFrom-Json
$uploads = @($installer, $manifest)
if ($bundle) { $uploads += @($bundle.Assets) }
if ($receipt.onlineInstaller) {
    if ((Get-Item -LiteralPath $receipt.onlineInstaller).Name -ne "LOPATA_Online_Setup_$($receipt.version).exe" -or
        (Get-FileHash -LiteralPath $receipt.onlineInstaller).Hash.ToLowerInvariant() -ne $receipt.onlineInstallerSha256) {
        throw 'Online installer differs from its build receipt. Release remains a draft.'
    }
    $uploads += $receipt.onlineInstaller
}
foreach ($upload in $uploads) {
    $uploadName = [IO.Path]::GetFileName($upload)
    $remoteAsset = $release.assets | Where-Object name -eq $uploadName | Select-Object -First 1
    if ($remoteAsset) {
        if ($remoteAsset.digest -ne ('sha256:' + (Get-FileHash -LiteralPath $upload).Hash.ToLowerInvariant())) {
            throw "Draft asset $uploadName differs from the selected build. No overwrite performed."
        }
    }
    else { Invoke-GitHub @('release', 'upload', $tag, $upload, '--repo', $repo) }
}
$release = Invoke-GitHub @('api', "repos/$repo/releases/$releaseId") | Out-String | ConvertFrom-Json
foreach ($upload in $uploads) {
    $local = Get-Item -LiteralPath $upload
    $remote = @($release.assets | Where-Object name -eq $local.Name)
    if ($remote.Count -ne 1 -or ($null -ne $remote[0].size -and $remote[0].size -ne $local.Length) -or
        $remote[0].digest -ne ('sha256:' + (Get-FileHash -LiteralPath $upload).Hash.ToLowerInvariant())) {
        throw "Uploaded asset verification failed: $($local.Name). Release remains a draft."
    }
}
$asset = @($release.assets | Where-Object { $_.name -eq $file.Name })
if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or $asset[0].digest -ne "sha256:$($receipt.sha256)") {
    throw 'Uploaded installer verification failed. The release remains a draft.'
}
$manifestAsset = @($release.assets | Where-Object { $_.name -eq 'lopata-update.json' })
if ($manifestAsset.Count -ne 1 -or $manifestAsset[0].digest -ne ('sha256:' + (Get-FileHash -LiteralPath $manifest).Hash.ToLowerInvariant())) {
    throw 'Uploaded update manifest verification failed. The release remains a draft.'
}
$edit = @('release', 'edit', $tag, '--repo', $repo, '--draft=false')
if (-not $receipt.version.EndsWith('-beta')) { $edit += '--latest' }
Invoke-GitHub $edit
Write-Host "Published: https://github.com/$repo/releases/tag/$tag"

# Retain the first available installer of the current X.Y line and the last two installers.
# Select assets using the outer release tag explicitly (PowerShell pipeline scopes differ).
$available = @($releases + $release | Where-Object {
    $expected = "LOPATA_Setup_$($_.tag_name -replace '^v','').exe"
    ($_.draft -eq $false -or $_.id -eq $release.id) -and
    $_.tag_name -match '^v\d+\.\d+\.\d+(-beta)?$' -and @($_.assets | Where-Object name -eq $expected).Count -eq 1
} | Sort-Object { [version](($_.tag_name -replace '^v','') -replace '-beta$','') }, { -not $_.prerelease })
$line = ($receipt.version -split '\.')[0..1] -join '.'
$baseline = $available | Where-Object { $_.tag_name -match "^v$([regex]::Escape($line))\." } |
    Sort-Object { if ($_.published_at) { [DateTimeOffset]$_.published_at } else { [DateTimeOffset]::MaxValue } } |
    Select-Object -First 1
$keepIds = @($available | Select-Object -Last 2 | ForEach-Object id) + @($baseline | ForEach-Object id)
foreach ($old in $available | Where-Object { $_.id -notin $keepIds }) {
    $oldName = "LOPATA_Setup_$($old.tag_name -replace '^v','').exe"
    Write-Host "Old installer eligible for removal: $($old.tag_name)/$oldName"
    if ($PruneInstallers) {
        Invoke-GitHub @('release', 'delete-asset', $old.tag_name, $oldName, '--repo', $repo, '--yes')
    }
}
if (-not $PruneInstallers) { Write-Host 'Old installers retained. Removal requires the explicit -PruneInstallers switch during publication.' }
