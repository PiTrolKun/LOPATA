$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $root 'Runtime/VideoUpscale/Build-8.1'
$bundle=Join-Path $root 'Runtime/VideoUpscale/ffmpeg-8.1.3-upscale-2'
if(Test-Path -LiteralPath $bundle){throw 'Use a new runtime revision rather than replacing an installed bundle.'}
New-Item -ItemType Directory -Path $bundle,(Join-Path $bundle 'licenses') | Out-Null
foreach($name in @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')){
 Copy-Item -LiteralPath (Join-Path $build "prefix/bin/$name") -Destination $bundle
}
Get-ChildItem -LiteralPath (Join-Path $root 'Runtime/VideoEditor/ffmpeg-8.1.3-editor-1/licenses') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $bundle 'licenses') }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build.sh') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $root 'Инструменты/CaptureRuntime/sources.json') -Destination (Join-Path $bundle 'sources.json')
$version=(& (Join-Path $bundle 'ffmpeg.exe') -version | Out-String)
if($LASTEXITCODE -ne 0 -or $version -notmatch 'lopata-video-upscale-2' -or $version -match '--enable-(gpl|nonfree|version3)'){throw 'Unexpected upscale runtime'}
[IO.File]::WriteAllText((Join-Path $bundle 'configuration.txt'),$version,[Text.UTF8Encoding]::new($false))
$manifest=@(Get-ChildItem -LiteralPath $bundle -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{Path=[IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $root 'Исходники/AIHub/Tools/VideoUpscale/ffmpeg-files.json') -Encoding utf8
Write-Output $bundle
