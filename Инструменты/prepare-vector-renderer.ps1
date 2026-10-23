param([switch]$RegisterLicense)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root 'Runtime/Svg/resvg-0.47.0'
$manifest = @(Get-Content -LiteralPath (Join-Path $root 'Исходники/AIHub/Tools/vector-renderer-files.json') -Raw | ConvertFrom-Json)
$licenses = Join-Path $root 'Исходники/AIHub/Licenses'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('lopata-resvg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    $archive = Join-Path $temporary 'resvg.zip'
    Invoke-WebRequest 'https://github.com/linebender/resvg/releases/download/v0.47.0/resvg-win64.zip' -OutFile $archive -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '5684e59ceaa53ce720b49efb441b0918ae99d04e8ce3f6f753664524592d67f1') { throw 'resvg archive checksum mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $temporary 'unpacked')
    $binary = @(Get-ChildItem -LiteralPath (Join-Path $temporary 'unpacked') -Recurse -File -Filter resvg.exe)
    if ($binary.Count -ne 1) { throw 'Expected exactly one resvg.exe' }
    Copy-Item -LiteralPath $binary[0].FullName -Destination (Join-Path $temporary 'resvg.exe')
    foreach ($name in @('LICENSE-MIT','LICENSE-APACHE','THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $licenses ('texts/svg-resvg-' + $name + '.txt')) -Destination (Join-Path $temporary $name)
    }
    foreach ($file in $manifest) {
        $path = Join-Path $temporary $file.Path
        if ((Get-Item -LiteralPath $path).Length -ne $file.Size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { throw ('resvg file checksum mismatch: ' + $file.Path) }
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in $manifest) { Copy-Item -LiteralPath (Join-Path $temporary $file.Path) -Destination (Join-Path $destination $file.Path) -Force }
    if ($RegisterLicense) {
        # Scoped registration: do not regenerate historical model terms or installer receipts.
        $catalogPath = Join-Path $licenses 'catalog.json'
        $catalog = @(Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json)
        $entry = @(Get-Content -LiteralPath (Join-Path $licenses 'svg-entries.json') -Raw | ConvertFrom-Json)[0]
        $terms = $entry.License + $entry.Ru + $entry.En
        foreach ($file in $entry.Texts) { $terms += [IO.File]::ReadAllText((Join-Path $licenses $file)) }
        $terms = $terms.Replace("`r`n", "`n").Replace("`r", "`n")
        $entry.Terms = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($terms)))
        $existing = @($catalog | Where-Object Id -eq $entry.Id)
        if ($existing.Count -gt 0 -and $existing[0].Terms -ne $entry.Terms) { throw 'Existing SVG terms changed: review explicitly before replacing them.' }
        if ($existing.Count -eq 0) { @($catalog + $entry) | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $catalogPath -Encoding utf8 }
    }
    Write-Host 'Pinned resvg 0.47.0 and original notices verified and restored.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('lopata-resvg-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
