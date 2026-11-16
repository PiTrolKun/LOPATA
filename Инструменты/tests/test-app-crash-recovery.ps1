param([switch]$KeepArtifacts, [switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stand = Join-Path $temp ('LOPATA-CrashRecovery-Test-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $stand 'App'
$profile = Join-Path $stand 'Profile'
$fixture = Join-Path $app 'AIHub.exe'
$helper = Join-Path $app 'LOPATA.Recovery.exe'
$windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$known = [Collections.Generic.HashSet[string]]::new()
$results = [Collections.Generic.List[object]]::new()
$taskName = $null
$caller = $null
$job = $null
$passed = $false

function Assert-Recovery([bool]$condition, [string]$message) {
    if (!$condition) { throw $message }
}

function Wait-Recovery([scriptblock]$condition, [string]$message) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 35) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 120
    }
    throw $message
}

function Get-StandProcess([int]$processId, [string]$exe, [long]$startTicks = 0) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and $process.Path -eq $exe -and (!$startTicks -or $process.StartTime.ToUniversalTime().Ticks -eq $startTicks)) {
        return $process
    }
}

function Invoke-RecoveryHelper([string[]]$arguments) {
    $process = Start-Process -FilePath $helper -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        Assert-Recovery ($process.WaitForExit(35000)) 'Recovery launch request did not return.'
        Assert-Recovery ($process.ExitCode -eq 0) "Recovery launch request failed with exit code $($process.ExitCode)."
    }
    finally { $process.Dispose() }
}

function Wait-Fixture([string]$mode) {
    $receipt = Wait-Recovery {
        foreach ($file in Get-ChildItem -LiteralPath $profile -File -Filter 'fixture-*.json') {
            $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if (!$known.Contains($record.id) -and (Get-StandProcess $record.pid $fixture $record.startTicks)) { return $record }
        }
    } "The $mode fixture did not start."
    Assert-Recovery ($receipt.mode -eq $mode) "Expected $mode, got $($receipt.mode)."
    [void]$known.Add($receipt.id)
    return $receipt
}

function Wait-QuietTask {
    [void](Wait-Recovery {
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        $alive = Get-Process -Name AIHub,LOPATA.Recovery -ErrorAction SilentlyContinue | Where-Object Path -In @($fixture,$helper)
        (!$task -or $task.State -notin @('Running','Queued')) -and !$alive
    } 'The recovery task or fixture remained active when it should have stopped.')
    Assert-Recovery (@(Get-ChildItem -LiteralPath $profile -File -Filter 'fixture-*.json').Count -eq $known.Count) 'Unexpected recovery launch.'
}

function Send-FixtureCommand($receipt, [string]$command) {
    [IO.File]::WriteAllText((Join-Path $profile "fixture-$($receipt.id).command"), $command)
}

function Stop-StandFixture($receipt, [uint32]$code) {
    $process = Get-StandProcess $receipt.pid $fixture $receipt.startTicks
    Assert-Recovery ($null -ne $process) 'The original fixture process was not found.'
    [LopataCrashRecoveryTestJob]::End($process.Handle, $code)
    Assert-Recovery ($process.WaitForExit(10000)) 'The fixture did not terminate.'
    $process.Dispose()
}

if (!$IsWindows) { throw 'Windows is required for this recovery test.' }
if (!('LopataCrashRecoveryTestJob' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public sealed class LopataCrashRecoveryTestJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcess, PerJob;
        public uint Flags;
        public UIntPtr WorkingSetMin, WorkingSetMax;
        public uint ActiveLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    private IntPtr job = CreateJobObject(IntPtr.Zero, null);
    public LopataCrashRecoveryTestJob()
    {
        if (job == IntPtr.Zero) throw new Win32Exception();
        var limits = new Limits();
        limits.Basic.Flags = 0x2000;
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Limits>())) return;
        var error = new Win32Exception();
        Dispose();
        throw error;
    }
    public void Add(IntPtr process)
    {
        if (!AssignProcessToJobObject(job, process)) throw new Win32Exception();
    }
    public bool Contains(IntPtr process)
    {
        if (!IsProcessInJob(process, job, out bool result)) throw new Win32Exception();
        return result;
    }
    public static void End(IntPtr process, uint code)
    {
        if (!TerminateProcess(process, code)) throw new Win32Exception();
    }
    public void Dispose()
    {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
'@
}

try {
    New-Item -ItemType Directory -Path $app,$profile | Out-Null
    $source = Join-Path $stand 'Fixture'
    New-Item -ItemType Directory -Path $source | Out-Null
    $recoveryProject = Join-Path $repo 'Исходники/LOPATA.Recovery/LOPATA.Recovery.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>AIHub</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(RecoveryProject)" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $source 'Fixture.csproj') -Encoding utf8
    @'
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Lopata.Recovery;

var path = Environment.ProcessPath!;
var profile = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture-profile.txt"));
if (args is ["--fixture-task-name"])
{
    File.WriteAllText(Path.Combine(profile, "task-name.txt"), RecoveryTask.GetName(path, profile));
    return;
}
var restoring = args.Contains("--recover-session");
var safe = args.Contains("--recover-safe");
var session = RecoverySession.Start(path, profile, args.Contains("--background"), restoring,
    error => File.AppendAllText(Path.Combine(profile, "fixture-error.log"), error + Environment.NewLine), safe);
var id = Guid.NewGuid().ToString("N");
var prefix = Path.Combine(profile, "fixture-" + id);
using var process = Process.GetCurrentProcess();
var checkpoint = Path.Combine(profile, "checkpoint.txt");
var receipt = new { id, pid = process.Id, startTicks = process.StartTime.ToUniversalTime().Ticks,
    mode = safe ? "safe" : restoring ? "restore" : "normal",
    checkpoint = File.Exists(checkpoint) ? File.ReadAllText(checkpoint) : null };
File.WriteAllText(prefix + ".pending", JsonSerializer.Serialize(receipt));
File.Move(prefix + ".pending", prefix + ".json");
while (!File.Exists(prefix + ".command")) Thread.Sleep(40);
var command = File.ReadAllText(prefix + ".command");
session.BeginExit(command == "update" ? RecoveryExitIntent.Update
    : command == "windows-session" ? RecoveryExitIntent.WindowsSessionEnding : RecoveryExitIntent.UserExit);
if (command == "interrupted-exit")
{
    File.WriteAllText(prefix + ".exit-started", "ready");
    Thread.Sleep(Timeout.Infinite);
}
session.CompleteExit();
'@ | Set-Content -LiteralPath (Join-Path $source 'Program.cs') -Encoding utf8
    $project = Join-Path $source 'Fixture.csproj'
    $buildArguments = @($project,'-c','Release','--artifacts-path',(Join-Path $stand 'Build'),'-o',$app,'-v','quiet',"-p:RecoveryProject=$recoveryProject")
    if ($SelfContained) { & dotnet publish @buildArguments -r win-x64 --self-contained true }
    else { & dotnet build @buildArguments }
    Assert-Recovery ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $fixture) -and (Test-Path -LiteralPath $helper)) 'Fixture and project-referenced recovery executable were not both built.'
    if ($SelfContained) {
        $config = Get-Content -LiteralPath (Join-Path $app 'LOPATA.Recovery.runtimeconfig.json') -Raw | ConvertFrom-Json
        Assert-Recovery (($config.runtimeOptions.includedFrameworks.name -contains 'Microsoft.NETCore.App') -and (Test-Path -LiteralPath (Join-Path $app 'hostfxr.dll'))) 'The published recovery executable requires a machine-wide .NET runtime.'
    }
    [IO.File]::WriteAllText((Join-Path $app 'fixture-profile.txt'), $profile)
    [IO.File]::WriteAllText((Join-Path $profile 'settings.json'), '{"languageCode":"ru","behavior":{"autoResumeBackgroundOperation":true}}')
    $nameProcess = Start-Process -FilePath $fixture -ArgumentList '--fixture-task-name' -WindowStyle Hidden -PassThru
    Assert-Recovery ($nameProcess.WaitForExit(10000) -and $nameProcess.ExitCode -eq 0) 'Cannot resolve isolated task name.'
    $taskName = [IO.File]::ReadAllText((Join-Path $profile 'task-name.txt'))
    Assert-Recovery ($taskName -cmatch '^LOPATA-Recovery-[a-f0-9]{24}$') 'Unexpected isolated task name.'
    Assert-Recovery (!(Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue)) 'Unexpected task name collision.'
    $nameProcess.Dispose()

    $launch = @('--launch', '--profile-root', ('"' + $profile + '"'))
    $launcher = Join-Path $stand 'launch.ps1'
    @'
param($stand, $helper, $profile)
$gate = Join-Path $stand 'caller.gate'
while (!(Test-Path -LiteralPath $gate)) { Start-Sleep -Milliseconds 40 }
$process = Start-Process -FilePath $helper -ArgumentList @('--launch','--profile-root',('"' + $profile + '"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
[IO.File]::WriteAllText((Join-Path $stand 'caller.ready'), [string]$process.ExitCode)
Start-Sleep -Seconds 90
'@ | Set-Content -LiteralPath $launcher -Encoding utf8
    $callerArguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File') + @($launcher,$stand,$helper,$profile).ForEach({ '"' + $_ + '"' })
    $job = [LopataCrashRecoveryTestJob]::new()
    $caller = Start-Process -FilePath $windowsPowerShell -ArgumentList $callerArguments -WindowStyle Hidden -PassThru
    $job.Add($caller.Handle)
    [IO.File]::WriteAllText((Join-Path $stand 'caller.gate'), 'go')
    [void](Wait-Recovery { Test-Path -LiteralPath (Join-Path $stand 'caller.ready') } 'Independent launch request did not complete.')
    Assert-Recovery ([IO.File]::ReadAllText((Join-Path $stand 'caller.ready')) -eq '0') 'The launch request inside the caller job failed.'
    $first = Wait-Fixture 'normal'
    $appProcess = Get-StandProcess $first.pid $fixture $first.startTicks
    $watcher = Wait-Recovery {
        Get-Process -Name LOPATA.Recovery -ErrorAction SilentlyContinue | Where-Object Path -Eq $helper | Select-Object -First 1
    } 'The scheduled recovery process did not remain running.'
    Assert-Recovery (!$job.Contains($appProcess.Handle) -and !$job.Contains($watcher.Handle)) 'The app or recovery process inherited the caller job.'
    $job.Dispose()
    Assert-Recovery ($caller.WaitForExit(10000)) 'The caller survived closing its job.'
    Assert-Recovery (!$appProcess.HasExited -and !$watcher.HasExited) 'The app or recovery process stopped when the caller was terminated.'
    $results.Add([ordered]@{ check = 'scheduled-processes-survive-caller-job'; caller = $caller.Id; app = $appProcess.Id; watcher = $watcher.Id })

    [IO.File]::WriteAllText((Join-Path $profile 'checkpoint.txt'), 'confirmed-step-3')
    Stop-StandFixture $first 0
    $resumed = Wait-Fixture 'restore'
    Assert-Recovery ($resumed.id -ne $first.id -and $resumed.checkpoint -eq 'confirmed-step-3') 'Abort with exit code zero lost the checkpoint or did not relaunch.'
    $results.Add([ordered]@{ check = 'abrupt-exit-with-zero-code'; restarted = $resumed.pid; checkpoint = $resumed.checkpoint })

    Stop-StandFixture $resumed 3221225477
    $safe = Wait-Fixture 'safe'
    Stop-StandFixture $safe 1
    Wait-QuietTask
    $results.Add([ordered]@{ check = 'bounded-restarts'; safe = $safe.pid })

    foreach ($mode in @('clean','update','windows-session','interrupted-exit')) {
        Invoke-RecoveryHelper $launch
        $current = Wait-Fixture 'normal'
        Send-FixtureCommand $current $mode
        if ($mode -eq 'interrupted-exit') {
            [void](Wait-Recovery { Test-Path -LiteralPath (Join-Path $profile "fixture-$($current.id).exit-started") } 'Exit intent was not announced.')
            Stop-StandFixture $current 1
        }
        Wait-QuietTask
        $results.Add([ordered]@{ check = $mode; app = $current.pid })
    }

    # A separately started copy can appear before the previous monitor releases its lock.
    [IO.File]::WriteAllText((Join-Path $profile 'settings.json'), '{"languageCode":"en","behavior":{"autoResumeBackgroundOperation":false}}')
    Invoke-RecoveryHelper $launch
    $old = Wait-Fixture 'normal'
    Send-FixtureCommand $old 'interrupted-exit'
    [void](Wait-Recovery { Test-Path -LiteralPath (Join-Path $profile "fixture-$($old.id).exit-started") } 'Exit intent was not announced before manual reopen.')
    $direct = Start-Process -FilePath $fixture -WindowStyle Hidden -PassThru
    $successor = Wait-Fixture 'normal'
    Assert-Recovery ($successor.pid -eq $direct.Id) 'The direct launcher started an unexpected instance.'
    Stop-StandFixture $old 1
    $eventFile = Get-ChildItem -LiteralPath (Join-Path $profile 'Recovery') -File -Recurse -Filter 'events.jsonl' | Select-Object -First 1
    [void](Wait-Recovery {
        Get-Content -LiteralPath $eventFile.FullName | ForEach-Object { $_ | ConvertFrom-Json -ErrorAction SilentlyContinue } |
            Where-Object { $_.action -eq 'watching' -and $_.pid -eq $successor.pid } | Select-Object -First 1
    } 'The new instance was not adopted after the previous monitor released ownership.')
    Stop-StandFixture $successor 0
    $afterManualReopen = Wait-Fixture 'restore'
    Send-FixtureCommand $afterManualReopen 'clean'
    Wait-QuietTask
    $direct.Dispose()
    $results.Add([ordered]@{ check = 'manual-reopen-overlaps-previous-monitor'; app = $successor.pid; restarted = $afterManualReopen.pid })

    $events = Get-Content -LiteralPath $eventFile.FullName | ForEach-Object { $_ | ConvertFrom-Json }
    foreach ($reason in @('UserExit','Update','WindowsSessionEnding')) {
        Assert-Recovery ([bool]($events | Where-Object { $_.action -eq 'expected_exit' -and $_.detail.reason -eq $reason })) "No confirmed exit logged for $reason."
    }
    Assert-Recovery ([bool]($events | Where-Object { $_.action -eq 'interrupted_exit' -and $_.detail.reason -eq 'UserExit' })) 'No interrupted Exit command logged.'
    $notificationAttempts = 0
    for ($i = 0; $i -lt $events.Count; $i++) {
        if ($events[$i].action -notlike 'restart_notification_*') { continue }
        Assert-Recovery ($i -gt 0 -and $i -lt $events.Count - 1 -and $events[$i - 1].action -eq 'unexpected_exit' -and
            $events[$i].pid -eq $events[$i - 1].pid -and $events[$i + 1].action -in @('restart','restart_safe')) 'A notification was attempted outside the automatic restart path.'
        $notificationAttempts++
    }
    Assert-Recovery ($notificationAttempts -eq 3) 'Each automatic restart should attempt exactly one Windows notification.'
    $results.Add([ordered]@{ check = 'notification-before-each-automatic-restart'; attempts = $notificationAttempts })
    $passed = $true
    $results | ConvertTo-Json -Depth 4
    "PASS: $($results.Count) crash recovery checks."
}
finally {
    if ($job) { $job.Dispose() }
    if ($caller -and !$caller.HasExited) { $caller.Kill(); [void]$caller.WaitForExit(10000) }
    if ($caller) { $caller.Dispose() }
    if ($taskName) {
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        if ($task -and @($task.Actions | Where-Object { $_.Execute.Trim('"') -eq $helper }).Count -eq 1) {
            Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
            Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
        }
    }
    foreach ($process in Get-Process -Name AIHub,LOPATA.Recovery -ErrorAction SilentlyContinue | Where-Object Path -In @($fixture,$helper)) {
        if (!$process.HasExited) { $process.Kill(); [void]$process.WaitForExit(10000) }
        $process.Dispose()
    }
    if ($passed -and !$KeepArtifacts) {
        $resolved = (Resolve-Path -LiteralPath $stand).Path
        Assert-Recovery ((Split-Path -Parent $resolved) -eq $temp -and (Split-Path -Leaf $resolved) -match '^LOPATA-CrashRecovery-Test-[a-f0-9]{32}$') 'Unsafe test cleanup path.'
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $stand) { Write-Host "Crash recovery test artifacts: $stand" }
}
