param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$MsvcDirectory,
    [Parameter(Mandatory)][ValidateSet('cpu','vulkan')][string]$Backend,
    [string]$VulkanLoader,
    [string]$VulkanLicense
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne 'd4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c') { throw 'Unexpected source revision.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$payload = Join-Path $output "payload-$Backend"
if (Test-Path -LiteralPath $payload) { throw 'Use a fresh output directory to preserve prior artifacts.' }
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $BuildDirectory 'bin') -File) {
    if ($file.Extension -notin @('.exe','.dll')) { continue }
    if ($file.Name -match 'libomp|cuda|cublas') { throw 'Unexpected excluded dependency.' }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload $file.Name)
}
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    $file = Get-Item -LiteralPath (Join-Path $MsvcDirectory $name)
    if ($file.VersionInfo.FileVersion -ne '14.44.35211.0') { throw 'Unexpected MSVC redistributable version.' }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $payload $name)
}
$notices = Join-Path $payload 'Notices'
New-Item -ItemType Directory -Path $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $notices 'llama-MIT.txt')
# Retain original third-party headers and their notices, including dual-license blocks.
Copy-Item -LiteralPath (Join-Path $source 'vendor') -Destination $notices -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Исходники/AIHub/Licenses/texts/music-MSVC-RUNTIME.txt') -Destination (Join-Path $notices 'MSVC-RUNTIME.txt')
if ($Backend -eq 'vulkan') {
    if ((Get-FileHash -LiteralPath $VulkanLoader).Hash -ne 'C7EE7F7EDEB5F2C175EFFA60621B33FC4B6D3E8764D03C9491C77D754BD804F1') { throw 'Unexpected Vulkan loader.' }
    if ((Get-AuthenticodeSignature -LiteralPath $VulkanLoader).Status -ne 'Valid') { throw 'Vulkan loader signature is not valid.' }
    Copy-Item -LiteralPath $VulkanLoader -Destination (Join-Path $payload 'vulkan-1.dll')
    Copy-Item -LiteralPath $VulkanLicense -Destination (Join-Path $notices 'Vulkan-Loader-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $source 'ggml/src/ggml-vulkan') -Destination $notices -Recurse
}
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/'); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ version='b9442-lopata1'; revision='d4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c'; backend=$Backend;
    openmp=$false; cuda=$false; embeddedUI=$false; msvc='14.44.35211.0'; files=$files }
[IO.File]::WriteAllText((Join-Path $payload 'runtime-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$archive = Join-Path $output "lopata-llama-b9442-lopata1-win-$Backend-x64.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -CompressionLevel Optimal
$installedBytes = 0L
foreach ($file in $files) { $installedBytes += [long]$file['size'] }
[ordered]@{file=[IO.Path]::GetFileName($archive);bytes=(Get-Item -LiteralPath $archive).Length;installedBytes=$installedBytes;sha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()} | ConvertTo-Json
