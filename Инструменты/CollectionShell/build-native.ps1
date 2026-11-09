param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with C++ is required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'The installed C++ toolchain was not found.' }
$msbuild = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
$project = Join-Path $root 'Исходники/LOPATA.CollectionShell/LOPATA.CollectionShell.vcxproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
& $msbuild $project /nologo /verbosity:minimal "/p:Configuration=$Configuration" "/p:Platform=$Architecture" "/p:OutDir=$output/"
if ($LASTEXITCODE -ne 0) { throw 'Collection Shell native build failed.' }
$dll = Join-Path $output 'LOPATA.CollectionShell.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Native build did not produce the DLL.' }
$dll
