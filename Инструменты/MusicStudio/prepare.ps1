param([string]$Workspace = 'H:\AI_HUB', [string]$Source, [string]$Portable)
$ErrorActionPreference = 'Stop'
$revision = 'v3.4.0-lopata-paths-1'
$commit = '9125be3cf9ba720ac439a81cd09ff8bcc2a00368'
if (!$Source) { $Source = Join-Path $Workspace '_tmp/studio-q8/source' }
if (!$Portable) { $Portable = Join-Path $Workspace '_tmp/studio-q8/YuE2-Studio-3.4.0-portable-windows-x64.zip' }
if ((git -C $Source rev-parse HEAD).Trim() -ne $commit) { throw 'Unexpected Studio source revision' }
if ((Get-FileHash -LiteralPath $Portable -Algorithm SHA256).Hash.ToLowerInvariant() -ne '3aa7859346605a63f859edf56db8f6d1047588edd9d0c40fe39cb746f0bdfb71') { throw 'Portable release checksum mismatch' }
$patch = Join-Path $PSScriptRoot 'lopata-paths.patch'
git -C $Source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $Source apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'Studio adapter patch cannot be applied' }
    git -C $Source apply $patch
}
Push-Location $Source
try { cargo build --locked --release -p music-server; if ($LASTEXITCODE -ne 0) { throw 'Studio build failed' } }
finally { Pop-Location }
$target = Join-Path $Workspace ('Runtime/MusicStudio/' + $revision)
if (Test-Path -LiteralPath $target) { throw 'Do not overwrite an existing runtime; back it up explicitly first' }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $Source 'target/release/music-server.exe') -Destination $target
Copy-Item -LiteralPath (Join-Path $Source 'LICENSE') -Destination (Join-Path $target 'LICENSE-Studio.txt')
Copy-Item -LiteralPath (Join-Path $Source 'Cargo.lock') -Destination $target
Copy-Item -LiteralPath $patch -Destination $target
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Portable)
try {
    foreach ($entry in $zip.Entries) {
        if (!$entry.FullName.StartsWith('resources/yue2-cpp/') -or $entry.FullName.EndsWith('/')) { continue }
        $relative = 'engine/' + $entry.FullName.Substring('resources/yue2-cpp/'.Length)
        $path = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (!$path.StartsWith([IO.Path]::GetFullPath($target) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe ZIP path' }
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
    }
} finally { $zip.Dispose() }
# Bundle both official CUDA builds: generation must never perform an implicit runtime download.
$cudaAssets = @(
    @{Version='13.5.1.27'; Sha256='c946e1c825e05895747a95ed4fee18030b08052c09783b9b7b19818fd2e31f58'; Dlls=@('cublas64_13.dll','cublasLt64_13.dll')},
    @{Version='12.9.1.4'; Sha256='d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed'; Dlls=@('cublas64_12.dll','cublasLt64_12.dll')}
)
foreach ($asset in $cudaAssets) {
    $archive = Join-Path $Workspace ('_tmp/studio-q8/cublas-' + $asset.Version + $(if ($asset.Version -eq '13.5.1.27') { '-pinned' } else { '' }) + '.zip')
    if (!(Test-Path $archive)) { throw "Download the official NVIDIA archive first: $archive" }
    if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $asset.Sha256) { throw 'NVIDIA archive checksum mismatch' }
    $cudaZip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($dll in $asset.Dlls) {
            $entries = @($cudaZip.Entries | Where-Object { $_.Name -eq $dll })
            if ($entries.Count -ne 1) { throw "Missing CUDA library: $dll" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $target ('engine/' + $dll)), $false)
        }
        foreach ($entry in $cudaZip.Entries | Where-Object { $_.Name -match 'LICENSE' }) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target ('CUDA-' + $asset.Version + '-' + $entry.Name)), $false)
        }
    } finally { $cudaZip.Dispose() }
}
# Original upstream sources plus the small adapter patch allow reconstruction.
$sourceArchive = Join-Path $target 'Studio-source.zip'
git -C $Source archive --format=zip --output=$sourceArchive $commit
if ($LASTEXITCODE -ne 0) { throw 'Studio source archive failed' }
$metadata = cargo metadata --locked --format-version 1 --manifest-path (Join-Path $Source 'Cargo.toml') | ConvertFrom-Json -AsHashtable
$notices = Join-Path $target 'CargoLicenses'
New-Item -ItemType Directory -Path $notices | Out-Null
$dependencyList = foreach ($package in $metadata.packages) {
    $folder = Split-Path $package.manifest_path
    $matches = Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)' }
    foreach ($file in $matches) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $notices ($package.name + '-' + $package.version + '-' + $file.Name)) }
    [ordered]@{ Name=$package.name; Version=$package.version; License=$package.license; Source=$package.source; Repository=$package.repository }
}
$dependencyList | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'dependencies.json') -Encoding utf8
# Supply pinned crate sources for notices, MPL-covered files and LGPL relinking.
$cargoRoot = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$sources = Join-Path $target 'CargoSources'
New-Item -ItemType Directory -Path $sources | Out-Null
foreach ($package in $metadata.packages | Where-Object { $_.source -like 'registry+*' }) {
    $archiveName = $package.name + '-' + $package.version + '.crate'
    $archives = @(Get-ChildItem (Join-Path $cargoRoot 'registry/cache') -Filter $archiveName -Recurse -File)
    if ($archives.Count -ne 1) { throw "Missing pinned crate source: $archiveName" }
    Copy-Item -LiteralPath $archives[0].FullName -Destination $sources
}
$lame = $metadata.packages | Where-Object { $_.name -eq 'mp3lame-sys' }
Copy-Item -LiteralPath (Join-Path (Split-Path $lame.manifest_path) 'lame-3.100/COPYING') -Destination (Join-Path $target 'LICENSE-LAME.txt')
# The native release includes Microsoft runtime DLLs and CUDA backends, with separate terms.
$nativeNotices = Join-Path $Workspace 'Runtime/Backends/yue2.cpp/11c1ecb084329200e22fcb286e252b847442ea5c/win-cuda128-x64'
foreach ($name in @('LICENSE-MSVC.txt','LICENSE-ggml.txt','LICENSE-yyjson.txt')) {
    Copy-Item -LiteralPath (Join-Path $nativeNotices $name) -Destination $target
}
$engineLicense = Invoke-WebRequest 'https://raw.githubusercontent.com/timoncool/yue2.cpp/1141479c725b803a3649c900fd3e6fcb27f6f595/LICENSE'
[IO.File]::WriteAllText((Join-Path $target 'LICENSE-yue2.txt'), $engineLicense.Content, [Text.UTF8Encoding]::new($false))
$files = Get-ChildItem -LiteralPath $target -File -Recurse | Where-Object { $_.FullName -ne (Join-Path $target 'manifest.json') } | ForEach-Object { [ordered]@{
    Name=[IO.Path]::GetRelativePath($target,$_.FullName).Replace('\','/'); Bytes=$_.Length;
    Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }
[ordered]@{ Revision=$revision; SourceRevision=$commit; EngineRevision='1141479c725b803a3649c900fd3e6fcb27f6f595';
    ReleaseArchiveSHA256='3aa7859346605a63f859edf56db8f6d1047588edd9d0c40fe39cb746f0bdfb71';
    AdapterPatchSHA256=(Get-FileHash -LiteralPath $patch).Hash.ToLowerInvariant(); Files=@($files) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
Write-Output $target
