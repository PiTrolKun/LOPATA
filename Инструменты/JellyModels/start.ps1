$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$Host.UI.RawUI.WindowTitle = 'Jelly - NuExtract3 + GLiNER2.5 download'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$python = Join-Path $root 'Runtime/Python/giga-embeddings/py312-torch210-transformers530/python.exe'
if (-not (Test-Path -LiteralPath $python)) { $python = (Get-Command python -ErrorAction Stop).Source }
& $python -u (Join-Path $PSScriptRoot 'download.py')
if ($LASTEXITCODE -ne 0) { throw 'Download interrupted or failed. Run this script again to resume.' }
Write-Host 'Download complete. You may close this window.' -ForegroundColor Green
