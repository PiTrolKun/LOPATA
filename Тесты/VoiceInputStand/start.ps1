$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskData = Join-Path $taskRoot '_tmp/voice-input-stand'
foreach ($taskRequired in @('models.json','runtime-files.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskData $taskRequired))) {
        throw 'The stand is not prepared. Run its prepare.ps1 first.'
    }
}
if (!(Test-Path -LiteralPath (Join-Path $taskRoot 'Исходники/AIHub/bin/Debug/net10.0-windows/AIHub.dll'))) {
    dotnet build (Join-Path $taskRoot 'Исходники/AIHub/AIHub.csproj')
    if ($LASTEXITCODE) { throw 'Common component-audit library build failed.' }
}
dotnet build (Join-Path $PSScriptRoot 'VoiceInputStand.csproj') --nologo
if ($LASTEXITCODE) { throw 'Voice input stand build failed.' }
Start-Process -FilePath (Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/LOPATA.VoiceInputStand.exe') `
    -ArgumentList ('"' + $taskRoot + '"') -WorkingDirectory $taskRoot
