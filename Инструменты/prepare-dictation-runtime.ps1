param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $workspace 'Runtime\VoiceInput\sherpa-onnx-1.13.8'
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) { $destination = [System.IO.Path]::GetFullPath($OutputDirectory) }
$inventory = Get-Content -LiteralPath (Join-Path $workspace 'Исходники\AIHub\Tools\dictation-runtime-files.json') -Raw | ConvertFrom-Json
$cache = Join-Path $workspace '_tmp\dictation-runtime-prepare'
New-Item -ItemType Directory -Path $destination, $cache -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression

# Restore only the three pinned Windows CPU files; never extract other platforms or frameworks.
foreach ($asset in $inventory) {
    $target = Join-Path $destination $asset.File
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $asset.Hash) {
            throw "Existing runtime differs from the pinned inventory; preserve and investigate: $target"
        }
        continue
    }
    $package = if ($asset.File -eq 'sherpa-onnx.dll') { 'org.k2fsa.sherpa.onnx' } else { 'org.k2fsa.sherpa.onnx.runtime.win-x64' }
    $archive = Join-Path $cache "$package.1.13.8.nupkg"
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$package/1.13.8/$package.1.13.8.nupkg" -OutFile $archive
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    $temporary = Join-Path $cache ($asset.File + '.verified-part')
    try {
        $entries = @($zip.Entries | Where-Object {
            $_.Name -eq $asset.File -and
            ($asset.File -ne 'sherpa-onnx.dll' -or $_.FullName -match '^lib/net10\.0/')
        })
        if ($entries.Count -ne 1) { throw "Ambiguous package entry: $($asset.File)" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $temporary, $true)
        if ((Get-Item -LiteralPath $temporary).Length -ne $asset.Bytes -or
            (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $asset.Hash) {
            throw "Pinned runtime hash mismatch: $($asset.File)"
        }
        Move-Item -LiteralPath $temporary -Destination $target
    }
    finally { $zip.Dispose() }
}
Write-Host 'Pinned sherpa-onnx 1.13.8 CPU runtime is ready.'
