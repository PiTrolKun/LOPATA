$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$build=Join-Path $root 'Runtime/VideoUpscale/Build-8.1-upscale-5'
$bundle=Join-Path $root 'Runtime/VideoUpscale/ffmpeg-8.1.3-upscale-5'
if(Test-Path -LiteralPath $bundle){throw 'Use a new runtime revision rather than replacing an installed bundle.'}
$ffmpeg=Join-Path $build 'prefix/bin/ffmpeg.exe'
$version=(& $ffmpeg -version | Out-String)
if($LASTEXITCODE -ne 0 -or $version -notmatch 'lopata-video-upscale-5' -or $version -match '--enable-(gpl|nonfree|version3)'){throw 'Unexpected video runtime'}
$filters=(& $ffmpeg -hide_banner -filters | Out-String)
if($LASTEXITCODE -ne 0){throw 'Could not inspect upscale runtime filters.'}
foreach($filter in @('scale','pad','unsharp','atadenoise')){
 if($filters -notmatch "(?m)^\s+\S+\s+$filter\s+"){throw "Missing upscale runtime filter: $filter"}
}
$encoders=(& $ffmpeg -hide_banner -encoders | Out-String)
if($LASTEXITCODE -ne 0){throw 'Could not inspect video runtime encoders.'}
foreach($encoder in @('av1_nvenc','libsvtav1','libaom-av1','hevc_nvenc','libkvazaar','h264_nvenc','libopenh264','libvpx-vp9','ffv1')){
 if($encoders -notmatch "(?m)^\s+\S+\s+$encoder\s+"){throw "Missing video runtime encoder: $encoder"}
}
$decoders=(& $ffmpeg -hide_banner -decoders | Out-String)
if($LASTEXITCODE -ne 0 -or $decoders -notmatch '(?m)^\s+\S+\s+libaom-av1\s+'){throw 'Missing software AV1 decoder.'}
$bitstreamFilters=(& $ffmpeg -hide_banner -bsfs | Out-String)
if($LASTEXITCODE -ne 0 -or $bitstreamFilters -notmatch '(?m)^av1_metadata\s*$'){throw 'Missing AV1 metadata bitstream filter.'}
New-Item -ItemType Directory -Path $bundle,(Join-Path $bundle 'licenses') | Out-Null
foreach($name in @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')){
 Copy-Item -LiteralPath (Join-Path $build "prefix/bin/$name") -Destination $bundle
}
Get-ChildItem -LiteralPath (Join-Path $root 'Runtime/VideoEditor/ffmpeg-8.1.3-editor-1/licenses') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $bundle 'licenses') }
$licenses = @{
 'aom/LICENSE' = 'AOM-LICENSE.txt'
 'aom/PATENTS' = 'AOM-PATENTS.txt'
 'svt/LICENSE.md' = 'SVT-AV1-LICENSE.md'
 'svt/LICENSE-BSD2.md' = 'SVT-AV1-LICENSE-BSD2.md'
 'svt/PATENTS.md' = 'SVT-AV1-PATENTS.md'
 'kvazaar/LICENSE' = 'Kvazaar-LICENSE.txt'
 'cpuinfo/LICENSE' = 'cpuinfo-LICENSE.txt'
}
foreach ($entry in $licenses.GetEnumerator()) {
 Copy-Item -LiteralPath (Join-Path $build ("codecs-src/" + $entry.Key)) -Destination (Join-Path $bundle ("licenses/" + $entry.Value))
}
foreach ($recipe in @('build.sh','build-codecs.sh','codec-sources.json')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $recipe) -Destination $bundle }
Copy-Item -LiteralPath (Join-Path $root 'Инструменты/CaptureRuntime/sources.json') -Destination (Join-Path $bundle 'sources.json')
[IO.File]::WriteAllText((Join-Path $bundle 'configuration.txt'),$version,[Text.UTF8Encoding]::new($false))
$manifest=@(Get-ChildItem -LiteralPath $bundle -File -Recurse|Sort-Object FullName|ForEach-Object{[ordered]@{Path=[IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
ConvertTo-Json -InputObject $manifest -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'Исходники/AIHub/Tools/VideoUpscale/ffmpeg-files.json') -Encoding utf8
Write-Output $bundle
