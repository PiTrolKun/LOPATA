param([string]$BuildDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$BuildDirectory) { $BuildDirectory = Join-Path $projectRoot 'Runtime/MusicAudio/FFmpeg-8.1/build' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'Runtime/MusicAudio/FFmpeg-8.1/bundle' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Package into a new directory; do not overwrite an existing bundle.' }
$sourceRoot = Join-Path $BuildDirectory 'corresponding-source'
New-Item -ItemType Directory -Force $sourceRoot, $OutputDirectory | Out-Null
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    $archive = Join-Path $BuildDirectory "sources/$($source.Archive)"
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $source.SHA256) { throw "Source hash mismatch: $($source.Name)" }
    Copy-Item -LiteralPath $archive -Destination $sourceRoot
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build-audio.sh'), (Join-Path $PSScriptRoot 'build.ps1'), (Join-Path $PSScriptRoot 'sources.json'), (Join-Path $PSScriptRoot 'ffmetadata-escape.patch'), (Join-Path $PSScriptRoot 'README.md') -Destination $sourceRoot
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'compiler.txt'), (Join-Path $BuildDirectory 'toolchain-packages.txt'), (Join-Path $BuildDirectory 'work/ffmpeg-build/ffbuild/config.log') -Destination $sourceRoot
Compress-Archive -Path "$sourceRoot/*" -DestinationPath (Join-Path $OutputDirectory 'corresponding-source.zip')
$files = @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avformat-62.dll','avutil-60.dll','avfilter-11.dll','swresample-6.dll')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $BuildDirectory "prefix/bin/$name") -Destination $OutputDirectory }
$licenseRoot = Join-Path $OutputDirectory 'licenses'; New-Item -ItemType Directory $licenseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'work/ffmpeg/COPYING.LGPLv2.1') -Destination (Join-Path $licenseRoot 'FFmpeg-LGPL-2.1.txt')
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'work/ffmpeg/COPYING.GPLv2') -Destination (Join-Path $licenseRoot 'GPL-2.0.txt')
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'work/opus/COPYING'), (Join-Path $BuildDirectory 'work/opus/opus_sources.mk') -Destination $licenseRoot
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'work/lame/COPYING') -Destination (Join-Path $licenseRoot 'LAME-LGPL-2.0.txt')
foreach ($name in @('Capture-Opus-IPR.txt','Capture-GCC-Exception.txt','Capture-GCC-GPL3.txt','Capture-MinGW-runtime.txt','Capture-MinGW-LICENSE.txt','Capture-MinGW-COPYING.txt')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "Исходники/AIHub/Licenses/texts/$name") -Destination $licenseRoot
}
$manifest = [ordered]@{ Revision='ffmpeg-8.1-opus-1.5.2-lame-3.100-lopata-audio-1'; Architecture='win-x64';
    Files=@($files | ForEach-Object { @{ Name=$_; Sha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $_)).Hash } });
    Sources=$sources; SourceArchive='corresponding-source.zip'; SourceSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'corresponding-source.zip')).Hash }
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8NoBOM
Write-Output $OutputDirectory
