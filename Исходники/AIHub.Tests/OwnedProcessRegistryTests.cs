using AIHub.Services;
using System.Diagnostics;

namespace AIHub.Tests;

[TestClass]
public sealed class OwnedProcessRegistryTests
{
    private static ProcessStartInfo Command(string command) => new("cmd.exe")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        ArgumentList = { "/d", "/c", command }
    };

    [TestMethod]
    public async Task DisposalStopsOwnedProcessButNotExternalProcess()
    {
        using var external = Process.Start(Command("ping -n 60 127.0.0.1 >nul"))!;
        using var registry = new OwnedProcessRegistry();
        using var child = registry.Start(Command("ping -n 60 127.0.0.1 >nul"), "test");
        try
        {
            Assert.AreEqual(child.Id, registry.GetSnapshot().Single().Pid);
            registry.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
            Assert.IsFalse(external.HasExited);
        }
        finally { if (!external.HasExited) { external.Kill(true); await external.WaitForExitAsync(); } }
    }

    [TestMethod]
    public async Task RapidExitKeepsOutputAndRemovesTracking()
    {
        using var registry = new OwnedProcessRegistry();
        for (var i = 0; i < 30; i++)
        {
            using var child = registry.Start(Command("echo owned-output"), "short probe");
            Assert.AreEqual("owned-output", (await child.StandardOutput.ReadToEndAsync()).Trim());
            await child.WaitForExitAsync();
        }
        Assert.AreEqual(0, registry.GetSnapshot().Count);
    }

    [TestMethod]
    public void StartFailureLeavesNoWorker()
    {
        using var registry = new OwnedProcessRegistry();
        Assert.Throws<System.ComponentModel.Win32Exception>(() => registry.Start(new ProcessStartInfo("missing-" + Guid.NewGuid() + ".exe") { UseShellExecute = false }, "missing"));
        Assert.AreEqual(0, registry.GetSnapshot().Count);
    }

    [TestMethod]
    public void ShellAndRetiredOwnerCannotStartWorkers()
    {
        using var registry = new OwnedProcessRegistry();
        Assert.Throws<ArgumentException>(() => registry.Start(new ProcessStartInfo("anything") { UseShellExecute = true }, "external"));
        registry.Dispose();
        Assert.Throws<ObjectDisposedException>(() => registry.Start(Command("echo forbidden"), "retired"));
    }

    [TestMethod]
    public async Task QdrantDoesNotStartOrCreateStorageAtConstruction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-qdrant-" + Guid.NewGuid().ToString("N"));
        var runtime = new QdrantRuntime(new QdrantOptions { DataDirectory = directory });
        Assert.IsNull(runtime.Pid);
        Assert.AreEqual("Stopped", runtime.State);
        Assert.IsFalse(Directory.Exists(directory));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.StartAsync(cancelled.Token));
        Assert.IsFalse(Directory.Exists(directory));
        await runtime.ShutdownAsync();
    }
}
