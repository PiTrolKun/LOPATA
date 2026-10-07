param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$CMake,
    [Parameter(Mandatory)][string]$Ninja,
    [Parameter(Mandatory)][string]$VsEnvironment,
    [string]$VulkanSdk
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne 'd4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c') { throw 'Unexpected llama.cpp revision.' }
if (& git -C $source status --porcelain --untracked-files=no) { throw 'Pinned source has local edits.' }
$build = [IO.Path]::GetFullPath($BuildDirectory)
foreach ($path in @($source, $build, $CMake, $Ninja, $VsEnvironment, $VulkanSdk)) {
    if ($path -match '["\r\n]') { throw 'Invalid build path.' }
}
New-Item -ItemType Directory -Path $build -Force | Out-Null
$gpu = if ($VulkanSdk) { 'ON' } else { 'OFF' }
$arguments = @('-S', $source, '-B', $build, '-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$Ninja",
    '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL',
    '-DLLAMA_BUILD_NUMBER=9442', '-DLLAMA_BUILD_COMMIT=d4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c',
    '-DBUILD_SHARED_LIBS=ON', '-DGGML_NATIVE=OFF', '-DGGML_CPU_ALL_VARIANTS=ON',
    '-DGGML_OPENMP=OFF', '-DGGML_CUDA=OFF', "-DGGML_VULKAN=$gpu", '-DGGML_BACKEND_DL=ON',
    '-DGGML_CCACHE=OFF', '-DLLAMA_OPENSSL=OFF', '-DLLAMA_BUILD_TESTS=OFF', '-DLLAMA_BUILD_UI=OFF', '-DLLAMA_USE_PREBUILT_UI=OFF')
$commands = @('@echo off', 'chcp 65001 >nul', "call `"$VsEnvironment`" >nul", 'if errorlevel 1 exit /b 1')
if ($VulkanSdk) { $commands += "set VULKAN_SDK=$VulkanSdk" }
$quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$commands += @("`"$CMake`" $quoted", 'if errorlevel 1 exit /b 1',
    "`"$CMake`" --build `"$build`" --target llama-server llama-cli --parallel 6", 'if errorlevel 1 exit /b 1')
$manifest = Join-Path $PSScriptRoot 'MusicNative/windows-utf8.manifest'
foreach ($name in @('llama-cli.exe', 'llama-server.exe')) {
    $commands += @("mt.exe -nologo -manifest `"$manifest`" -outputresource:`"$build/bin/$name`";1", 'if errorlevel 1 exit /b 1')
}
$batch = Join-Path $build 'build-lopata.cmd'
[IO.File]::WriteAllText($batch, ($commands -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
& $batch
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Write-Host "Pinned CPU/Vulkan build ready for verification at $build. OpenMP and CUDA are disabled."
