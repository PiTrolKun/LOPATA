param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root 'Runtime/Capture/FFmpeg-8.1'
$bundle = Join-Path $runtime 'bundle'
$release = 'autobuild-2026-10-01-13-06'
$name = 'ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1'
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$release/$name.zip"
$expected = 'BF545D8FEE9BB6957C1F3DEA0F384BF64EDEAD407D763326DBBD2DE1B04768A4'
$archive = Join-Path $runtime 'pinned-package.zip'
New-Item -ItemType Directory -Force $runtime | Out-Null
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw 'The pinned capture runtime archive checksum does not match. No binaries were copied.' }
$extracted = Join-Path $runtime 'pinned'
if (!(Test-Path -LiteralPath (Join-Path $extracted "$name/bin/ffmpeg.exe"))) { Expand-Archive -LiteralPath $archive -DestinationPath $extracted }
$source = Join-Path $extracted $name
New-Item -ItemType Directory -Force $bundle | Out-Null
$files = @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $source "bin/$file") -Destination (Join-Path $bundle $file) -Force }
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination (Join-Path $bundle 'LICENSE.txt') -Force
$notice = Join-Path $root 'Документы_проекта/Лицензии/Захват_видео_FFmpeg_NOTICE.md'
if (!(Test-Path -LiteralPath $notice)) { throw 'The capture runtime redistribution notice is missing.' }
Copy-Item -LiteralPath $notice -Destination (Join-Path $bundle 'NOTICE.md') -Force
$manifest = [ordered]@{Version='n8.1.3-14-g330caae0c1-20261001';Source=$url;ArchiveSha256=$expected;Builder='https://github.com/BtbN/FFmpeg-Builds/tree/e88e49f624457c455700b058f0a84ca87d499cc2';Files=@($files | ForEach-Object { [ordered]@{Name=$_;Sha256=(Get-FileHash -LiteralPath (Join-Path $bundle $_)).Hash} })}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bundle 'provenance.json') -Encoding utf8
Write-Host "Prepared pinned capture runtime: $bundle"
