param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CaptureRuntime/runtime.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $root 'Runtime/Capture/FFmpeg-8.1'
$bundle = Join-Path $runtime 'bundle'
New-Item -ItemType Directory -Force $runtime | Out-Null
if (!$ArchivePath) {
    $ArchivePath = Join-Path $runtime ('minimal-'+$pin.SHA256.Substring(0,12)+'.zip')
    if (!(Test-Path -LiteralPath $ArchivePath)) { Invoke-WebRequest $pin.Url -OutFile $ArchivePath }
}
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-FileHash -LiteralPath $archive).Hash -ne $pin.SHA256) { throw 'Pinned capture archive checksum mismatch. No files were copied.' }
$extracted = Join-Path $runtime ('verified-'+$pin.SHA256.Substring(0,12))
if (!(Test-Path -LiteralPath $extracted)) { Expand-Archive -LiteralPath $archive -DestinationPath $extracted }
$record = Get-Content -LiteralPath (Join-Path $extracted 'provenance.json') -Raw | ConvertFrom-Json
if ($record.Version -ne $pin.Version -or $record.SourceArchiveSha256 -ne $pin.SourceSHA256 -or $record.SourceArchive -ne $pin.SourceUrl) { throw 'Runtime provenance does not match the pinned source package.' }
foreach ($file in $record.Files) {
    if ([IO.Path]::IsPathRooted($file.Name) -or $file.Name -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid runtime file path.' }
    if ((Get-FileHash -LiteralPath (Join-Path $extracted $file.Name)).Hash -ne $file.Sha256) { throw "Runtime file checksum mismatch: $($file.Name)" }
}
# Validate the complete archive before touching the working bundle.
New-Item -ItemType Directory -Force $bundle | Out-Null
foreach ($file in $record.Files) {
    $target = Join-Path $bundle $file.Name
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $extracted $file.Name) -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $extracted 'provenance.json') -Destination $bundle -Force
Write-Host "Prepared pinned minimal capture runtime: $bundle"
