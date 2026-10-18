param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$VerifierPath,
    [string]$EngineSetArtifactDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!(Test-Path -LiteralPath (Join-Path $PublishDir 'EngineSets'))) { return @() }
if (!$EngineSetArtifactDirectory) { $EngineSetArtifactDirectory = Join-Path $taskRoot 'Runtime/EngineSetBuild/packages' }
$taskCatalogPath = Join-Path $taskRoot 'Исходники/AIHub/ContentEngineSets/catalog.json'
$taskCatalog = Get-Content -LiteralPath $taskCatalogPath -Raw | ConvertFrom-Json
$taskRows = @()
foreach ($taskSet in $taskCatalog.sets) {
    if ($taskSet.id -notmatch '^[0-9]{3}$') { throw 'Invalid engine set identifier.' }
    $taskName = "engine-set-$($taskSet.id).signed.json"
    $taskSource = Join-Path $EngineSetArtifactDirectory $taskName
    $taskEnvelope = Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json
    foreach ($taskProperty in @('keyId','payload','signature')) {
        if ($taskEnvelope.$taskProperty -cne $taskSet.manifest.$taskProperty) { throw 'Bundled catalog and engine artifact manifest differ.' }
    }
    $taskConfigPath = Join-Path $OutputDirectory "verify-engine-set-$($taskSet.id).json"
    @{ publicKeysPath = Join-Path $taskRoot 'Исходники/LOPATA.Updates/release-keys.json'; targetManifestPath = $taskSource } |
        ConvertTo-Json | Set-Content -LiteralPath $taskConfigPath -Encoding utf8
    $taskVerified = & dotnet $VerifierPath verify $taskConfigPath
    if ($LASTEXITCODE -ne 0) { throw 'Engine set artifact signature verification failed.' }
    $taskManifest = $taskVerified | Out-String | ConvertFrom-Json
    $taskDestination = Join-Path $OutputDirectory $taskName
    Copy-Item -LiteralPath $taskSource -Destination $taskDestination -Force
    foreach ($taskPackage in $taskManifest.packages) {
        $taskExpected = "https://github.com/PiTrolKun/LOPATA/releases/download/v$Version/$($taskPackage.id)"
        if ($taskPackage.url -cne $taskExpected) { continue }
        $taskPackageSource = Join-Path $EngineSetArtifactDirectory $taskPackage.id
        $taskFile = Get-Item -LiteralPath $taskPackageSource
        if ($taskFile.Length -ne $taskPackage.size -or
            (Get-FileHash -LiteralPath $taskPackageSource).Hash.ToLowerInvariant() -cne $taskPackage.sha256) {
            throw 'Engine set package differs from its signed manifest.'
        }
        Copy-Item -LiteralPath $taskPackageSource -Destination (Join-Path $OutputDirectory $taskPackage.id) -Force
    }
    $taskRows += [ordered]@{ id = $taskSet.id; manifestName = $taskName;
        manifestSha256 = (Get-FileHash -LiteralPath $taskDestination).Hash.ToLowerInvariant() }
}
if (@($taskRows | Where-Object id -eq '001').Count -ne 1) { throw 'The initial legacy transition requires signed set 001.' }
return $taskRows
