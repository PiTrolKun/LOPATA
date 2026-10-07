param(
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$BootstrapCache,
    [Parameter(Mandatory)][string]$MsvcDirectory
)
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath($WorkDirectory)
$stage=Join-Path $work 'payload-cpu'
if(Test-Path -LiteralPath $stage){throw 'Use a fresh output directory; an existing runtime is never modified.'}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$lockPath=Join-Path $PSScriptRoot '../Исходники/AIHub/Tools/python-hardware-cpu-lock.json'
$lock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
foreach($item in @(
    @{File='python.zip'; Size=11133606; Hash='4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'},
    @{File='pip.whl'; Size=1778622; Hash='9655943313a94722b7774661c21049070f6bbb0a1516bf02f7c8d5d9201514cd'}
)){
    $source=Join-Path $BootstrapCache $item.File
    if((Get-Item -LiteralPath $source).Length -ne $item.Size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Hash){throw "Invalid bootstrap: $($item.File)"}
}
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $BootstrapCache 'python.zip'), $stage)
Copy-Item -LiteralPath (Join-Path $BootstrapCache 'pip.whl') -Destination (Join-Path $stage 'pip.whl')
$site=Join-Path $stage 'Lib/site-packages'
New-Item -ItemType Directory -Force -Path $site | Out-Null
[IO.File]::WriteAllText((Join-Path $stage 'python312._pth'), "python312.zip`n.`nLib/site-packages`npip.whl`nimport site`n")
$wheels=@()
foreach($wheel in $lock.wheels){
    $path=Join-Path $InputDirectory $wheel.file
    if((Get-Item -LiteralPath $path).Length -ne $wheel.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wheel.sha256){throw "Invalid wheel: $($wheel.file)"}
    $wheels+=$path
}
$python=Join-Path $stage 'python.exe'
& $python -B -m pip --isolated install --disable-pip-version-check --no-compile --no-deps --no-index --report (Join-Path $work 'installation.json') @wheels
if($LASTEXITCODE -ne 0){throw 'CPU wheel installation failed.'}
foreach($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
    $source=Join-Path $MsvcDirectory $name
    if((Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid' -or (Get-Item -LiteralPath $source).VersionInfo.FileVersion -ne '14.44.35211.0'){throw "Invalid MSVC redistributable: $name"}
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name) -Force
}
$notices=Join-Path $stage 'Notices'; New-Item -ItemType Directory -Force -Path $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Исходники/AIHub/Licenses/texts/music-MSVC-RUNTIME.txt') -Destination (Join-Path $notices 'MSVC-RUNTIME.txt')
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $notices 'wheel-lock.json')
& $python -B -c "import torch, transformers, PIL, json; assert torch.__version__ == '2.10.0+cpu'; assert torch.version.cuda is None; assert transformers.__version__ == '5.3.0'; assert (torch.ones(2,2) @ torch.ones(2,2)).sum().item()==8; print(json.dumps({'torch':torch.__version__,'transformers':transformers.__version__,'cuda':torch.version.cuda,'ready':True}))"
if($LASTEXITCODE -ne 0){throw 'CPU runtime import/operation verification failed.'}
Write-Output $stage
