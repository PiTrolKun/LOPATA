param([Parameter(Mandatory)][string]$StandDataRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stand = [IO.Path]::GetFullPath($StandDataRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Тесты/FileUpdates')) + [IO.Path]::DirectorySeparatorChar
if (!$stand.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stand root.' }
$setup = Join-Path $stand 'Installers/LOPATA_Setup_0.2.42-beta.exe'
$receipt = Get-Content -LiteralPath "$setup.build.json" -Raw | ConvertFrom-Json
if ($receipt.standBuild -ne $true) { throw 'Only the explicitly isolated installer may run in this stand.' }
if ((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant() -ne $receipt.sha256) { throw 'Stand installer hash mismatch.' }
$app = Join-Path $stand 'App'
$realData = Join-Path $env:LOCALAPPDATA 'AI_HUB'
$protected = @('settings.json','Licenses/installer-receipts.json','Updates/channel.json','Updates/installation.json')
$before = @{}
foreach ($relative in $protected) {
    $path = Join-Path $realData $relative
    $before[$relative] = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { $null }
}
$previousStandEnvironment = $env:LOPATA_UPDATE_STAND_ROOT
$env:LOPATA_UPDATE_STAND_ROOT = $stand
$results = [Collections.Generic.List[object]]::new()
function Run-StandProcess([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(180000)) { throw "Stand process timed out; inspect process $($process.Id)." }
    $code = $process.ExitCode
    $process.Dispose()
    return $code
}
try {
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($relative in @('Projects/control.txt','Models/control.txt','App/personal.txt')) {
        $path = Join-Path $stand $relative
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.File]::WriteAllText($path,'Must survive installation and uninstall')
    }
    $baseArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/ACCEPTLICENSES=1','/UPDATESTAND=1',('/DIR="' + $app + '"'))
    $code = Run-StandProcess $setup ($baseArguments + @('/LOG="' + (Join-Path $stand 'missing-direction.log') + '"'))
    if ($code -eq 0 -or (Test-Path -LiteralPath (Join-Path $app 'AIHub.exe'))) { throw 'Missing direction was accepted.' }
    $results.Add(@{check='explicit-direction-required'; passed=$true})
    $code = Run-StandProcess $setup ($baseArguments + @('/UPDATECHANNEL=beta',('/LOG="' + (Join-Path $stand 'first-install.log') + '"')))
    if ($code -ne 0) { throw "First installation failed: $code" }
    $registration = Get-Content -LiteralPath (Join-Path $stand 'Updates/installation.json') -Raw | ConvertFrom-Json
    if ($registration.appDirectory -ne $app -or !$registration.llamaDirectory.StartsWith($stand) -or !$registration.chatLlmDirectory.StartsWith($stand)) {
        throw 'Registration escaped the isolated stand.'
    }
    if ((Get-Content -LiteralPath (Join-Path $stand 'Updates/channel.json') -Raw | ConvertFrom-Json).channel -ne 'beta') { throw 'Beta choice was not stored.' }
    $results.Add(@{check='first-install-and-signed-registration'; passed=$true})
    # Direct executable launch must hand off to the installed launcher and return to the application.
    $first = Start-Process -FilePath (Join-Path $app 'AIHub.exe') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $running = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $running = Get-Process AIHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'AIHub.exe') -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($running) { break }
        Start-Sleep -Milliseconds 250
    }
    if (!$running) { throw 'The isolated application window did not open through the launcher.' }
    $results.Add(@{check='direct-launch-through-independent-updater'; passed=$true; title=$running.MainWindowTitle})
    if (!$running.CloseMainWindow() -or !$running.WaitForExit(60000)) { throw 'The stand application did not close gracefully.' }
    $running.Dispose(); $first.Dispose()
    if (!(Test-Path -LiteralPath (Join-Path $stand 'settings.json'))) { throw 'Application settings were not isolated.' }
    $code = Run-StandProcess $setup ($baseArguments + @('/UPDATECHANNEL=stable',('/LOG="' + (Join-Path $stand 'repeat-install.log') + '"')))
    if ($code -ne 0) { throw "Repeat installation failed: $code" }
    if ((Get-Content -LiteralPath (Join-Path $stand 'Updates/channel.json') -Raw | ConvertFrom-Json).channel -ne 'stable') { throw 'Repeat installer did not accept changed choice.' }
    $results.Add(@{check='repeat-install-and-direction-change'; passed=$true})
    $code = Run-StandProcess (Join-Path $app 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $stand 'uninstall.log') + '"'))
    if ($code -ne 0 -or (Test-Path -LiteralPath (Join-Path $app 'AIHub.exe'))) { throw "Uninstall failed: $code" }
    foreach ($relative in @('Projects/control.txt','Models/control.txt','App/personal.txt')) {
        if ([IO.File]::ReadAllText((Join-Path $stand $relative)) -ne 'Must survive installation and uninstall') { throw "Control file changed: $relative" }
    }
    if (Test-Path -LiteralPath (Join-Path $stand 'Updates/installation.json')) { throw 'Installation registration was retained after uninstall.' }
    $results.Add(@{check='uninstall-preserves-unknown-files-projects-models'; passed=$true})
}
finally {
    $env:LOPATA_UPDATE_STAND_ROOT = $previousStandEnvironment
    foreach ($relative in $protected) {
        $path = Join-Path $realData $relative
        $after = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { $null }
        if ($after -ne $before[$relative]) { throw "Real user data unexpectedly changed: $relative" }
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stand 'installed-test-results.json') -Encoding utf8
}
Write-Host "PASS: $($results.Count) installed integration checks; real user data unchanged."
