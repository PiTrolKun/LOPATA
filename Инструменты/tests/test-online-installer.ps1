param([Parameter(Mandatory)][string]$StandDataRoot)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stand = [IO.Path]::GetFullPath($StandDataRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'Тесты/FileUpdates')) + [IO.Path]::DirectorySeparatorChar
if (!$stand.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stand path.' }
$app = Join-Path $stand 'App'
$receipt = Get-Content -LiteralPath (Join-Path $stand 'Installers/LOPATA_Setup_0.2.42-beta.exe.build.json') -Raw | ConvertFrom-Json
if (!$receipt.standBuild) { throw 'An isolated stand build is required.' }
$id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($app.TrimEnd('\').ToUpperInvariant()))).ToLowerInvariant().Substring(0,24)
$state = Join-Path $stand "Updates/Installations/$id"
$settings = Join-Path $stand 'settings.json'
$before = (Get-FileHash -LiteralPath $settings).Hash
$prior = $env:LOPATA_UPDATE_STAND_ROOT
$env:LOPATA_UPDATE_STAND_ROOT = $stand
function Run-Stand([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(180000) -or $process.ExitCode -ne 0) { throw "Isolated process failed: $File" }
    $process.Dispose()
}
try {
    Run-Stand (Join-Path $app 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    if (Test-Path -LiteralPath (Join-Path $state 'installed.signed.json')) { throw 'Uninstall retained old ownership.' }
    $cache = Join-Path $state 'packages'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    Get-ChildItem -LiteralPath $receipt.packageDirectory -Filter 'pkg-*.zip' -File | Copy-Item -Destination $cache -Force
    $setup = $receipt.onlineInstaller
    if ((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant() -ne $receipt.onlineInstallerSha256) { throw 'Online installer hash mismatch.' }
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/ACCEPTLICENSES=1','/UPDATESTAND=1','/UPDATECHANNEL=stable',('/DIR="' + $app + '"'))
    Run-Stand $setup ($arguments + @('/LOG="' + (Join-Path $stand 'online-install.log') + '"'))
    $registrationPath = Join-Path $stand 'Updates/installation.json'
    if (!(Test-Path -LiteralPath $registrationPath)) { throw 'Network installation was not registered.' }
    # Simulate interruption after the verified file transaction but before final Inno registration.
    Move-Item -LiteralPath $registrationPath -Destination (Join-Path $stand 'interrupted-registration.fixture.json')
    Run-Stand $setup ($arguments + @('/LOG="' + (Join-Path $stand 'online-registration-resume.log') + '"'))
    if (!(Test-Path -LiteralPath $registrationPath)) { throw 'Completed network install did not resume registration.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $before) { throw 'Settings changed during online installation.' }
    if ([IO.File]::ReadAllText((Join-Path $app 'legacy-user-file.txt')) -ne 'Keep this unknown file') { throw 'Unknown file was changed.' }
    @{check='online-inno-with-verified-local-cache';passed=$true;registrationResume=$true;settingsPreserved=$true;network='HTTP download interruption/resume covered separately by downloader/core tests'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stand 'online-install-result.json') -Encoding utf8
    Write-Host 'PASS: lightweight Inno, signed cached packages, registration resume, preserved data.'
}
finally { $env:LOPATA_UPDATE_STAND_ROOT = $prior }
