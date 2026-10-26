$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $root 'Runtime/VideoEditor/Build-8.1'
$bundle=Join-Path $root 'Runtime/VideoEditor/ffmpeg-8.1.3-editor-1'
if(Test-Path -LiteralPath $bundle){throw 'Use a fresh runtime revision; do not replace an installed component in place.'}
New-Item -ItemType Directory -Path $bundle,(Join-Path $bundle 'licenses') | Out-Null
foreach($name in @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')){
 Copy-Item -LiteralPath (Join-Path $build "prefix/bin/$name") -Destination $bundle
}
$licenseRoot=Join-Path $root 'Исходники/AIHub/Licenses/texts'
Get-ChildItem -LiteralPath $licenseRoot -File | Where-Object {$_.Name -like 'Capture-*' -or $_.Name -in @('FFmpeg-8.1-LGPL.txt','FFmpeg-8.1-GPL2.txt','FFmpeg-8.1-NOTICE.md')} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $bundle 'licenses') }
$zlib=Join-Path $root 'Runtime/BuildTools/Capture/msys64/ucrt64/share/licenses/zlib/LICENSE'
if(!(Test-Path -LiteralPath $zlib)){throw 'Missing zlib licence'}
Copy-Item -LiteralPath $zlib -Destination (Join-Path $bundle 'licenses/zlib-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build.sh') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $root 'Инструменты/CaptureRuntime/sources.json') -Destination (Join-Path $bundle 'sources.json')
$version=(& (Join-Path $bundle 'ffmpeg.exe') -version | Out-String)
if($LASTEXITCODE -ne 0 -or $version -notmatch 'lopata-video-editor-1' -or $version -match '--enable-(gpl|nonfree|version3)'){throw 'Unexpected editor runtime'}
[IO.File]::WriteAllText((Join-Path $bundle 'configuration.txt'),$version,[Text.UTF8Encoding]::new($false))
$manifest=@(Get-ChildItem -LiteralPath $bundle -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{Path=[IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'Исходники/AIHub/Tools/video-editor-runtime-files.json') -Encoding utf8
Write-Output $bundle
