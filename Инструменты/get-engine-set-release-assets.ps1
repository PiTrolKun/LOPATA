param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)]$Receipt,
    [Parameter(Mandatory)]$TransitionManifest
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskUsesSets = @($TransitionManifest.files | Where-Object { $_.root -eq 'app' -and $_.path -match '^EngineSets/[0-9]{3}/' }).Count -gt 0
if (!$taskUsesSets) { return [pscustomobject]@{ Assets = @(); Reused = @() } }
$taskFreshPath = Join-Path $PackageDirectory 'lopata-fresh-files.json'
if (!$Receipt.freshManifestSha256 -or !(Test-Path -LiteralPath $taskFreshPath) -or
    (Get-FileHash -LiteralPath $taskFreshPath).Hash.ToLowerInvariant() -cne $Receipt.freshManifestSha256) {
    throw 'Engine set publication requires the unchanged separate fresh manifest.'
}
$taskFresh = & (Join-Path $PSScriptRoot 'get-update-bundle.ps1') -ManifestPath $taskFreshPath `
    -Version $Receipt.version -SourceCommit $Receipt.sourceCommit
$taskAssets = @($taskFresh.Assets)
$taskReused = @($taskFresh.Reused)
$taskVerifier = Join-Path $taskRoot 'Исходники/LOPATA.UpdateTool/bin/Release/net10.0/LOPATA.UpdateTool.dll'
$taskRows = @($Receipt.engineSetArtifacts)
if (!$taskRows.Count -or @($taskRows | Where-Object id -eq '001').Count -ne 1) {
    throw 'Engine set publication requires signed optional artifacts, including the legacy set.'
}
foreach ($taskRow in $taskRows) {
    if ($taskRow.id -notmatch '^[0-9]{3}$' -or $taskRow.manifestName -cne "engine-set-$($taskRow.id).signed.json") {
        throw 'Invalid optional engine set artifact name.'
    }
    $taskPath = Join-Path $PackageDirectory $taskRow.manifestName
    if ((Get-FileHash -LiteralPath $taskPath).Hash.ToLowerInvariant() -cne $taskRow.manifestSha256) {
        throw 'The optional engine set artifact has changed since packaging.'
    }
    $taskConfig = Join-Path $PackageDirectory "verify-engine-release-$($taskRow.id).json"
    @{ publicKeysPath = Join-Path $taskRoot 'Исходники/LOPATA.Updates/release-keys.json'; targetManifestPath = $taskPath } |
        ConvertTo-Json | Set-Content -LiteralPath $taskConfig -Encoding utf8
    $taskVerified = & dotnet $taskVerifier verify $taskConfig
    if ($LASTEXITCODE -ne 0) { throw 'The optional engine set signature is invalid.' }
    $taskManifest = $taskVerified | Out-String | ConvertFrom-Json
    $taskAssets += $taskPath
    foreach ($taskPackage in $taskManifest.packages) {
        if ($taskPackage.url -cne "https://github.com/PiTrolKun/LOPATA/releases/download/v$($Receipt.version)/$($taskPackage.id)") {
            $taskReused += $taskPackage; continue
        }
        $taskPackagePath = Join-Path $PackageDirectory $taskPackage.id
        $taskFile = Get-Item -LiteralPath $taskPackagePath
        if ($taskFile.Length -ne $taskPackage.size -or
            (Get-FileHash -LiteralPath $taskPackagePath).Hash.ToLowerInvariant() -cne $taskPackage.sha256) {
            throw 'The optional engine set package differs from its signed manifest.'
        }
        $taskAssets += $taskPackagePath
    }
}
$taskPairConfig = Join-Path $PackageDirectory 'verify-engine-transition.json'
@{ publicKeysPath = Join-Path $taskRoot 'Исходники/LOPATA.Updates/release-keys.json';
    targetManifestPath = Join-Path $PackageDirectory 'lopata-files.json'; freshManifestPath = $taskFreshPath;
    legacyEngineSetManifestPath = Join-Path $PackageDirectory 'engine-set-001.signed.json' } |
    ConvertTo-Json | Set-Content -LiteralPath $taskPairConfig -Encoding utf8
& dotnet $taskVerifier verify-engine-transition $taskPairConfig | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Legacy-safe update and fresh installation payloads do not match.' }
return [pscustomobject]@{ Assets = @($taskAssets | Sort-Object -Unique); Reused = @($taskReused | Sort-Object url -Unique) }
