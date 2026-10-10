param([string]$OutputDirectory = 'Тесты/Установщики/mini', [string]$StandDataRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$native = Join-Path $out 'native'
$arguments = @('publish', (Join-Path $root 'Исходники/LOPATA.Bootstrap/LOPATA.Bootstrap.csproj'), '-c', 'Release', '-o', $native)
if ($StandDataRoot) { $arguments += '-p:DefineConstants=UPDATE_STAND' }
& dotnet @arguments
if ($LASTEXITCODE) { throw 'Native bootstrap build failed.' }
$iscc = @("${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe", "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe", "$env:ProgramFiles/Inno Setup 6/ISCC.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$iscc) { throw 'Inno Setup compiler was not found.' }
$defines = @("/DBootstrapDir=$native", "/DOutputDir=$out", "/DSetupIconFile=$(Join-Path $root 'Исходники/AIHub/Assets/AppIcon.ico')")
if ($StandDataRoot) { $defines += "/DStandDataRoot=$([IO.Path]::GetFullPath($StandDataRoot))" }
& $iscc @defines (Join-Path $PSScriptRoot 'Installer/LOPATA.Mini.iss')
if ($LASTEXITCODE) { throw 'Mini installer compilation failed.' }
$exe = Get-Item -LiteralPath (Join-Path $out 'LOPATA_Setup.exe')
if ($exe.Length -gt 10000000) { throw "Mini installer exceeds 10 MB: $($exe.Length) bytes." }
$changes = & git -C $root status --porcelain --untracked-files=all -- 'Исходники' 'Инструменты' 'VERSION'
@{ schemaVersion = 1; bootstrapProtocol = 1; fileName = $exe.Name; size = $exe.Length;
   sha256 = (Get-FileHash -LiteralPath $exe.FullName).Hash.ToLowerInvariant();
   sourceCommit = (& git -C $root rev-parse HEAD).Trim(); sourceDirty = [bool]$changes; standBuild = [bool]$StandDataRoot } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'mini-installer.build.json') -Encoding utf8
Write-Host "Mini installer: $($exe.FullName) ($($exe.Length) bytes)"
