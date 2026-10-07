param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OfficialArchive,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$MsvcDirectory,
    [Parameter(Mandatory)][string]$OpenMpDirectory,
    [Parameter(Mandatory)][ValidateSet('cpu','vulkan')][string]$Backend,
    [string]$VulkanLoader,
    [string]$VulkanLicense
)
$ErrorActionPreference = 'Stop'
$revision = '3f8527a46c54ecf4cb4ed6003da8e8982283c73c'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne $revision) { throw 'Unexpected source revision.' }
$expected = if ($Backend -eq 'cpu') { '5e7caca2080321b25a12c1fa4175cb7d953f2b182309f8f73bfc9c725231d26c' } else { '60e6850d650417409f18c2170ab5e27335db96da70cd3d1ad1e930bcffc2fd35' }
if ((Get-FileHash -LiteralPath $OfficialArchive).Hash.ToLowerInvariant() -ne $expected) { throw 'Official archive checksum mismatch.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output "payload-$Backend"
if (Test-Path -LiteralPath $payload) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Expand-Archive -LiteralPath $OfficialArchive -DestinationPath $payload
if (@(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -match 'libomp|cublas|cudart|ggml-cuda' }).Count) { throw 'Unexpected excluded backend dependency.' }
$executable = @(Get-ChildItem -LiteralPath $payload -Recurse -Filter sd-cli.exe -File)
if ($executable.Count -ne 1) { throw 'Unexpected archive layout.' }
$binDirectory = $executable[0].DirectoryName
foreach ($name in @('msvcp140.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) {
    $redistDirectory = if ($name -eq 'vcomp140.dll') { $OpenMpDirectory } else { $MsvcDirectory }
    $file = Get-Item -LiteralPath (Join-Path $redistDirectory $name)
    if ($file.VersionInfo.FileVersion -ne '14.44.35211.0' -or (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status -ne 'Valid') { throw "Unexpected redistributable: $name" }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $binDirectory $name)
}
$notices = Join-Path $payload 'Notices'
New-Item -ItemType Directory -Path $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $notices 'stable-diffusion-MIT.txt')
Copy-Item -LiteralPath (Join-Path $source 'ggml/LICENSE') -Destination (Join-Path $notices 'ggml-MIT.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Исходники/AIHub/Licenses/texts/music-MSVC-RUNTIME.txt') -Destination (Join-Path $notices 'MSVC-RUNTIME.txt')
foreach ($directory in @('thirdparty','ggml/src/ggml-vulkan')) {
    $noticeDirectory = Join-Path $notices $directory
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    # Retain source headers with their original notices and all standalone license files.
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $source $directory) -Recurse -File | Where-Object { $_.Extension -in @('.h','.hpp','.c','.cpp','.comp') -or $_.Name -match 'LICENSE|COPYING|PATENTS|AUTHORS' }) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $source $directory), $file.FullName)
        $target = Join-Path $noticeDirectory $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
if ($Backend -eq 'vulkan') {
    if ((Get-FileHash -LiteralPath $VulkanLoader).Hash -ne 'C7EE7F7EDEB5F2C175EFFA60621B33FC4B6D3E8764D03C9491C77D754BD804F1' -or (Get-AuthenticodeSignature -LiteralPath $VulkanLoader).Status -ne 'Valid') { throw 'Unexpected Vulkan loader.' }
    Copy-Item -LiteralPath $VulkanLoader -Destination (Join-Path $binDirectory 'vulkan-1.dll')
    Copy-Item -LiteralPath $VulkanLicense -Destination (Join-Path $notices 'Vulkan-Loader-LICENSE.txt')
}
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{version='3f8527a-lopata1';revision=$revision;backend=$Backend;officialArchiveSha256=$expected;msvc='14.44.35211.0';cuda=$false;files=$files}
[IO.File]::WriteAllText((Join-Path $payload 'runtime-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$archive = Join-Path $output "lopata-sd-3f8527a-lopata1-win-$Backend-x64.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -CompressionLevel Optimal
$installedBytes = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum
[ordered]@{file=[IO.Path]::GetFileName($archive);bytes=(Get-Item -LiteralPath $archive).Length;installedBytes=[long]$installedBytes;sha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant();manifestSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'runtime-manifest.json')).Hash.ToLowerInvariant()} | ConvertTo-Json
