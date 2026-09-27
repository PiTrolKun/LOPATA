param([Parameter(Mandatory)][string]$StandDataRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stand = [IO.Path]::GetFullPath($StandDataRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Тесты/FileUpdates')) + [IO.Path]::DirectorySeparatorChar
if (!$stand.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stand root.' }
$receipt = Get-Content -LiteralPath (Join-Path $stand 'Installers/LOPATA_Setup_0.2.42-beta.exe.build.json') -Raw | ConvertFrom-Json
$upgradeReceipt = Get-Content -LiteralPath (Join-Path $stand 'UpgradePackages/file-update.build.json') -Raw | ConvertFrom-Json
if (!$receipt.standBuild -or !$upgradeReceipt.standBuild) { throw 'Both builds must be isolated fixtures.' }
$app = Join-Path $stand 'App'
$registration = Get-Content -LiteralPath (Join-Path $stand 'Updates/installation.json') -Raw | ConvertFrom-Json
if ($registration.appDirectory -ne $app) { throw 'Wrong installed application.' }
$id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($app.TrimEnd('\').ToUpperInvariant()))).ToLowerInvariant().Substring(0,24)
$state = Join-Path $stand "Updates/Installations/$id"
$signed = Get-Content -LiteralPath (Join-Path $stand 'UpgradePackages/lopata-files.json') -Raw | ConvertFrom-Json
$manifest = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($signed.payload)) | ConvertFrom-Json
if ($manifest.version -ne '0.2.43-beta') { throw 'Wrong fixture target.' }
# Simulate an already verified download; the installed updater must revalidate every cached archive.
$cache = Join-Path $state 'packages'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
foreach ($folder in @($receipt.packageDirectory, (Join-Path $stand 'UpgradePackages'))) {
    Get-ChildItem -LiteralPath $folder -Filter 'pkg-*.zip' -File | Copy-Item -Destination $cache -Force
}
$protected = @('Projects/control.txt','Models/control.txt','App/personal.txt','Updates/channel.json')
$before = @{}
foreach ($relative in $protected) { $before[$relative] = (Get-FileHash -LiteralPath (Join-Path $stand $relative)).Hash }
$prepared = @{version=$manifest.version;delivery=1;installer=$null;installerPath=$null;files=$signed;applyOnNextLaunch=$true;notes=$manifest.notes;notesIncomplete=$false}
$prepared | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $state 'prepared.json') -Encoding utf8
$previousEnv = $env:LOPATA_UPDATE_STAND_ROOT
$env:LOPATA_UPDATE_STAND_ROOT = $stand
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    $first = Start-Process -FilePath (Join-Path $app 'AIHub.exe') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $running = $null
    do {
        $running = Get-Process AIHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'AIHub.exe') -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($running -and $running.MainWindowTitle -like '*0.2.43-beta*' -and !(Test-Path -LiteralPath (Join-Path $state 'prepared.json'))) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$running -or $running.MainWindowTitle -notlike '*0.2.43-beta*') { throw 'Deferred update did not open the target application.' }
    if (Test-Path -LiteralPath (Join-Path $state 'prepared.json')) { throw 'Healthy launch did not consume the prepared decision.' }
    $journal = Get-Content -LiteralPath (Join-Path $state 'transaction.json') -Raw | ConvertFrom-Json
    if ($journal.phase -ne 3) { throw "Unexpected journal phase: $($journal.phase)" }
    $roots = @{app=$registration.appDirectory;llama=$registration.llamaDirectory;chatllm=$registration.chatLlmDirectory}
    foreach ($file in $manifest.files) {
        $path = Join-Path $roots[$file.root] $file.path
        if ((Get-Item -LiteralPath $path).Length -ne $file.size -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Target mismatch: $($file.path)" }
    }
    foreach ($relative in $protected) {
        if ((Get-FileHash -LiteralPath (Join-Path $stand $relative)).Hash -ne $before[$relative]) { throw "Protected stand file changed: $relative" }
    }
    $title = $running.MainWindowTitle
    if (!$running.CloseMainWindow() -or !$running.WaitForExit(60000)) { throw 'Updated application did not close gracefully.' }
    $running.Dispose(); $first.Dispose()
    @{check='real-deferred-file-update-and-health-ack';passed=$true;title=$title;files=$manifest.files.Count;elapsedSeconds=$timer.Elapsed.TotalSeconds;protectedFiles=$protected.Count} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stand 'deferred-update-result.json') -Encoding utf8
    Write-Host 'PASS: deferred update, target files, healthy launch and user-file preservation.'
}
finally { $env:LOPATA_UPDATE_STAND_ROOT = $previousEnv }
