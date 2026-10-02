param(
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$BuildDirectory,
    [ValidateRange(1,32)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$BuildDirectory) { $BuildDirectory = Join-Path $root 'Runtime/Capture/Minimal-8.1' }
$build = [IO.Path]::GetFullPath($BuildDirectory)
$bash = Join-Path (Resolve-Path -LiteralPath $MsysRoot).Path 'usr/bin/bash.exe'
if (!(Test-Path -LiteralPath $bash)) { throw 'Provide an isolated MSYS2 root containing UCRT64 GCC, make, nasm, perl, cmake and pkgconf.' }
New-Item -ItemType Directory -Force (Join-Path $build 'sources') | Out-Null
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    $archive = Join-Path $build "sources/$($source.Archive)"
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $source.Url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $source.SHA256) { throw "Source checksum mismatch: $($source.Name). Build not started." }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Destination (Join-Path $build 'sources.json') -Force
# Environment changes are confined to the child build and restored for the caller.
$previousSystem = $env:MSYSTEM; $previousHere = $env:CHERE_INVOKING
try {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    $script = (Join-Path $PSScriptRoot 'build-minimal.sh') -replace '\\','/'
    $directory = $build -replace '\\','/'
    if ($script.Contains("'") -or $directory.Contains("'")) { throw 'Build paths cannot contain an apostrophe.' }
    & $bash -lc "bash `"`$(cygpath -u '$script')`" `"`$(cygpath -u '$directory')`" $Jobs"
    if ($LASTEXITCODE -ne 0) { throw "Capture build failed. Inspect $build/logs." }
}
finally { $env:MSYSTEM = $previousSystem; $env:CHERE_INVOKING = $previousHere }
