param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)

function Assert-PlainPath([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        try { $attributes = [IO.File]::GetAttributes($part) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Collection output cannot contain filesystem links.'
        }
    }
}
function Resolve-Payload([string]$Relative) {
    if ($Relative -cnotmatch '^[a-f0-9]{24}(-[a-f0-9]{8})?/[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw 'Invalid collection payload path.'
    }
    $full = [IO.Path]::GetFullPath((Join-Path $output $Relative))
    if (!$full.StartsWith($output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Collection payload escapes its output directory.'
    }
    Assert-PlainPath $full
    return $full
}
function Get-PayloadRevision($Entries, [string]$SourceRevision, [string]$Rid) {
    $lines = @('collection-payload-v2|' + $Rid + '|' + $SourceRevision)
    $paths = [string[]]@($Entries | ForEach-Object path)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($path in $paths) {
        $entry = $Entries | Where-Object path -CEQ $path | Select-Object -First 1
        $lines += $entry.path + '|' + ([long]$entry.size).ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + $entry.sha256
    }
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))
}

Assert-PlainPath $output
$sourceRoots = @('LOPATA.Collection', 'LOPATA.CollectionWorker', 'LOPATA.CollectionShell', 'LOPATA.Updates')
$inputs = foreach ($folder in $sourceRoots) {
    Get-ChildItem -LiteralPath (Join-Path $root "Исходники/$folder") -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|x64|ARM64)[\\/]' -and $_.Extension -in @('.cs', '.csproj', '.cpp', '.h', '.def', '.vcxproj') }
}
$identity = [Collections.Generic.List[string]]::new()
$identity.Add('collection-source-v2|' + $Configuration + '|' + $RuntimeIdentifier + '|' + [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim())
$inputs += Get-Item -LiteralPath (Join-Path $root 'Исходники/AIHub/Services/ComponentAudit.cs'), (Join-Path $root 'Исходники/AIHub/Services/CollectionComponent.cs')
$inputs += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File
foreach ($file in $inputs | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    $identity.Add($relative + '|' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
}
$sourceRevision = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($identity -join "`n"))))
$manifestPath = Join-Path $output 'current.json'
Assert-PlainPath $manifestPath
if (Test-Path -LiteralPath $manifestPath) {
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 256KB) { throw 'Collection manifest is too large.' }
    $old = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $entries = @($old.files)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $null = Resolve-Payload $entry.path
        if (!$seen.Add($entry.path)) { throw 'Duplicate collection payload path.' }
    }
    $null = Resolve-Payload $old.native
    $null = Resolve-Payload $old.worker
    $intact = $old.schema -eq 1 -and $old.sourceRevision -ceq $sourceRevision -and
        $old.runtimeIdentifier -ceq $RuntimeIdentifier -and $entries.Count -gt 0 -and $entries.Count -le 32 -and
        $seen.Contains($old.native) -and $seen.Contains($old.worker)
    if ($intact) {
        $generation = $old.native.Split('/')[0]
        $intact = $old.native -ceq "$generation/LOPATA.CollectionShell.dll" -and $old.worker -ceq "$generation/LOPATA.CollectionWorker.exe"
        foreach ($entry in $entries) {
            $path = Resolve-Payload $entry.path
            if ($entry.path.Split('/')[0] -cne $generation -or $entry.size -lt 0 -or $entry.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
                !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.size -or
                (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $entry.sha256) { $intact = $false; break }
        }
        if ($intact -and $old.revision -ceq (Get-PayloadRevision $entries $sourceRevision $RuntimeIdentifier)) {
            [IO.File]::WriteAllText((Join-Path (Split-Path $output -Parent) 'collection-generation.txt'), $generation)
            Write-Host 'Collection payload is current.'; return
        }
    }
}

$temporary = Join-Path $root ('_tmp/collection-build/' + [guid]::NewGuid().ToString('N'))
$native = Join-Path $temporary 'native'
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'ARM64' } else { 'x64' }
& (Join-Path $PSScriptRoot 'build-native.ps1') -OutputDirectory $native -Configuration $Configuration -Architecture $architecture
if ($LASTEXITCODE -ne 0) { throw 'The native collection module did not build.' }
$worker = Join-Path $temporary 'worker'
& dotnet publish (Join-Path $root 'Исходники/LOPATA.CollectionWorker/LOPATA.CollectionWorker.csproj') `
    --configuration $Configuration --runtime $RuntimeIdentifier --self-contained true -p:PublishSingleFile=true `
    --output $worker --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'The collection worker did not build.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$staged = Join-Path $output ('.payload-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staged) | Out-Null
Copy-Item -LiteralPath (Join-Path $native 'LOPATA.CollectionShell.dll') -Destination $staged
foreach ($file in Get-ChildItem -LiteralPath $worker -File | Where-Object Extension -ne '.pdb') {
    Copy-Item -LiteralPath $file.FullName -Destination $staged
}
$generation = $sourceRevision.Substring(0, 24)
while (Test-Path -LiteralPath (Join-Path $output $generation)) {
    $generation = $sourceRevision.Substring(0, 24) + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
}
$destination = Join-Path $output $generation
Assert-PlainPath $destination
# A generation is published once by directory rename. Existing DLLs are never overwritten.
[IO.Directory]::Move($staged, $destination)
$entries = @(Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path = "$generation/$($_.Name)"; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
if ($entries.Count -lt 2 -or $entries.Count -gt 32) { throw 'Incomplete collection payload.' }
foreach ($entry in $entries) { $null = Resolve-Payload $entry.path }
$manifest = [ordered]@{
    schema = 1; sourceRevision = $sourceRevision; runtimeIdentifier = $RuntimeIdentifier
    revision = Get-PayloadRevision $entries $sourceRevision $RuntimeIdentifier
    native = "$generation/LOPATA.CollectionShell.dll"
    worker = "$generation/LOPATA.CollectionWorker.exe"
    files = $entries
}
$text = $manifest | ConvertTo-Json -Depth 5
$next = $manifestPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($next, $text, [Text.UTF8Encoding]::new($false))
[IO.File]::Move($next, $manifestPath, $true)
[IO.File]::WriteAllText((Join-Path (Split-Path $output -Parent) 'collection-generation.txt'), $generation)
Write-Host "Collection payload ready: $generation"
