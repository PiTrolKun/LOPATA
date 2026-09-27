param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$folder = Split-Path -Parent $manifestPath
dotnet build (Join-Path $root 'Исходники/LOPATA.UpdateTool/LOPATA.UpdateTool.csproj') --configuration Release --verbosity quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Manifest verifier build failed.' }
$configPath = Join-Path $folder 'verification.json'
@{ publicKeysPath = Join-Path $root 'Исходники/LOPATA.Updates/release-keys.json'; targetManifestPath = $manifestPath } |
    ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
$verified = & dotnet (Join-Path $root 'Исходники/LOPATA.UpdateTool/bin/Release/net10.0/LOPATA.UpdateTool.dll') verify $configPath
if ($LASTEXITCODE -ne 0) { throw 'Release manifest signature or structure is invalid.' }
$manifest = $verified | Out-String | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.sourceCommit -ne $SourceCommit) { throw 'Signed payload and source receipt differ.' }
if (@($manifest.files | Where-Object { $_.root -eq 'app' -and $_.path -eq 'lopata-stand-build.marker' }).Count) {
    throw 'Stand payload must not be published.'
}
$assets = @($manifestPath)
$reused = @()
foreach ($package in $manifest.packages) {
    $expected = "https://github.com/PiTrolKun/LOPATA/releases/download/v$Version/$($package.id)"
    if ($package.url -eq $expected) {
        $path = Join-Path $folder $package.id
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne $package.size -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $package.sha256) {
            throw "Package differs from its signed manifest: $($package.id)"
        }
        $assets += $path
    }
    else { $reused += $package }
}
[pscustomobject]@{ Manifest = $manifest; Assets = $assets; Reused = $reused }
