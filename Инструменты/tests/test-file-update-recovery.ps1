param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolProject = Join-Path $root 'Исходники/LOPATA.UpdateTool/LOPATA.UpdateTool.csproj'
& dotnet build $toolProject --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot build update stand tool.' }
$tool = Join-Path $root 'Исходники/LOPATA.UpdateTool/bin/Debug/net10.0/LOPATA.UpdateTool.dll'
$stand = Join-Path $root ('Тесты/FileUpdates/' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stand
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
function Run-Tool([string]$Command, [string]$Config, [string]$Id = '') {
    $arguments = @($tool, $Command, $Config)
    if ($Id) { $arguments += $Id }
    $output = & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tool failed: $Command" }
    return $output
}
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$private = Join-Path $stand 'test-only-private.pem'
$public = Join-Path $stand 'test-only-public.json'
[IO.File]::WriteAllText($private, $key.ExportPkcs8PrivateKeyPem())
Write-Json $public @{ stand = $key.ExportSubjectPublicKeyInfoPem() }
$key.Dispose()
$old = Join-Path $stand 'old-payload'
$new = Join-Path $stand 'new-payload'
$null = New-Item -ItemType Directory -Path $old, $new
[IO.File]::WriteAllText((Join-Path $old 'AIHub.exe'), 'old binary')
[IO.File]::WriteAllText((Join-Path $old 'obsolete.dll'), 'old dependency')
[IO.File]::WriteAllText((Join-Path $new 'AIHub.exe'), 'new binary')
[IO.File]::WriteAllText((Join-Path $new 'added.dll'), 'new dependency')
$oldPackages = Join-Path $stand 'old-packages'
$newPackages = Join-Path $stand 'new-packages'
$timestamp = [DateTimeOffset]::UtcNow.ToString('o')
foreach ($item in @(@('0.2.42-beta', $old, $oldPackages), @('0.2.43-beta', $new, $newPackages))) {
    $configPath = Join-Path $stand ($item[0] + '.json')
    Write-Json $configPath @{
        roots = @{ app = $item[1] }; publicKeysPath = $public; privateKeyPath = $private; keyId = 'stand'
        version = $item[0]; sourceCommit = ('a' * 40); outputDirectory = $item[2]
        notes = @(@{ version = $item[0]; publishedUtc = $timestamp; text = 'Isolated recovery stand; not a product release.' })
    }
    Run-Tool 'pack' $configPath | Out-Host
}
$results = @()
foreach ($stopAfter in @(1, 2, 3)) {
    $case = Join-Path $stand "interruption-$stopAfter"
    $app = Join-Path $case 'installed'
    $null = New-Item -ItemType Directory -Path $app -Force
    Copy-Item -LiteralPath (Join-Path $old 'AIHub.exe'), (Join-Path $old 'obsolete.dll') -Destination $app
    [IO.File]::WriteAllText((Join-Path $app 'user-book.txt'), 'unchanged user manuscript')
    $configPath = Join-Path $case 'stand.json'
    $config = @{
        roots = @{ app = $app }; publicKeysPath = $public; stateDirectory = (Join-Path $case 'state')
        targetManifestPath = (Join-Path $oldPackages 'lopata-files.json')
        stagingDirectory = (Join-Path $case 'stage'); outputDirectory = $newPackages; pauseAfterFiles = $stopAfter
    }
    Write-Json $configPath $config
    Run-Tool 'register' $configPath | Out-Host
    $config.targetManifestPath = Join-Path $newPackages 'lopata-files.json'
    Write-Json $configPath $config
    Run-Tool 'extract' $configPath | Out-Host
    $id = (Run-Tool 'prepare' $configPath | Out-String).Trim()
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($arg in @($tool, 'apply', $configPath, $id)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    $reached = $false
    try {
        while (-not $process.HasExited) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(10000)) { throw 'Timed out waiting for update checkpoint.' }
            if ($null -eq $read.Result) { break }
            $checkpoint = $read.Result | ConvertFrom-Json
            if ($checkpoint.Point -eq 'file-applied' -and $checkpoint.CompletedFiles -eq $stopAfter) {
                $reached = $true
                # Only the child process created above is terminated; no application/backend process lookup.
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) { throw 'Stand child did not exit.' }
                break
            }
        }
        if (-not $reached) { throw 'Requested interruption point was not reached.' }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        $process.Dispose()
    }
    Run-Tool 'recover' $configPath | Out-Host
    Run-Tool 'recover' $configPath | Out-Host
    if ([IO.File]::ReadAllText((Join-Path $app 'AIHub.exe')) -ne 'old binary' -or
        [IO.File]::ReadAllText((Join-Path $app 'obsolete.dll')) -ne 'old dependency' -or
        (Test-Path -LiteralPath (Join-Path $app 'added.dll')) -or
        [IO.File]::ReadAllText((Join-Path $app 'user-book.txt')) -ne 'unchanged user manuscript') {
        throw "Recovery did not restore the old installation: $stopAfter"
    }
    $results += @{ interruptedAfter = $stopAfter; recovery = 'passed'; userData = 'unchanged'; repeatRecovery = 'passed' }
}
Write-Json (Join-Path $stand 'result.json') @{
    cases = $results
    limitation = 'Actual child process termination tested. This does not simulate sudden power loss or storage-controller cache loss.'
}
Write-Host "PASS: three separate-process interruptions and recovery. Evidence: $stand"
