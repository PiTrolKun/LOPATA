[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ForkDirectory,
    [Parameter(Mandatory)] [string] $HostOutputDirectory
)

$ErrorActionPreference = 'Stop'
$fork = (Resolve-Path -LiteralPath $ForkDirectory).Path
$hostOutput = (Resolve-Path -LiteralPath $HostOutputDirectory).Path
$source = Join-Path $fork 'src/NabyScape/NabyScape.csproj'
$package = Join-Path $hostOutput 'NabyScape'
if (Test-Path -LiteralPath $package) {
    throw "Choose a fresh host output directory. A package already exists in $package"
}

$commit = git -C $fork rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'The fork commit could not be identified.' }
$changes = git -C $fork status --porcelain -- src/NabyScape licenses LICENSE docs/presets
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Commit the fork sources and license files before packaging.' }

$staging = Join-Path $hostOutput ('.island-package-' + [Guid]::NewGuid().ToString('N'))
$lopataRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$publishOptions = @("-p:LopataRepositoryRoot=$lopataRoot", '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false')
dotnet publish $source -c Release -r win-x64 --self-contained true --nologo @publishOptions -o $staging
if ($LASTEXITCODE -ne 0) { throw "Island publish failed. Staging directory: $staging" }

$exe = Get-Item -LiteralPath (Join-Path $staging 'LopataIsland.exe')
if (Get-ChildItem -LiteralPath $staging -Force | Where-Object Name -ne $exe.Name) {
    throw "The island runtime did not publish as a single binary. Staging directory: $staging"
}
Copy-Item -LiteralPath (Join-Path $fork 'LICENSE') -Destination $staging
Copy-Item -LiteralPath (Join-Path $fork 'licenses') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $lopataRoot 'LICENSE') -Destination (Join-Path $staging 'licenses/LOPATA-GPL-3.0.txt')
$collectionSources = Join-Path $lopataRoot 'Исходники/LOPATA.Collection'
$collectionChecksums = Get-ChildItem -LiteralPath $collectionSources -File |
    Where-Object { $_.Extension -in '.cs','.csproj' } | Sort-Object Name |
    ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }
$collectionBytes = [Text.Encoding]::UTF8.GetBytes(($collectionChecksums -join "\n"))
$collectionDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($collectionBytes))
$updateChecksums = Get-ChildItem -LiteralPath (Join-Path $lopataRoot 'Исходники/LOPATA.Updates') -File |
    Where-Object { $_.Extension -in '.cs','.csproj','.json' } | Sort-Object Name |
    ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }
$updateBytes = [Text.Encoding]::UTF8.GetBytes(($updateChecksums -join [char]10))
[ordered] @{
    Commit = $commit
    CollectionSourceSha256 = $collectionDigest
    UpdatesSourceSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($updateBytes))
    Length = $exe.Length
    Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'island-package.json') -Encoding utf8NoBOM
Move-Item -LiteralPath $staging -Destination $package
Get-Item -LiteralPath (Join-Path $package $exe.Name)
