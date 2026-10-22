param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskData = Join-Path $taskRoot '_tmp/voice-input-stand'
$taskPython = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
if (!(Test-Path -LiteralPath $taskPython)) {
    & $Python -m venv (Join-Path $PSScriptRoot '.venv')
    if ($LASTEXITCODE) { throw 'Could not create the isolated Python environment.' }
}
& $taskPython -m pip install --only-binary=:all: -r (Join-Path $PSScriptRoot 'requirements.freeze.txt')
if ($LASTEXITCODE) { throw 'Stand dependency installation failed.' }
& $taskPython (Join-Path $PSScriptRoot 'prepare_assets.py')
if ($LASTEXITCODE) { throw 'Stand model preparation failed.' }
& $taskPython (Join-Path $PSScriptRoot 'finalize_assets.py')
if ($LASTEXITCODE) { throw 'Stand inventory preparation failed.' }
Write-Host 'Selected Parakeet CPU recognizer is prepared. No microphone recording was started.'
