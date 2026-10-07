using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PythonGpuStagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PinnedRocmArchivesAssembleAndProbeInAnIndependentStage() =>
        await PinnedGpuArchivesAssembleAndProbeInAnIndependentStage("Rocm721");

    [TestMethod]
    public async Task TrustedRocmManifestAndProductionHardwareProbeAgree() =>
        await TrustedGpuManifestsAndProductionHardwareProbeAgree("Rocm721");

    [TestMethod]
    [DataRow("Cuda126")]
    [DataRow("Xpu")]
    [DataRow("Cuda128")]
    public async Task TrustedGpuManifestsAndProductionHardwareProbeAgree(string name)
    {
        var stage = Environment.GetEnvironmentVariable("AIHUB_PYTHON_STAGE_" + name.ToUpperInvariant());
        if (string.IsNullOrWhiteSpace(stage)) Assert.Inconclusive("Explicit isolated GPU stage required.");
        var profile = Enum.Parse<PythonRuntimeProfile>(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await PythonRuntimeBundleVerifier.VerifyAsync(profile, stage, timeout.Token);
        var probe = await ManagedPythonHardwareProbe.ReadAsync(profile, stage, "auto", timeout.Token);
        Assert.IsTrue(probe.FreeBytes > 0);
        if (profile is PythonRuntimeProfile.Cuda126 or PythonRuntimeProfile.Cuda128 && Environment.GetEnvironmentVariable("AIHUB_EXPECT_CUDA") == "1")
        {
            Assert.StartsWith("cuda:", probe.Device);
            Assert.IsNotNull(probe.Devices);
            Assert.IsTrue(probe.Devices.Count > 0);
            Assert.IsTrue(probe.Devices.All(row => !string.IsNullOrWhiteSpace(row.Name) && row.FreeBytes > 0));
            Assert.IsTrue(probe.Devices.Any(row => row.Device == probe.Device));
            var explicitDevice = await ManagedPythonHardwareProbe.ReadAsync(profile, stage, probe.Device, timeout.Token);
            Assert.AreEqual(probe.Device, explicitDevice.Device);
            await Assert.ThrowsExactlyAsync<PythonHardwareProbeException>(() =>
                ManagedPythonHardwareProbe.ReadAsync(profile, stage, "cuda:99", timeout.Token));
        }
        await PythonRuntimeBundleVerifier.VerifyAsync(profile, stage, timeout.Token);
        TestContext.WriteLine(JsonSerializer.Serialize(probe));
    }

    [TestMethod]
    [DataRow("Cuda126")]
    [DataRow("Xpu")]
    [DataRow("Cuda128")]
    public async Task PinnedGpuArchivesAssembleAndProbeInAnIndependentStage(string name)
    {
        var cpu = Environment.GetEnvironmentVariable("AIHUB_PYTHON_WHEEL_CACHE");
        var gpu = Environment.GetEnvironmentVariable("AIHUB_PYTHON_GPU_WHEEL_CACHE");
        var bootstrap = Environment.GetEnvironmentVariable("AIHUB_PYTHON_BOOTSTRAP_CACHE");
        var msvc = Environment.GetEnvironmentVariable("AIHUB_PYTHON_MSVC_SOURCE");
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        if (new[] { cpu, gpu, bootstrap, msvc, stand }.Any(string.IsNullOrWhiteSpace))
            Assert.Inconclusive("Explicit pinned archives and independent staging paths are required.");
        var profile = Enum.Parse<PythonRuntimeProfile>(name);
        var cache = PythonBootstrapArtifacts.DownloadSet(profile).ToDictionary(row => row.FileName, row =>
            row.Name == "python" ? Path.Combine(bootstrap!, "python.zip") : row.Name == "pip"
                ? Path.Combine(bootstrap!, "pip.whl") : File.Exists(Path.Combine(gpu!, row.FileName))
                    ? Path.Combine(gpu!, row.FileName) : Path.Combine(cpu!, row.FileName));
        var stage = Path.GetFullPath(Path.Combine(stand!, "python-gpu-stages", name + "-" + Guid.NewGuid().ToString("N") + ".installing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // This stage is a research artifact; no component state or user license receipts are modified.
        await PythonRuntimeStaging.PrepareAsync(profile, stage, cache, msvc!, timeout.Token);
        await PythonRuntimeHealthProbe.VerifyAsync(profile, stage, timeout.Token);
        Assert.IsFalse(Directory.EnumerateDirectories(stage, "__pycache__", SearchOption.AllDirectories).Any());
        var files = new List<object>();
        foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var data = File.OpenRead(file);
            files.Add(new { path = Path.GetRelativePath(stage, file).Replace('\\', '/'), size = data.Length,
                sha256 = Convert.ToHexString(await SHA256.HashDataAsync(data, timeout.Token)).ToLowerInvariant() });
        }
        var manifest = Path.Combine(Path.GetDirectoryName(stage)!, name + "-files.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { files }, new JsonSerializerOptions { WriteIndented = true }) + "\n", timeout.Token);
        var policy = Path.Combine(AppContext.BaseDirectory, "Tools", "runtime_hardware.py");
        var script = "import torch,json,runpy; p=runpy.run_path(" + JsonSerializer.Serialize(policy) + "); "
            + "device,memory,reason=p['select_torch_device'](torch,'auto'); "
            + "print(json.dumps({'torch':torch.__version__,'device':device,'memory':memory,'reason':reason,"
            + "'cuda':torch.version.cuda,'architectures':torch.cuda.get_arch_list() if torch.version.cuda else []}))";
        var info = new ProcessStartInfo(Path.Combine(stage, "python.exe"))
        {
            WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        foreach (var argument in new[] { "-I", "-B", "-c", ManagedPythonLaunch.Prelude + script }) info.ArgumentList.Add(argument);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Test.PythonGpuProfile");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            Assert.AreEqual(0, process.ExitCode, await errors);
            using var report = JsonDocument.Parse(await output);
            Assert.AreEqual(profile.TorchVersion(), report.RootElement.GetProperty("torch").GetString());
            if (profile is PythonRuntimeProfile.Cuda126 or PythonRuntimeProfile.Cuda128 && Environment.GetEnvironmentVariable("AIHUB_EXPECT_CUDA") == "1")
                Assert.StartsWith("cuda:", report.RootElement.GetProperty("device").GetString()!);
            await File.WriteAllTextAsync(stage + ".probe.json", await output, timeout.Token);
            TestContext.WriteLine(stage + "\n" + await output + "\nManifest: " + manifest);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
