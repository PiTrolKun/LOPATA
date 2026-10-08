param(
    [Parameter(Mandatory)][string]$MsysRoot,
    [string]$BuildDirectory,
    [ValidateRange(1,32)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$BuildDirectory) { $BuildDirectory = Join-Path $projectRoot 'Runtime/MusicAudio/FFmpeg-8.1/build' }
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$bashPath = Join-Path (Resolve-Path -LiteralPath $MsysRoot).Path 'usr/bin/bash.exe'
if (!(Test-Path -LiteralPath $bashPath)) { throw 'Provide an isolated MSYS2 UCRT64 toolchain.' }
New-Item -ItemType Directory -Force (Join-Path $buildRoot 'sources') | Out-Null
$sources = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($source in $sources) {
    $sourceArchive = Join-Path $buildRoot "sources/$($source.Archive)"
    $existingArchive = Join-Path $projectRoot "Runtime/Capture/Minimal-8.1/sources/$($source.Archive)"
    if (!(Test-Path -LiteralPath $sourceArchive)) {
        if (Test-Path -LiteralPath $existingArchive) { Copy-Item -LiteralPath $existingArchive -Destination $sourceArchive }
        else { Invoke-WebRequest -Uri $source.Url -OutFile $sourceArchive }
    }
    if ((Get-FileHash -LiteralPath $sourceArchive).Hash -ne $source.SHA256) { throw "Source digest mismatch: $($source.Name)" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Destination $buildRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ffmetadata-escape.patch') -Destination $buildRoot
$priorSystem = $env:MSYSTEM; $priorHere = $env:CHERE_INVOKING
try {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    $recipePath = (Join-Path $PSScriptRoot 'build-audio.sh') -replace '\\','/'
    $buildArgument = $buildRoot -replace '\\','/'
    if ($recipePath.Contains("'") -or $buildArgument.Contains("'")) { throw 'Build paths cannot contain apostrophes.' }
    & $bashPath -lc "bash `"`$(cygpath -u '$recipePath')`" `"`$(cygpath -u '$buildArgument')`" $Jobs"
    if ($LASTEXITCODE -ne 0) { throw "Audio build failed; inspect $buildRoot/logs" }
}
finally { $env:MSYSTEM = $priorSystem; $env:CHERE_INVOKING = $priorHere }
