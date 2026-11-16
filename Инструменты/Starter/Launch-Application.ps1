function Get-LopataApplicationProcess {
    param([string]$ExecutablePath, [switch]$Visible)
    $expected = [IO.Path]::GetFullPath($ExecutablePath)
    $session = (Get-Process -Id $PID).SessionId
    foreach ($candidate in Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($expected)) -ErrorAction SilentlyContinue) {
        try {
            $candidate.Refresh()
            if ($candidate.SessionId -ne $session -or $candidate.Path -ine $expected) { continue }
            if (!$Visible -or ($candidate.MainWindowHandle -ne [IntPtr]::Zero -and $candidate.Responding)) { return $candidate }
        } catch [InvalidOperationException] { }
        catch [System.ComponentModel.Win32Exception] { } # Another user's process may be inaccessible.
    }
    return $null
}

function Wait-LopataApplicationWindow {
    param([Diagnostics.Process]$StartedProcess, [string]$ExecutablePath, [int]$TimeoutSeconds = 120)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $nextNotice = 5
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $StartedProcess.Refresh()
        if ($StartedProcess.HasExited -and $StartedProcess.ExitCode -ne 0) {
            throw "AI_HUB exited before opening its window. PID=$($StartedProcess.Id), exit=$($StartedProcess.ExitCode)."
        }
        # A second invocation normally exits after activating the existing, same-installation window.
        $visible = Get-LopataApplicationProcess -ExecutablePath $ExecutablePath -Visible
        if ($null -ne $visible) { return $visible }
        if ($clock.Elapsed.TotalSeconds -ge $nextNotice) {
            Write-Host "Waiting for the AI_HUB window... $([int]$clock.Elapsed.TotalSeconds)s (launch PID $($StartedProcess.Id))"
            $nextNotice += 5
        }
        Start-Sleep -Milliseconds 200
    }
    $state = if ($StartedProcess.HasExited) { "exited=$($StartedProcess.ExitCode)" } else { 'still running without a visible window' }
    throw "AI_HUB did not open its window within $TimeoutSeconds seconds. PID=$($StartedProcess.Id), $state."
}
