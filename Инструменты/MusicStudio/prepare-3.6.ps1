param(
    [string]$Workspace = 'H:\AI_HUB',
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Portable,
    [string]$BuiltServer,
    [string]$BuiltServerSha256
)
$ErrorActionPreference = 'Stop'
$revision = 'v3.6.0-lopata-paths-1'
$commit = 'c3e214f3a65f3f56c18e107127ebd2ebad5150ad'
$engineCommit = 'c8516dbf44d660cca40d3f420c67063e37ec8258'
$archiveHash = '04eac6e480152cc3ab5467f6fc51a60ae8445046df940ade87b9a1417c37a0f5'
if ((git -C $Source rev-parse HEAD).Trim() -ne $commit) { throw 'Unexpected Studio source revision' }
if ((Get-FileHash -LiteralPath $Portable).Hash.ToLowerInvariant() -ne $archiveHash) { throw 'Portable release checksum mismatch' }
$patch = Join-Path $PSScriptRoot 'lopata-paths-3.6.patch'
git -C $Source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $Source apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'Studio adapter patch cannot be applied' }
    git -C $Source apply $patch
}
if ($BuiltServer) {
    if (!$BuiltServerSha256 -or (Get-FileHash -LiteralPath $BuiltServer).Hash.ToLowerInvariant() -ne $BuiltServerSha256.ToLowerInvariant()) {
        throw 'An existing tested server needs its exact expected SHA256'
    }
} else {
    # Run from an MSVC developer shell with CMake available. VS generator avoids libflac's Ninja Release path mismatch.
    $env:CMAKE_GENERATOR = 'Visual Studio 17 2022'
    cargo build --locked --release -p music-server --jobs 4 --manifest-path (Join-Path $Source 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Studio build failed' }
    $BuiltServer = Join-Path $Source 'target/release/music-server.exe'
}
$target = Join-Path $Workspace ('Runtime/MusicStudio/' + $revision)
if (Test-Path -LiteralPath $target) { throw 'An immutable runtime already exists; do not overwrite it' }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -LiteralPath $BuiltServer -Destination (Join-Path $target 'music-server.exe')
Copy-Item -LiteralPath (Join-Path $Source 'LICENSE') -Destination (Join-Path $target 'LICENSE-Studio.txt')
Copy-Item -LiteralPath (Join-Path $Source 'Cargo.lock') -Destination $target
Copy-Item -LiteralPath $patch -Destination $target
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Portable)
try {
    foreach ($entry in $zip.Entries) {
        $entryName = $entry.FullName.Replace('\','/')
        if (!$entryName.StartsWith('resources/yue2-cpp/') -or $entryName.EndsWith('/')) { continue }
        $path = [IO.Path]::GetFullPath((Join-Path $target ('engine/' + $entryName.Substring('resources/yue2-cpp/'.Length))))
        if (!$path.StartsWith([IO.Path]::GetFullPath($target) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe ZIP path' }
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
    }
} finally { $zip.Dispose() }
if (!(Test-Path -LiteralPath (Join-Path $target 'engine/yue-server.exe'))) { throw 'Portable archive contains no native engine' }
# Unchanged, verified redistributables: no generation-time download and no global CUDA installation.
$legacy = Join-Path $Workspace 'Runtime/MusicStudio/v3.4.0-lopata-paths-1'
$legacyManifest = Get-Content -LiteralPath (Join-Path $legacy 'manifest.json') -Raw | ConvertFrom-Json
foreach ($name in @('engine/cublas64_12.dll','engine/cublasLt64_12.dll','engine/cublas64_13.dll','engine/cublasLt64_13.dll',
    'CUDA-12.9.1.4-LICENSE','CUDA-13.5.1.27-LICENSE','LICENSE-MSVC.txt','LICENSE-ggml.txt','LICENSE-yyjson.txt')) {
    $expected = @($legacyManifest.Files | Where-Object Name -eq $name)
    $path = Join-Path $legacy $name
    if ($expected.Count -ne 1 -or (Get-Item -LiteralPath $path).Length -ne $expected[0].Bytes -or
        (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $expected[0].Sha256) { throw "Legacy redistributable mismatch: $name" }
    Copy-Item -LiteralPath $path -Destination (Join-Path $target $name)
}
$sourceArchive = Join-Path $target 'Studio-source.zip'
git -C $Source archive --format=zip --output=$sourceArchive $commit
if ($LASTEXITCODE -ne 0) { throw 'Studio source archive failed' }
$metadata = cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1 --manifest-path (Join-Path $Source 'Cargo.toml') | ConvertFrom-Json -AsHashtable
if ($LASTEXITCODE -ne 0) { throw 'Pinned dependency metadata failed' }
$notices = Join-Path $target 'CargoLicenses'
$sources = Join-Path $target 'CargoSources'
New-Item -ItemType Directory -Path $notices, $sources | Out-Null
$cargoRoot = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$dependencyList = foreach ($package in $metadata.packages) {
    $folder = Split-Path $package.manifest_path
    foreach ($file in Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(LICENSE|LICENCE|COPYING|NOTICE)') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices ($package.name + '-' + $package.version + '-' + $file.Name))
    }
    if ($package.source -like 'registry+*') {
        $archiveName = $package.name + '-' + $package.version + '.crate'
        $archives = @(Get-ChildItem -LiteralPath (Join-Path $cargoRoot 'registry/cache') -Filter $archiveName -Recurse -File)
        if ($archives.Count -ne 1) { throw "Missing pinned crate source: $archiveName" }
        Copy-Item -LiteralPath $archives[0].FullName -Destination $sources
    }
    [ordered]@{ Name=$package.name; Version=$package.version; License=$package.license; Source=$package.source; Repository=$package.repository }
}
$dependencyList | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'dependencies.json') -Encoding utf8
$lame = $metadata.packages | Where-Object name -eq 'mp3lame-sys'
Copy-Item -LiteralPath (Join-Path (Split-Path $lame.manifest_path) 'lame-3.100/COPYING') -Destination (Join-Path $target 'LICENSE-LAME.txt')
$engineLicense = Invoke-WebRequest ('https://raw.githubusercontent.com/timoncool/yue2.cpp/' + $engineCommit + '/LICENSE')
[IO.File]::WriteAllText((Join-Path $target 'LICENSE-yue2.txt'), $engineLicense.Content, [Text.UTF8Encoding]::new($false))
$files = Get-ChildItem -LiteralPath $target -File -Recurse | ForEach-Object { [ordered]@{
    Name=[IO.Path]::GetRelativePath($target,$_.FullName).Replace('\','/'); Bytes=$_.Length;
    Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } }
[ordered]@{ Revision=$revision; SourceRevision=$commit; EngineRevision=$engineCommit; ReleaseArchiveSHA256=$archiveHash;
    AdapterPatchSHA256=(Get-FileHash -LiteralPath $patch).Hash.ToLowerInvariant(); Files=@($files) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
Write-Output $target
