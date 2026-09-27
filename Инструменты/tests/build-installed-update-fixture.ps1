param([Parameter(Mandatory)][string]$StandDataRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stand = [IO.Path]::GetFullPath($StandDataRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Тесты/FileUpdates')) + [IO.Path]::DirectorySeparatorChar
if (!$stand.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stand root.' }
$receipt = Get-Content -LiteralPath (Join-Path $stand 'Installers/LOPATA_Setup_0.2.42-beta.exe.build.json') -Raw | ConvertFrom-Json
if (!$receipt.standBuild) { throw 'An isolated baseline is required.' }
$payload = Join-Path $repoRoot ('Runtime/Publish/UpdateFixture-' + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repoRoot 'Исходники/AIHub/AIHub.csproj') --configuration Release --runtime win-x64 --self-contained true `
    --output $payload -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:AIHubVersion=0.2.43-beta -p:DefineConstants=UPDATE_STAND
if ($LASTEXITCODE -ne 0) { throw 'Fixture application publish failed.' }
$hostOutput = Join-Path $repoRoot ('Runtime/Publish/UpdateFixtureHost-' + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repoRoot 'Исходники/LOPATA.Updater/LOPATA.Updater.csproj') --configuration Release --runtime win-x64 --self-contained true `
    --output $hostOutput -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:DefineConstants=UPDATE_STAND
if ($LASTEXITCODE -ne 0) { throw 'Fixture updater publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $payload 'Updater') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $hostOutput 'LOPATA.Updater.exe') -Destination (Join-Path $payload 'Updater')
'Stand only. Never publish.' | Set-Content -LiteralPath (Join-Path $payload 'lopata-stand-build.marker') -Encoding utf8
$notes = Join-Path $stand 'fixture-notes.md'
'Isolated installed update test. This is not a public release.' | Set-Content -LiteralPath $notes -Encoding utf8
$output = Join-Path $stand 'UpgradePackages'
& (Join-Path $repoRoot 'Инструменты/build-file-update.ps1') -Version '0.2.43-beta' -PublishDir $payload `
    -BackendDir (Join-Path $repoRoot 'Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64') `
    -ChatLlmBackendDir (Join-Path $repoRoot 'Runtime/Backends/chatllm.cpp/v24/win-x64') -NotesPath $notes `
    -OutputDirectory $output -PreviousManifestPath $receipt.fileManifest `
    -HistoryPath (Join-Path $receipt.packageDirectory 'build-inputs/history.json') -StandBuild
Write-Host "Installed upgrade fixture ready: $output"
