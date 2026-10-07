param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$ChatLlmBackendDir,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PreviousManifestPath,
    [string]$HistoryPath,
    [switch]$StandBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(-beta)?$') { throw 'File delivery requires an explicit beta or public version.' }
$notesText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesPath).Path).Trim()
if (!$notesText) { throw 'User-facing release notes are required.' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$packaging = Join-Path $out 'build-inputs'
New-Item -ItemType Directory -Path $packaging -Force | Out-Null
$roots = [ordered]@{ app = (Resolve-Path -LiteralPath $PublishDir).Path }
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDir 'AIHub.dll')).ProductVersion
if ($payloadVersion -ne $Version) { throw "Application payload version differs from target version: $payloadVersion / $Version" }
if ((Test-Path -LiteralPath (Join-Path $PublishDir 'lopata-stand-build.marker')) -and !$StandBuild) {
    throw 'Stand payload cannot be packaged as a public update.'
}
# Mirror the installer payload precisely, without logs or user data from runtime folders.
foreach ($pair in ([ordered]@{ chatllm = $ChatLlmBackendDir }).GetEnumerator()) {
    $source = (Resolve-Path -LiteralPath $pair.Value).Path
    $destination = Join-Path $packaging ($pair.Key + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        if ($file.Extension -eq '.log') { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked backend files cannot be packaged.' }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    $roots[$pair.Key] = $destination
}
if ($HistoryPath) { $history = @(Get-Content -LiteralPath $HistoryPath -Raw | ConvertFrom-Json) }
else {
    $feed = & gh api 'repos/PiTrolKun/LOPATA/releases?per_page=100' --paginate --slurp
    if ($LASTEXITCODE -ne 0) { throw 'Release history is unavailable. Provide a previously verified HistoryPath to build offline.' }
    $releases = @($feed | Out-String | ConvertFrom-Json | ForEach-Object { $_ } | ForEach-Object { $_ })
    $history = @($releases | Where-Object { !$_.draft -and $_.tag_name -match '^v\d+\.\d+\.\d+(-beta)?$' } | ForEach-Object {
        [ordered]@{ version = $_.tag_name.Substring(1); publishedUtc = $_.published_at; text = $_.body }
    })
}
$targetVersion = [version]($Version -replace '-beta$', '')
$history = @($history | Where-Object { [version]($_.version -replace '-beta$', '') -lt $targetVersion })
if (@($history | Where-Object { [string]::IsNullOrWhiteSpace($_.text) }).Count) { throw 'Published history has empty notes; resolve them before packaging.' }
$history += [ordered]@{ version = $Version; publishedUtc = [DateTimeOffset]::UtcNow.ToString('o'); text = $notesText }
$history = @($history | Sort-Object { [version]($_.version -replace '-beta$', '') } -Unique)
$history | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packaging 'history.json') -Encoding utf8
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
$privatePath = Join-Path $env:LOCALAPPDATA 'LOPATA-Publishing/Keys/file-updates-p256.pem'
if (!(Test-Path -LiteralPath $privatePath)) { throw 'The release signing key is not available. Restore it from its secure backup.' }
$config = [ordered]@{
    roots = $roots; publicKeysPath = Join-Path $root 'Исходники/LOPATA.Updates/release-keys.json'
    privateKeyPath = $privatePath; keyId = 'lopata-file-updates-1'; version = $Version
    sourceCommit = $commit; notes = $history; outputDirectory = $out
    previousManifestPath = $(if ($PreviousManifestPath) { (Resolve-Path -LiteralPath $PreviousManifestPath).Path } else { $null })
}
$configPath = Join-Path $packaging 'pack.json'
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
dotnet build (Join-Path $root 'Исходники/LOPATA.UpdateTool/LOPATA.UpdateTool.csproj') --configuration Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Packaging tool build failed.' }
& dotnet (Join-Path $root 'Исходники/LOPATA.UpdateTool/bin/Release/net10.0/LOPATA.UpdateTool.dll') pack $configPath
if ($LASTEXITCODE -ne 0) { throw 'Signed package build failed.' }
$sourceChanges = & git -C $root status --porcelain --untracked-files=all -- 'Исходники' 'Инструменты' 'VERSION' 'Каталоги' 'LICENSE' 'NOTICE.md' 'THIRD_PARTY_NOTICES.md'
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source state.' }
@{ schemaVersion = 1; version = $Version; sourceCommit = $commit; sourceDirty = [bool]$sourceChanges; standBuild = [bool]$StandBuild;
    manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $out 'lopata-files.json')).Hash.ToLowerInvariant() } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'file-update.build.json') -Encoding utf8
Write-Host "File update ready: $(Join-Path $out 'lopata-files.json')"
