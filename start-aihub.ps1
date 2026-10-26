param([int]$LaunchTimeoutSeconds = 120)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'Исходники\AIHub\AIHub.csproj'
$exe = Join-Path $PSScriptRoot 'Исходники\AIHub\bin\Debug\net10.0-windows\AIHub.exe'
$logDirectory = Join-Path $PSScriptRoot 'Runtime\Logs\Launcher'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$log = Join-Path $logDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N') + '.log')
"Started: $([DateTimeOffset]::Now.ToString('o'))" | Out-File -LiteralPath $log -Encoding utf8
try {
    . (Join-Path $PSScriptRoot 'Инструменты\Starter\Launch-Application.ps1')
    if (!(Test-Path -LiteralPath $projectPath)) { throw "AIHub.csproj was not found: $projectPath" }
    $existing = Get-LopataVisibleProcess -ExecutablePath $exe
    if ($null -eq $existing) {
        Write-Host 'Building AI_HUB...'
        & dotnet build $projectPath --nologo 2>&1 | Tee-Object -FilePath $log -Append
        $buildCode = $LASTEXITCODE
        if ($buildCode -ne 0) { throw "Build failed with exit code $buildCode. Application was not started." }
    } else {
        Write-Host "AI_HUB is already running (PID $($existing.Id)); activating it. Close it before rebuilding changed sources."
    }
    if (!(Test-Path -LiteralPath $exe)) { throw "AIHub.exe was not found: $exe" }
    Write-Host 'Starting AI_HUB...'
    $started = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -PassThru
    "Launch PID: $($started.Id)" | Out-File -LiteralPath $log -Encoding utf8 -Append
    $window = Wait-LopataApplicationWindow -StartedProcess $started -ExecutablePath $exe -TimeoutSeconds $LaunchTimeoutSeconds
    $result = "AI_HUB window opened: PID=$($window.Id), title=$($window.MainWindowTitle)"
    Write-Host $result
    $result | Out-File -LiteralPath $log -Encoding utf8 -Append
} catch {
    $message = $_.Exception.Message
    "FAILED: $message" | Out-File -LiteralPath $log -Encoding utf8 -Append
    Write-Host "AI_HUB start failed: $message" -ForegroundColor Red
    Write-Host "Launch log: $log"
    exit 1
}
