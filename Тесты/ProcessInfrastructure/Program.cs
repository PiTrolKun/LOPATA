using AIHub.Services;
using System.Diagnostics;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("mode output-directory");
var mode = args[0];
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
if (mode == "ui") { WpfProbe.Run(output); return; }
if (mode == "child")
{
    await File.WriteAllTextAsync(Path.Combine(output, "child.txt"), Environment.ProcessId.ToString());
    using var grandchild = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "sleep", output } })!;
    await File.WriteAllTextAsync(Path.Combine(output, "grandchild.txt"), grandchild.Id.ToString());
    await Task.Delay(Timeout.Infinite);
    return;
}
if (mode == "sleep") { await Task.Delay(Timeout.Infinite); return; }
if (mode == "owner")
{
    using var child = OwnedProcessRegistry.Shared.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "child", output } }, "Crash lifecycle child");
    await Task.Delay(Timeout.Infinite);
    return;
}
if (mode == "lifecycle")
{
    using var foreign = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "sleep", output } })!;
    using var owner = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "owner", output } })!;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var childPath = Path.Combine(output, "child.txt"); var grandchildPath = Path.Combine(output, "grandchild.txt");
        while (!File.Exists(childPath) || !File.Exists(grandchildPath)) await Task.Delay(50, timeout.Token);
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPath, timeout.Token)));
        using var grandchild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(grandchildPath, timeout.Token)));
        _ = child.SafeHandle; _ = grandchild.SafeHandle;
        owner.Kill(); // Kill only the owner: Windows must clean its job tree.
        await Task.WhenAll(owner.WaitForExitAsync(timeout.Token), child.WaitForExitAsync(timeout.Token), grandchild.WaitForExitAsync(timeout.Token));
        if (foreign.HasExited) throw new Exception("Foreign process was terminated.");
        await File.WriteAllTextAsync(Path.Combine(output, "lifecycle-result.json"), JsonSerializer.Serialize(new { owner = owner.Id, child = child.Id, grandchild = grandchild.Id, foreign = foreign.Id, ownedTreeTerminated = true, foreignSurvived = true }));
    }
    finally
    {
        if (!owner.HasExited) owner.Kill(true);
        if (!foreign.HasExited) { foreign.Kill(true); await foreign.WaitForExitAsync(); }
    }
    return;
}
if (mode == "qdrant")
{
    var options = new QdrantOptions { DataDirectory = Path.Combine(output, "данные памяти"), HelperExecutable = Path.Combine(AppDataPaths.ProjectRoot!, "Исходники", "AIHub", "bin", "Debug", "net10.0-windows", "AIHub.exe") };
    var runtime = new QdrantRuntime(options);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        await QdrantInstaller.InstallAsync(options, null, timeout.Token);
        if (runtime.Pid is not null) throw new Exception("Unexpected auto-start.");
        await Task.WhenAll(runtime.StartAsync(timeout.Token), runtime.StartAsync(timeout.Token));
        var pid = runtime.Pid;
        var contender = new QdrantRuntime(options);
        var secondOwnerRejected = false;
        try { await contender.StartAsync(timeout.Token); }
        catch (IOException) { secondOwnerRejected = true; }
        finally { await contender.ShutdownAsync(); }
        if (!secondOwnerRejected || runtime.Pid != pid) throw new Exception("Data ownership isolation failed.");
        var result = await runtime.ProbeAsync(timeout.Token);
        if (!result.GracefulRestart) throw new Exception("Graceful restart failed.");
        await runtime.StopAsync();
        if (!runtime.LastStopGraceful) throw new Exception("Graceful final shutdown failed.");
        await runtime.StartAsync(timeout.Token);
        using (var child = Process.GetProcessById(runtime.Pid!.Value)) { child.Kill(); await child.WaitForExitAsync(timeout.Token); }
        await runtime.StartAsync(timeout.Token);
        var recovered = runtime.IsReady;
        await runtime.StopAsync();
        var impatient = new QdrantRuntime(options with { StartupTimeout = TimeSpan.FromMilliseconds(1) });
        var timedOut = false;
        try { await impatient.StartAsync(timeout.Token); }
        catch (OperationCanceledException) { timedOut = true; }
        finally { await impatient.ShutdownAsync(); }
        if (!timedOut || impatient.Pid is not null) throw new Exception("Startup timeout cleanup failed.");
        await runtime.StartAsync(timeout.Token); // File lease was released after timed-out startup.
        await runtime.StopAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "qdrant-result.json"), JsonSerializer.Serialize(new { result, firstPid = pid, recovered, secondOwnerRejected, startupTimeoutCleaned = timedOut, finalPid = runtime.Pid }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally { await runtime.ShutdownAsync(); OwnedProcessRegistry.Shared.Dispose(); }
    return;
}
throw new ArgumentException("Unknown mode.");
