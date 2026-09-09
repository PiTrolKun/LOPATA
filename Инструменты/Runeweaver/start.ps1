param([switch]$DownloadOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$python = (Get-Command python -ErrorAction Stop).Source
& $python -u (Join-Path $PSScriptRoot 'download.py')
if ($LASTEXITCODE -ne 0) { throw 'Download failed. See the message above; run again to resume.' }
if ($DownloadOnly) {
    Write-Host 'Download complete. You may close this window.' -ForegroundColor Green
    return
}
$backend = Join-Path $root $config.backend
if (-not (Test-Path -LiteralPath $backend)) { throw "Backend not found: $backend" }
$model = Join-Path $root ('Тесты\Runeweaver\model\' + $config.filename)
$logs = Join-Path $root 'Тесты\Runeweaver\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$stamp = Get-Date -Format yyyyMMdd_HHmmss
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs "$stamp-config.json") -Encoding UTF8
Write-Host 'Paste or type your message. End it with / on a separate line to send.'
Write-Host 'Ctrl+C interrupts generation; follow the client prompt to exit. Backend log is saved.'
& $backend -m $model -c $config.context -ngl 99 -cnv -mli --simple-io `
    --temp $config.temperature --repeat-penalty $config.repeat_penalty -n 2048 `
    --log-file (Join-Path $logs "$stamp-backend.log") `
    -sysf (Join-Path $PSScriptRoot 'writer.txt')
if ($LASTEXITCODE -ne 0) { Write-Host "Client exit code: $LASTEXITCODE" }
