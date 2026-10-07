using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PythonComponentInstallationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PreparedDeviceInventoryContainsOnlyProbedDevicesAndNeverFallsBackForExplicitMissingGpu()
    {
        var isolated = Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT");
        if (string.IsNullOrWhiteSpace(isolated) || AppDataPaths.ProjectRoot is not null
            || !ManagedModelPathIdentity.SameDirectory(isolated, AppDataPaths.BaseDirectory))
            Assert.Inconclusive("Prepared isolated UPDATE_STAND required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var priorGate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            var devices = await ManagedPythonRuntime.ListGpuDevicesAsync(timeout.Token);
            TestContext.WriteLine(JsonSerializer.Serialize(devices));
            if (Environment.GetEnvironmentVariable("AIHUB_EXPECT_CUDA") == "1")
            {
                Assert.IsTrue(devices.Any(row => row.Device.StartsWith("cuda:", StringComparison.Ordinal)));
                Assert.IsTrue(devices.All(row => row.FreeBytes > 0 && !string.IsNullOrWhiteSpace(row.Name)));
            }
            foreach (var policy in new[] { "hip:99", "xpu:99", "cuda:99" })
                await Assert.ThrowsExactlyAsync<PythonHardwareProbeException>(() => ManagedPythonRuntime.ResolveAsync(policy, timeout.Token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ManagedPythonRuntime.ListGpuDevicesAsync(cancelled.Token));
        }
        finally { ComponentLicenseGate.ConfirmAsync = priorGate; }
    }

    [TestMethod]
    public async Task RocmProfileUsesTheOrdinaryInstallerAndManagedDeviceSelection() =>
        await GpuProfilesUseTheOrdinaryInstallerAndManagedDeviceSelection("Rocm721");

    [TestMethod]
    [DataRow("Cuda126")]
    [DataRow("Xpu")]
    [DataRow("Cuda128")]
    public async Task GpuProfilesUseTheOrdinaryInstallerAndManagedDeviceSelection(string name)
    {
        var cpu = Environment.GetEnvironmentVariable("AIHUB_PYTHON_WHEEL_CACHE");
        var gpu = Environment.GetEnvironmentVariable("AIHUB_PYTHON_GPU_WHEEL_CACHE");
        var bootstrap = Environment.GetEnvironmentVariable("AIHUB_PYTHON_BOOTSTRAP_CACHE");
        var msvcSource = Environment.GetEnvironmentVariable("AIHUB_PYTHON_MSVC_SOURCE");
        var isolated = Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT");
        if (new[] { cpu, gpu, bootstrap, msvcSource, isolated }.Any(string.IsNullOrWhiteSpace)
            || AppDataPaths.ProjectRoot is not null || !ManagedModelPathIdentity.SameDirectory(isolated!, AppDataPaths.BaseDirectory))
            Assert.Inconclusive("Explicit isolated UPDATE_STAND inputs are required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var manager = new ComponentManager();
        var dependency = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
        var msvc = manager.GetInstallDirectory(dependency);
        if (!Directory.Exists(msvc))
        {
            foreach (var file in Directory.EnumerateFiles(msvcSource!, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(msvc, Path.GetRelativePath(msvcSource!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            new ComponentStateStore().Save(new() { Components = [new()
            { ComponentId = dependency.Id, Version = dependency.Version, InstallPath = msvc, Status = ComponentInstallStatuses.Installed }] });
        }
        var kind = Enum.Parse<PythonRuntimeProfile>(name);
        var id = kind switch
        {
            PythonRuntimeProfile.Cuda126 => HardwareRuntimeCatalog.PythonCudaId,
            PythonRuntimeProfile.Cuda128 => HardwareRuntimeCatalog.PythonCuda128Id,
            PythonRuntimeProfile.Rocm721 => HardwareRuntimeCatalog.PythonRocmId,
            _ => HardwareRuntimeCatalog.PythonXpuId
        };
        var priorGate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            var licenses = new HashSet<string>();
            ComponentLicenseGate.ConfirmAsync = (ids, token) =>
            { token.ThrowIfCancellationRequested(); foreach (var license in ids) licenses.Add(license); return Task.CompletedTask; };
            foreach (var selected in new[] { PythonRuntimeProfile.Cpu, kind })
            {
                var entry = ComponentCatalog.Find(selected == PythonRuntimeProfile.Cpu ? HardwareRuntimeCatalog.PythonCpuId : id)!;
                var cache = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
                Directory.CreateDirectory(cache);
                foreach (var row in PythonBootstrapArtifacts.DownloadSet(selected))
                {
                    var target = Path.Combine(cache, row.FileName);
                    if (File.Exists(target)) continue;
                    var source = row.Name == "python" ? Path.Combine(bootstrap!, "python.zip") : row.Name == "pip"
                        ? Path.Combine(bootstrap!, "pip.whl") : File.Exists(Path.Combine(gpu!, row.FileName))
                            ? Path.Combine(gpu!, row.FileName) : Path.Combine(cpu!, row.FileName);
                    File.Copy(source, target);
                }
                Assert.IsTrue((await manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token)).IsAvailable);
            }
            Assert.Contains(kind == PythonRuntimeProfile.Cuda126 ? "runtime.python-cuda"
                : kind == PythonRuntimeProfile.Cuda128 ? "runtime.python-cuda128"
                : kind == PythonRuntimeProfile.Rocm721 ? "runtime.python-rocm721" : "runtime.python-xpu", licenses);
            var selection = await ManagedPythonRuntime.ResolveAsync("auto", timeout.Token);
            if (Environment.GetEnvironmentVariable("AIHUB_EXPECT_CUDA") == "1"
                && (kind is PythonRuntimeProfile.Cuda126 or PythonRuntimeProfile.Cuda128
                    || manager.GetStatus().Any(item => item.Entry.Id == HardwareRuntimeCatalog.PythonCudaId && item.IsAvailable)))
                Assert.StartsWith("cuda:", selection.Device);
            var processor = await ManagedPythonRuntime.ResolveAsync("cpu", timeout.Token);
            Assert.AreEqual(HardwareRuntimeCatalog.PythonCpuId, processor.Entry.Id);
            Assert.AreEqual("cpu", processor.Device);
            // No generated bytecode or other runtime mutations during execution selection.
            foreach (var selectedId in new[] { HardwareRuntimeCatalog.PythonCpuId, id })
                await PythonRuntimeBundleVerifier.VerifyAsync(HardwareRuntimeCatalog.PythonProfile(selectedId),
                    manager.GetInstallDirectory(ComponentCatalog.Find(selectedId)!), timeout.Token);
            TestContext.WriteLine(JsonSerializer.Serialize(new { selection.Entry.Id, selection.Device, selection.FreeBytes }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = priorGate; }
    }

    [TestMethod]
    public async Task CpuProfileIsAtomicResumableLicenseGatedAndVerifiedInFirstLaunchPlan()
    {
        var source = Environment.GetEnvironmentVariable("AIHUB_PYTHON_WHEEL_CACHE");
        var bootstrap = Environment.GetEnvironmentVariable("AIHUB_PYTHON_BOOTSTRAP_CACHE");
        var msvcSource = Environment.GetEnvironmentVariable("AIHUB_PYTHON_MSVC_SOURCE");
        var isolatedRoot = Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(bootstrap)
            || string.IsNullOrWhiteSpace(msvcSource) || string.IsNullOrWhiteSpace(isolatedRoot)
            || AppDataPaths.ProjectRoot is not null || !ManagedModelPathIdentity.SameDirectory(isolatedRoot, AppDataPaths.BaseDirectory))
            Assert.Inconclusive("An UPDATE_STAND build and explicit isolated inputs are required; production state is never modified.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var manager = new ComponentManager();
        var entry = ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCpuId)!;
        var directory = manager.GetInstallDirectory(entry);
        Assert.IsFalse(Directory.Exists(directory));
        var priorGate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => throw new InvalidOperationException("Test license refusal.");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token));
            Assert.IsFalse(Directory.Exists(directory));
            Assert.IsFalse(File.Exists(AppDataPaths.ComponentStatePath));

            var llama = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
            var msvc = manager.GetInstallDirectory(llama);
            foreach (var file in Directory.EnumerateFiles(msvcSource, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(msvc, Path.GetRelativePath(msvcSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            new ComponentStateStore().Save(new() { Components = [new()
            { ComponentId = llama.Id, Version = llama.Version, InstallPath = msvc, Status = ComponentInstallStatuses.Installed }] });
            var cache = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
            Directory.CreateDirectory(cache);
            foreach (var artifact in PythonBootstrapArtifacts.CpuDownloadSet())
                File.Copy(Path.Combine(artifact.Name is "python" or "pip" ? bootstrap : source,
                    artifact.Name == "python" ? "python.zip" : artifact.Name == "pip" ? "pip.whl" : artifact.FileName),
                    Path.Combine(cache, artifact.FileName));
            string[]? requestedLicenses = null;
            // Isolated host policy only: no real license receipt is written or changed.
            ComponentLicenseGate.ConfirmAsync = (ids, token) =>
            { token.ThrowIfCancellationRequested(); requestedLicenses = ids.ToArray(); return Task.CompletedTask; };
            var installed = await manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token);
            Assert.IsTrue(installed.IsAvailable);
            CollectionAssert.AreEquivalent(new[] { "runtime.llama-engine", "runtime.llama-msvc",
                "runtime.python-cpu", "runtime.python-intel-openmp" }, requestedLicenses!);
            Assert.IsFalse(requestedLicenses!.Contains("native.cuda"));
            Assert.AreEqual(entry.DownloadSizeBytes, installed.Record.DownloadedBytes);
            Assert.AreEqual(entry.Sha256, installed.Record.ComputedSha256);
            var priorVerification = installed.Record.VerifiedAt;
            await PythonRuntimeBundleVerifier.VerifyCpuAsync(directory, timeout.Token);
            await PythonRuntimeHealthProbe.VerifyCpuAsync(directory, timeout.Token);

            using var cancellation = new CancellationTokenSource();
            var cancelAtVerification = new InlineProgress<ComponentDownloadProgress>(value =>
            { if (value.Stage == ComponentInstallStatuses.NeedsVerification) cancellation.Cancel(); });
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => manager.DownloadAndInstallAsync(
                entry.Id, cancelAtVerification, cancellation.Token, forceReinstall: true));
            var retained = manager.GetStatus().Single(item => item.Entry.Id == entry.Id);
            Assert.IsTrue(retained.IsAvailable);
            Assert.AreEqual(priorVerification, retained.Record.VerifiedAt);
            Assert.IsFalse(Directory.Exists(directory + ".installing"));
            Assert.IsFalse(Directory.Exists(directory + ".previous"));

            var extra = Path.Combine(directory, "unexpected.py");
            File.WriteAllText(extra, "unexpected");
            try
            {
                var plan = await new HardwareRuntimePreparation(manager).CheckAsync([], timeout.Token);
                Assert.IsFalse(plan.Items.Single(item => item.ComponentId == entry.Id).AlreadyAvailable);
            }
            finally { File.Delete(extra); }
            var readyPlan = await new HardwareRuntimePreparation(manager).CheckAsync([], timeout.Token);
            Assert.IsTrue(readyPlan.Items.Single(item => item.ComponentId == entry.Id).AlreadyAvailable);

            // Interrupted directory swap: a damaged new directory must not destroy the retained old one.
            Directory.Move(directory, directory + ".previous"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "python.exe"), "partial");
            var recovered = await manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token, forceReinstall: true);
            Assert.IsTrue(recovered.IsAvailable);
            Assert.IsFalse(Directory.Exists(directory + ".previous"));
            await PythonRuntimeBundleVerifier.VerifyCpuAsync(directory, timeout.Token);
            TestContext.WriteLine("Composite installer, license refusal, 38 cached digests, full receipt/health, cancellation and interrupted replacement passed in " + isolatedRoot);
        }
        finally { ComponentLicenseGate.ConfirmAsync = priorGate; }
    }
}
