using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class HardwareReadinessTests
{
    [TestMethod]
    public async Task StartupUsesLayoutWhileFullVerificationStillRejectsAlteredContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-readiness-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hardware", "llama-cpu-runtime-manifest.json");
            File.Copy(fixture, Path.Combine(runtime, "runtime-manifest.json"));
            using var manifest = JsonDocument.Parse(File.ReadAllText(fixture));
            // Correct sizes with deliberately invalid bytes: inventory may see the bundle,
            // but no execution path may regard this as verified content.
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = Path.Combine(runtime, file.GetProperty("path").GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = File.Create(path);
                output.SetLength(file.GetProperty("size").GetInt64());
            }
            var entry = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
            var store = new ComponentStateStore(Path.Combine(root, "state.json"));
            store.Save(new() { Components = [new() { ComponentId = entry.Id, Version = entry.Version,
                Status = ComponentInstallStatuses.Installed, InstallPath = runtime }] });
            var preparation = new HardwareRuntimePreparation(new ComponentManager(store));
            var ready = await preparation.CheckReadinessAsync([], CancellationToken.None);
            Assert.IsTrue(ready.Items.Single(item => item.ComponentId == entry.Id).AlreadyAvailable);
            Assert.Contains(HardwareRuntimeCatalog.PythonCpuId, ready.Items.Select(item => item.ComponentId).ToArray());
            Assert.Contains(HardwareRuntimeCatalog.SdCpuId, ready.Items.Select(item => item.ComponentId).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                HardwareRuntimeBundleVerifier.VerifyFilesAsync(entry.Id, runtime, CancellationToken.None));
            File.Delete(Path.Combine(runtime, entry.HealthCheckRelativePath));
            ready = await preparation.CheckReadinessAsync([], CancellationToken.None);
            Assert.IsFalse(ready.Items.Single(item => item.ComponentId == entry.Id).AlreadyAvailable);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CanceledReadinessDoesNotCreateComponentState()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-readiness-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "state.json");
        var preparation = new HardwareRuntimePreparation(new ComponentManager(new ComponentStateStore(state)));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            preparation.CheckReadinessAsync([], new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(root));
    }
}
