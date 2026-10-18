using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class HardwareReadinessTests
{
    [TestMethod]
    public async Task LegacyComponentInventoryUsesLayoutWhileExecutionRejectsAlteredContent()
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
            // This fixture is the original 001 component-manager contract. New
            // packaged engine sets correctly do not acquire these legacy IDs.
            // Keep the inventory-versus-execution integrity assertion explicit.
            var manager = new ComponentManager(store);
            ComponentAcquisitionPlan Inventory() => manager.BuildPlanForComponents(
                [HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.SdCpuId, HardwareRuntimeCatalog.PythonCpuId], "Legacy 001 inventory fixture");
            var ready = Inventory();
            Assert.IsTrue(ready.Items.Single(item => item.ComponentId == entry.Id).AlreadyAvailable);
            Assert.Contains(HardwareRuntimeCatalog.PythonCpuId, ready.Items.Select(item => item.ComponentId).ToArray());
            Assert.Contains(HardwareRuntimeCatalog.SdCpuId, ready.Items.Select(item => item.ComponentId).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                HardwareRuntimeBundleVerifier.VerifyFilesAsync(entry.Id, runtime, CancellationToken.None));
            File.Delete(Path.Combine(runtime, entry.HealthCheckRelativePath));
            ready = Inventory();
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
