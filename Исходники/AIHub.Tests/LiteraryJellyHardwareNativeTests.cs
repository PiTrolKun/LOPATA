using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryJellyHardwareNativeTests
{
    [TestMethod]
    public async Task PythonHardwarePolicyRejectsBadWeightsAndProbesEveryDevice()
    {
        if (!System.IO.File.Exists(GigaEmbeddingInstallation.Python)) Assert.Inconclusive("Embedded Python is not installed.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await GigaEmbeddingInstallation.RunAsync([
            System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hardware", "test_runtime_hardware.py"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Tools")], TestContext.WriteLine, timeout.Token);
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
