param(
    [switch]$SkipPublish,
    [switch]$PublicBeta
)

$ErrorActionPreference = 'Stop'
if ($PublicBeta -and $SkipPublish) { throw 'Public beta requires a fresh dotnet publish; SkipPublish is only for internal builds.' }

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Find-InnoCompiler {
    $command = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 5\ISCC.exe",
        "$env:ProgramFiles(x86)\Inno Setup 5\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 5\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Escape-InnoDefineValue {
    param([string]$Value)
    return $Value
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'Исходники\AIHub\AIHub.csproj'
$versionPath = Join-Path $repoRoot 'VERSION'
$publishDir = Join-Path $repoRoot 'Runtime\Publish\AIHub-win-x64'
$installerDir = Join-Path $repoRoot 'Тесты\Установщики'
$innoScriptPath = Join-Path $repoRoot 'Инструменты\Installer\LOPATA.iss'
$iconPath = Join-Path $repoRoot 'Исходники\AIHub\Assets\AppIcon.ico'
$backendDir = Join-Path $repoRoot 'Runtime\Backends\llama.cpp\b9442\win-cuda-12.4-x64'
$chatLlmBackendDir = Join-Path $repoRoot 'Runtime\Backends\chatllm.cpp\v24\win-x64'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Не найден проект: $projectPath"
}

if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "Не найден файл версии: $versionPath"
}

if (-not (Test-Path -LiteralPath $innoScriptPath)) {
    throw "Не найден Inno Setup сценарий: $innoScriptPath"
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Не найдена иконка установщика: $iconPath"
}

if (-not (Test-Path -LiteralPath (Join-Path $backendDir 'llama-server.exe'))) {
    throw "Не найден llama.cpp backend для установщика: $backendDir"
}

if (-not (Test-Path -LiteralPath (Join-Path $chatLlmBackendDir 'server.exe'))) {
    throw "Не найден chatllm.cpp backend для установщика: $chatLlmBackendDir"
}

if (-not (Test-Path -LiteralPath (Join-Path $chatLlmBackendDir 'imagemagick\magick.exe'))) {
    throw "Не найден приватный ImageMagick для chatllm.cpp: $chatLlmBackendDir"
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($PublicBeta) {
    if ($version -notmatch '^\d+\.\d+\.\d+-dev$') { throw 'PublicBeta requires an internal X.Y.Z-dev version.' }
    $version = $version -replace '-dev$', '-beta'
    $publishDir = Join-Path $repoRoot "Runtime\Publish\LOPATA-$version-win-x64"
}
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Файл VERSION пустой."
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Host "LOPATA: build test installer."
Write-Host "Version: $version"
Write-Host "Output: $installerDir"

if (-not $SkipPublish) {
    Write-Step "Publishing LOPATA"
    dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDir `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        "-p:AIHubVersion=$version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с ошибкой: $LASTEXITCODE"
    }
}
else {
    Write-Step "Publish skipped"
}

$exePath = Join-Path $publishDir 'AIHub.exe'
$payloadVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir 'AIHub.dll')).ProductVersion
if ($payloadVersion -ne $version) { throw "Payload version mismatch: $payloadVersion / $version" }
$licenseCatalog = Join-Path $publishDir 'Licenses\catalog.json'
foreach ($required in @('catalog.json', 'installer.txt', 'installer-receipt.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Licenses\$required"))) {
        throw "В publish отсутствует лицензионный комплект: $required. Выполните актуальный publish."
    }
}
$licenseEntries = Get-Content -LiteralPath $licenseCatalog -Raw | ConvertFrom-Json
foreach ($entry in $licenseEntries) {
    foreach ($text in $entry.Texts) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Licenses\$text"))) {
            throw "В publish отсутствует текст лицензии: $text"
        }
    }
}
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "После publish не найден AIHub.exe: $exePath"
}

$iscc = Find-InnoCompiler
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup Compiler не найден." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Чтобы сборка установщика заработала, установите Inno Setup 6."
    Write-Host "Самый простой вариант через winget:"
    Write-Host ""
    Write-Host "  winget install --id JRSoftware.InnoSetup -e"
    Write-Host ""
    Write-Host "После установки снова запустите Собрать_установщик_LOPATA.cmd."
    exit 2
}

Write-Step "Building installer with Inno Setup"
Write-Host "ISCC: $iscc"

$arguments = @(
    "/DAppVersion=$version",
    "/DNumericVersion=$($version -replace '-.*$', '')",
    "/DPublishDir=$(Escape-InnoDefineValue $publishDir)",
    "/DBackendDir=$(Escape-InnoDefineValue $backendDir)",
    "/DChatLlmBackendDir=$(Escape-InnoDefineValue $chatLlmBackendDir)",
    "/DOutputDir=$(Escape-InnoDefineValue $installerDir)",
    "/DSetupIconFile=$(Escape-InnoDefineValue $iconPath)",
    $innoScriptPath
)

& $iscc @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup завершился с ошибкой: $LASTEXITCODE"
}

$setupPath = Join-Path $installerDir "LOPATA_Setup_$version.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Сборка завершилась, но ожидаемый установщик не найден: $setupPath"
}

Write-Host ""
Write-Host "Готово: $setupPath" -ForegroundColor Green

# Local receipt only. This script never uploads or publishes a release.
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
$sourceChanges = & git -C $repoRoot status --porcelain --untracked-files=all -- 'Исходники' 'Инструменты' 'VERSION' 'Каталоги' 'LICENSE' 'NOTICE.md' 'THIRD_PARTY_NOTICES.md'
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source state.' }
$receipt = [ordered]@{
    schemaVersion = 1
    version = $version
    fileName = [IO.Path]::GetFileName($setupPath)
    size = (Get-Item -LiteralPath $setupPath).Length
    sha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceCommit = $sourceCommit
    sourceDirty = [bool]$sourceChanges
    payloadVersion = $payloadVersion
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath "$setupPath.build.json" -Encoding utf8
Write-Host "Local build receipt: $setupPath.build.json"
