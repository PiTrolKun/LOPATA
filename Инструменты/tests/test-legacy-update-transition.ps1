param([Parameter(Mandatory)][string]$StandDataRoot, [Parameter(Mandatory)][string]$LegacyAppDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stand = [IO.Path]::GetFullPath($StandDataRoot)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'Тесты/FileUpdates')) + [IO.Path]::DirectorySeparatorChar
if (!$stand.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stand path.' }
$app = Join-Path $stand 'App'
if (Test-Path -LiteralPath (Join-Path $app 'AIHub.exe')) { throw 'Use an empty isolated application folder.' }
$source = (Resolve-Path -LiteralPath $LegacyAppDirectory).Path
$legacyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'AIHub.dll')).ProductVersion
$setup = Join-Path $stand 'Installers/LOPATA_Setup_0.2.42-beta.exe'
$receipt = Get-Content -LiteralPath "$setup.build.json" -Raw | ConvertFrom-Json
if (!$receipt.standBuild -or (Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant() -ne $receipt.sha256) { throw 'Invalid isolated installer.' }
New-Item -ItemType Directory -Path $app -Force | Out-Null
# Never execute or modify the user's legacy installation: copy its program files into the stand.
Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notlike 'unins*' } | Copy-Item -Destination $app -Recurse -Force
$settings = '{"languageCode":"en","updates":{"checkOnStartup":false}}'
[IO.File]::WriteAllText((Join-Path $stand 'settings.json'), $settings)
$before = (Get-FileHash -LiteralPath (Join-Path $stand 'settings.json')).Hash
$control = Join-Path $app 'legacy-user-file.txt'
[IO.File]::WriteAllText($control, 'Keep this unknown file')
$prior = $env:LOPATA_UPDATE_STAND_ROOT
$env:LOPATA_UPDATE_STAND_ROOT = $stand
try {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/ACCEPTLICENSES=1','/UPDATESTAND=1','/UPDATECHANNEL=beta',('/DIR="' + $app + '"'),('/LOG="' + (Join-Path $stand 'legacy-transition.log') + '"'))
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(180000) -or $process.ExitCode -ne 0) { throw 'Transition installation did not complete.' }
    if ((Get-FileHash -LiteralPath (Join-Path $stand 'settings.json')).Hash -ne $before) { throw 'Installer modified legacy settings.' }
    if ([IO.File]::ReadAllText($control) -ne 'Keep this unknown file') { throw 'Unknown legacy file changed.' }
    $registration = Get-Content -LiteralPath (Join-Path $stand 'Updates/installation.json') -Raw | ConvertFrom-Json
    if ($registration.appDirectory -ne $app) { throw 'Incorrect transition registration.' }
    $targetVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'AIHub.dll')).ProductVersion
    if ($targetVersion -ne '0.2.42-beta') { throw 'Incorrect target version.' }
    @{check='legacy-program-files-full-transition';from=$legacyVersion;to=$targetVersion;passed=$true;settingsPreserved=$true;unknownFilePreserved=$true;scope='Copied legacy app files; isolated user profile and Inno registration'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stand 'legacy-transition-result.json') -Encoding utf8
    Write-Host "PASS: copied legacy $legacyVersion to $targetVersion; settings and unknown file preserved."
}
finally { $env:LOPATA_UPDATE_STAND_ROOT = $prior }
