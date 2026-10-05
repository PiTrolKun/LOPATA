param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$CMake,
    [Parameter(Mandatory)][string]$Ninja,
    [Parameter(Mandatory)][string]$VsEnvironment,
    [string]$CudaToolkit,
    [string]$VulkanSdk,
    [switch]$CpuOnly
)
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne '11c1ecb084329200e22fcb286e252b847442ea5c') { throw 'Unexpected YuE2 revision.' }
if ((& git -C (Join-Path $source ggml) rev-parse HEAD) -ne '40e16e4a814f7fe851a0c486fb9e8c722e957830') { throw 'Unexpected GGML revision.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MusicNative/yue-probe.cpp') -Destination (Join-Path $source 'tools/yue-probe.cpp')
$definition=Join-Path $source CMakeLists.txt
if (!(Select-String -LiteralPath $definition -Pattern 'add_executable\(yue-probe' -Quiet)) {
    Copy-Item -LiteralPath $definition -Destination ($definition+'.before-lopata-probe')
    Add-Content -LiteralPath $definition -Value "`nadd_executable(yue-probe tools/yue-probe.cpp)`ntarget_link_libraries(yue-probe PRIVATE yyjson)`nlink_ggml_backends(yue-probe)"
}
$build=[IO.Path]::GetFullPath($BuildDirectory)
New-Item -ItemType Directory -Path $build -Force | Out-Null
$gpu=if($CpuOnly){'OFF'}else{'ON'}
$args=@('-S', $source, '-B', $build, '-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$Ninja", '-DCMAKE_BUILD_TYPE=Release',
    '-DBUILD_SHARED_LIBS=ON','-DGGML_NATIVE=OFF','-DGGML_OPENMP=OFF',"-DGGML_CUDA=$gpu", "-DGGML_VULKAN=$gpu",
    '-DGGML_BACKEND_DL=ON','-DGGML_CCACHE=OFF','-DCMAKE_EXE_LINKER_FLAGS=')
if (!$CpuOnly) {
    if (!$CudaToolkit -or !$VulkanSdk) { throw 'Explicit CUDA and Vulkan SDK paths required.' }
    $args+=@("-DCUDAToolkit_ROOT=$CudaToolkit", "-DCMAKE_CUDA_COMPILER=$CudaToolkit/bin/nvcc.exe", '-DCMAKE_CUDA_ARCHITECTURES=86-real;89-real;120-real')
}
# Run a UTF-8 batch under the MSVC environment; no system installation or registry writes.
$commands=@('@echo off','chcp 65001 >nul',"call `"$VsEnvironment`" >nul",'if errorlevel 1 exit /b 1')
if (!$CpuOnly) { $commands+="set VULKAN_SDK=$VulkanSdk" }
$quoted=($args | ForEach-Object { '"'+$_+'"' }) -join ' '
$commands+="`"$CMake`" $quoted"
$commands+=@('if errorlevel 1 exit /b 1',"`"$CMake`" --build `"$build`" --target yue-plan yue-synth yue-probe --parallel 6",'if errorlevel 1 exit /b 1')
$manifest=(Join-Path $PSScriptRoot 'MusicNative/windows-utf8.manifest')
foreach($name in @('yue-plan.exe','yue-synth.exe','yue-probe.exe')) {
    $commands+="mt.exe -nologo -manifest `"$manifest`" -outputresource:`"$build/$name`";1"
    $commands+='if errorlevel 1 exit /b 1'
}
$batch=Join-Path $build 'build-lopata.cmd'
[IO.File]::WriteAllText($batch,($commands -join "`r`n")+"`r`n",[Text.UTF8Encoding]::new($false))
& $batch
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Write-Host "Built pinned YuE2 runtime at $build; copy only verified payload and original notices, not SDK files."
