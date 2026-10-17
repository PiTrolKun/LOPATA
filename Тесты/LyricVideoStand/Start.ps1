param(
    [Parameter(Mandatory = $true)][string]$Song,
    [switch]$SelfTest,
    [switch]$ExportTest,
    [switch]$SmokeUi
)
$ErrorActionPreference = 'Stop'
$standRoot = $PSScriptRoot
$workspaceRoot = (Resolve-Path -LiteralPath (Join-Path $standRoot '..\..')).Path
$songPath = (Resolve-Path -LiteralPath $Song).Path
$projectPath = Join-Path $standRoot 'LyricVideoStand.csproj'
$packageCache = Join-Path $env:USERPROFILE '.nuget\packages'
if ($ExportTest -and -not $SelfTest) { throw 'ExportTest requires SelfTest.' }
if ($SelfTest -and $SmokeUi) { throw 'Choose SelfTest or SmokeUi.' }

# This tool reuses locally installed pinned packages; it does not acquire models or runtimes.
& dotnet restore $projectPath --source $packageCache
if ($LASTEXITCODE -ne 0) { throw 'Stand restore failed.' }
& dotnet build $projectPath --no-restore -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Stand build failed.' }
& (Join-Path $workspaceRoot 'Инструменты\check-cyrillic-integrity.ps1') -Root $standRoot -Quiet
if ($LASTEXITCODE -ne 0) { throw 'Stand UTF-8 check failed.' }

$executablePath = Join-Path $standRoot 'bin\Debug\net10.0-windows\LOPATA.LyricVideoStand.exe'
# Start-Process joins its argument list, so quote each observed local path explicitly.
$launchArguments = '--root "' + $workspaceRoot + '" --song "' + $songPath + '"'
if ($SelfTest) { $launchArguments += ' --self-test' }
if ($ExportTest) { $launchArguments += ' --export-test' }
if ($SmokeUi) { $launchArguments += ' --smoke-ui' }
if ($SelfTest -or $SmokeUi) {
    $standProcess = Start-Process -FilePath $executablePath -ArgumentList $launchArguments -PassThru -Wait -WindowStyle Hidden
    if ($standProcess.ExitCode -ne 0) { throw "Stand verification failed with exit $($standProcess.ExitCode). Inspect out/*.log." }
}
else {
    # The user explicitly requested an interactive stand window.
    $standProcess = Start-Process -FilePath $executablePath -ArgumentList $launchArguments -PassThru
    Write-Output "Stand PID=$($standProcess.Id); logs and exports: $(Join-Path $standRoot 'out')"
}
