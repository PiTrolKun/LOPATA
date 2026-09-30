using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class BackgroundProcessTests
{
    [TestMethod]
    public async Task PauseConfirmsOwnedProcessExitAndLeavesOtherProcessAlive()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-background-process-" + Guid.NewGuid().ToString("N"));
        using var process = Probe(); using var other = Probe();
        try
        {
            Assert.AreEqual("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("READY", await other.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var controller = new BackgroundOperationController(new(Path.Combine(root, "operation.json")));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0;
            Task Retire()
            {
                var type = typeof(BackgroundOperationController).Assembly.GetType("AIHub.Services.ModelProcessRetirement")!;
                return (Task)type.GetMethod("StopAsync")!.Invoke(null, [process, (Action)(() => { if (!process.HasExited) process.Kill(); })])!;
            }
            var work = controller.RunAsync(new BackgroundOperationState
                { Kind = "test.process", Title = "Process probe", Input = JsonSerializer.SerializeToElement("probe") },
                async ct => { if (++attempts > 1) return 42; started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; },
                Retire, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.IsTrue(process.HasExited); Assert.IsFalse(other.HasExited);
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            await controller.ResumeAsync(CancellationToken.None); Assert.AreEqual(42, await work.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            foreach (var probe in new[] { process, other }) { if (!probe.HasExited) probe.Kill(); await probe.WaitForExitAsync(); }
            var path = Path.GetFullPath(root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("lopata-background-process-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
    private static Process Probe()
    {
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$probeMemory = [byte[]]::new(64MB); for ($probeOffset = 0; $probeOffset -lt $probeMemory.Length; $probeOffset += 4096) { $probeMemory[$probeOffset] = 1 }; Write-Output READY; [Threading.Thread]::Sleep(60000)");
        return Process.Start(start) ?? throw new IOException("Could not start the owned retirement probe.");
    }
}
