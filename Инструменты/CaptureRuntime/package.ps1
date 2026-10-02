param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+-beta$')][string]$ReleaseVersion = '0.3.20-beta'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new package directory; existing artifacts are never overwritten.' }
New-Item -ItemType Directory -Path $output | Out-Null
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json
$sourceStage = Join-Path $output 'source-inputs'
$bundle = Join-Path $output 'bundle'
New-Item -ItemType Directory -Path $sourceStage,$bundle,(Join-Path $sourceStage 'sources'),(Join-Path $bundle 'licenses') | Out-Null
foreach ($source in $sources) {
    $archive = Join-Path $build "sources/$($source.Archive)"
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $source.SHA256) { throw "Source checksum mismatch: $($source.Name)." }
    Copy-Item -LiteralPath $archive -Destination (Join-Path $sourceStage "sources/$($source.Archive)")
}
foreach ($name in @('README.md','build.ps1','build-minimal.sh','package.ps1','sources.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $sourceStage
}
foreach ($name in @('toolchain-packages.txt','compiler.txt')) {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination $sourceStage
}
foreach ($name in @('config.h','config_components.h','ffbuild/config.mak')) {
    Copy-Item -LiteralPath (Join-Path $build "work/ffmpeg-build/$name") -Destination (Join-Path $sourceStage ([IO.Path]::GetFileName($name)))
}
[IO.File]::WriteAllText((Join-Path $sourceStage 'changes.diff'),'',[Text.UTF8Encoding]::new($false))
$licenseRoot = Join-Path $root 'Исходники/AIHub/Licenses/texts'
$licenses = @(Get-ChildItem -LiteralPath $licenseRoot -File | Where-Object { $_.Name -like 'Capture-*.txt' -or $_.Name -in @('FFmpeg-8.1-LGPL.txt','FFmpeg-8.1-GPL2.txt') })
foreach ($license in $licenses) { Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $bundle 'licenses') }
Copy-Item -LiteralPath (Join-Path $bundle 'licenses') -Destination (Join-Path $sourceStage 'licenses') -Recurse
$sourceZip = Join-Path $output "LOPATA-Capture-Sources-$ReleaseVersion.zip"
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath $sourceZip -CompressionLevel NoCompression
$files = @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $build "prefix/bin/$file") -Destination $bundle }
Copy-Item -LiteralPath (Join-Path $licenseRoot 'FFmpeg-8.1-LGPL.txt') -Destination (Join-Path $bundle 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $licenseRoot 'FFmpeg-8.1-NOTICE.md') -Destination (Join-Path $bundle 'NOTICE.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Destination $bundle
$version = (& (Join-Path $bundle 'ffmpeg.exe') -version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'lopata-minimal-1' -or $version -match '--enable-(gpl|nonfree|version3)') { throw 'Unexpected encoder configuration.' }
[IO.File]::WriteAllText((Join-Path $bundle 'configuration.txt'),$version+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
$record = [ordered]@{
    Version = '8.1.3-lopata-minimal-1'; Source = 'https://github.com/FFmpeg/FFmpeg/tree/'+$sources[0].Commit
    SourceArchive = "https://github.com/PiTrolKun/LOPATA/releases/download/v$ReleaseVersion/"+[IO.Path]::GetFileName($sourceZip)
    SourceArchiveSha256 = (Get-FileHash -LiteralPath $sourceZip).Hash
    Builder = 'Инструменты/CaptureRuntime/build-minimal.sh'; Sources = $sources
    Files = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Name = [IO.Path]::GetRelativePath($bundle,$_.FullName) -replace '\\','/'; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
}
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle 'provenance.json') -Encoding utf8
$runtimeZip = Join-Path $output "LOPATA-Capture-Runtime-$ReleaseVersion.zip"
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $runtimeZip -CompressionLevel Optimal
[ordered]@{ Version=$record.Version; Url="https://github.com/PiTrolKun/LOPATA/releases/download/v$ReleaseVersion/"+[IO.Path]::GetFileName($runtimeZip); SHA256=(Get-FileHash -LiteralPath $runtimeZip).Hash; SourceUrl=$record.SourceArchive; SourceSHA256=$record.SourceArchiveSha256 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'runtime.json') -Encoding utf8
Write-Host "Prepared exact source and runtime archives: $output"
