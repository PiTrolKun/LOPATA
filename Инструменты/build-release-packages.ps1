param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PreviousManifestPath,
    [string]$HistoryPath,
    [switch]$StandBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(-beta)?$') { throw 'An explicit public version is required.' }
$internal = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim() -replace '-dev$', ''
if (($Version -replace '-beta$', '') -ne $internal) { throw 'The release must use the current VERSION number.' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$publish = Join-Path $out ('payload-' + [guid]::NewGuid().ToString('N'))
$hostOutput = Join-Path $out ('host-' + [guid]::NewGuid().ToString('N'))
$constants = if ($StandBuild) { 'UPDATE_STAND' } else { '' }
& dotnet publish (Join-Path $root 'Исходники/AIHub/AIHub.csproj') -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false "-p:AIHubVersion=$Version" "-p:DefineConstants=$constants"
if ($LASTEXITCODE) { throw 'Application publish failed.' }
& dotnet publish (Join-Path $root 'Исходники/LOPATA.Updater/LOPATA.Updater.csproj') -c Release -r win-x64 --self-contained true -o $hostOutput `
    -p:PublishSingleFile=true -p:PublishReadyToRun=false "-p:DefineConstants=$constants"
if ($LASTEXITCODE) { throw 'Update host publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $publish 'Updater') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $hostOutput 'LOPATA.Updater.exe') -Destination (Join-Path $publish 'Updater')
if ($StandBuild) { 'Isolated stand. Never publish.' | Set-Content -LiteralPath (Join-Path $publish 'lopata-stand-build.marker') -Encoding utf8 }
$catalog = Get-Content -LiteralPath (Join-Path $publish 'Licenses/catalog.json') -Raw | ConvertFrom-Json
foreach ($entry in $catalog) {
    foreach ($text in $entry.Texts) {
        if (!(Test-Path -LiteralPath (Join-Path $publish "Licenses/$text"))) { throw "Missing license: $text" }
    }
}
$packages = Join-Path $out 'packages'
& (Join-Path $PSScriptRoot 'build-file-update.ps1') -Version $Version -PublishDir $publish -NotesPath $NotesPath `
    -ChatLlmBackendDir (Join-Path $root 'Runtime/Backends/chatllm.cpp/v24/win-x64') -OutputDirectory $packages `
    -PreviousManifestPath $PreviousManifestPath -HistoryPath $HistoryPath -StandBuild:$StandBuild
if ($LASTEXITCODE) { throw 'Signed package build failed.' }
@{ version = $Version; publishDirectory = $publish; packageDirectory = $packages; fullInstallerBuilt = $false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'release-packages.build.json') -Encoding utf8
Write-Host "Release packages ready: $packages. No offline installer was built or published."
