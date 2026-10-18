using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryJellyHardwareNativeTests
{
    [TestMethod, Timeout(300_000)]
    public async Task PythonHardwarePolicyRejectsBadWeightsAndProbesEveryDevice()
    {
        if (!System.IO.File.Exists(GigaEmbeddingInstallation.Python)) Assert.Inconclusive("Embedded Python is not installed.");
        // The policy fixture itself is tiny. Full pinned-tree verification is a separate disk-bound stage.
        var phase = Stopwatch.StartNew();
        TestContext.WriteLine("phase=resolve_cpu; budget_seconds=180");
        using (var prepare = new CancellationTokenSource(TimeSpan.FromMinutes(3))) {
            var actual = await ManagedPythonRuntime.ResolveAsync("cpu", prepare.Token,
                log: line => TestContext.WriteLine($"prepare_elapsed_seconds={phase.Elapsed.TotalSeconds:F3}; {line}"));
            Assert.AreEqual("cpu", actual.Device);
            Assert.AreEqual(GigaEmbeddingInstallation.Python, actual.Python);
        }
        TestContext.WriteLine($"phase=resolve_cpu; elapsed_seconds={phase.Elapsed.TotalSeconds:F3}");
        phase.Restart();
        TestContext.WriteLine("phase=policy_child; budget_seconds=30");
        var info = new ProcessStartInfo(GigaEmbeddingInstallation.Python) {
            WorkingDirectory = GigaEmbeddingInstallation.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        ManagedPythonLaunch.ScriptArguments(info, [
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hardware", "test_runtime_hardware.py"),
            Path.Combine(AppContext.BaseDirectory, "Tools")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = OwnedProcessRegistry.Shared.Start(info, "Tests.PythonHardwarePolicy");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync(timeout.Token);
            TestContext.WriteLine(await output); TestContext.WriteLine(await error);
            Assert.AreEqual(0, process.ExitCode, "Original Python hardware policy assertions failed.");
        }
        finally {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            TestContext.WriteLine($"phase=policy_child; elapsed_seconds={phase.Elapsed.TotalSeconds:F3}; exited={process.HasExited}");
        }
    }

    [TestMethod]
    public async Task CancellationRetiresOwnedWorker()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_JELLY_HARDWARE_NATIVE") != "1") Assert.Inconclusive("Native test is opt-in.");
        var worker = await LiteraryJellyWorker.CreateAsync("gliner", "unused", LiteraryJellyInstallation.Dependencies("gliner"), (_, _) => { }, "cpu", CancellationToken.None);
        var pid = worker.Id;
        try
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker.ReadAsync(cancelled.Token));
        }
        finally { await worker.DisposeAsync(); }
        Assert.ThrowsExactly<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [TestMethod]
    [DataRow("gliner")]
    [DataRow("nuextract")]
    public async Task CpuAndAutomaticDevicesProduceResultsAndExit(string mode)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_JELLY_HARDWARE_NATIVE") != "1")
            Assert.Inconclusive("Opt-in test requires locally verified specialist weights and dependencies.");
        var model = await LiteraryJellyInstallation.FindModelAsync(mode, CancellationToken.None);
        var deps = await LiteraryJellyInstallation.FindDependenciesAsync(mode, CancellationToken.None);
        Assert.IsNotNull(model); Assert.IsNotNull(deps);
        foreach (var policy in new[] { "cpu", "auto" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            int pid;
            await using (var worker = await LiteraryJellyWorker.CreateAsync(mode, model, deps, (_, _) => { }, policy, timeout.Token))
            {
                pid = worker.Id;
                var device = await worker.ReadAsync(timeout.Token);
                Assert.AreEqual("device", device.GetProperty("type").GetString());
                if (policy == "cpu") Assert.AreEqual("cpu", device.GetProperty("backend").GetString());
                if (policy == "auto" && Environment.GetEnvironmentVariable("AIHUB_JELLY_EXPECT_GPU") == "1")
                    Assert.AreEqual("cuda", device.GetProperty("backend").GetString(), device.ToString());
                var loaded = await worker.CallAsync(new { action = "load" }, timeout.Token);
                Assert.AreEqual("loaded", loaded.GetProperty("type").GetString());
                using var output = JsonDocument.Parse(await worker.ExtractAsync("Пётр открыл дверь. Мария осталась в саду.", timeout.Token));
                Assert.AreEqual(JsonValueKind.Array, output.RootElement.GetProperty("facts").ValueKind);
                // This smoke test verifies execution/protocol, not factual quality.
                TestContext.WriteLine($"{policy}: {device}; {loaded}; {output.RootElement}");
            }
            Assert.ThrowsExactly<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
