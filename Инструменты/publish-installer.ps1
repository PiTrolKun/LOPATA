param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$NotesPath,
    [switch]$Publish,
    [switch]$PruneInstallers
)

$ErrorActionPreference = 'Stop'
$repo = 'PiTrolKun/LOPATA'
$root = Split-Path -Parent $PSScriptRoot
function Invoke-GitHub {
    param([string[]]$Arguments)
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0])" }
    return $result
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$notes = (Resolve-Path -LiteralPath $NotesPath).Path
$receipt = Get-Content -LiteralPath "$installer.build.json" -Raw | ConvertFrom-Json
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
# Read the complete paginated list as one JSON array.
$releases = @(Invoke-GitHub @('api', "repos/$repo/releases?per_page=100", '--paginate', '--slurp') |
    Out-String | ConvertFrom-Json | ForEach-Object { $_ })
$releases = @($releases | ForEach-Object { $_ })
if ($releases | Where-Object { $_.tag_name -eq $tag }) { throw "Release $tag already exists. Do not overwrite published installers." }
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
$manifestDirectory = Join-Path ([IO.Path]::GetDirectoryName($installer)) "release-$($receipt.version)"
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null
$manifest = Join-Path $manifestDirectory 'lopata-update.json'
[ordered]@{ schemaVersion = 1; version = $receipt.version; fileName = $file.Name; size = $file.Length;
    sha256 = $receipt.sha256; sourceCommit = $receipt.sourceCommit } |
    ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8

$create = @('release', 'create', $tag, '--repo', $repo, '--target', $receipt.sourceCommit,
    '--title', "LOPATA $($receipt.version)", '--notes-file', $notes, '--draft')
if ($receipt.version.EndsWith('-beta')) { $create += '--prerelease' }
Invoke-GitHub $create
Invoke-GitHub @('release', 'upload', $tag, $installer, $manifest, '--repo', $repo)
$release = Invoke-GitHub @('api', "repos/$repo/releases/tags/$tag") | Out-String | ConvertFrom-Json
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
