using System.IO;
using System.Diagnostics;
using AIHub.Services;
using AIHub.Models;

namespace AIHub.Tests;

[TestClass]
public sealed class SdHardwareNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ExplicitOfflineVerificationRestoresExistingCards()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_SD_REVERIFY_EXISTING") != "1")
            Assert.Inconclusive("Explicit local verification only; never downloads models.");
        var root = Environment.GetEnvironmentVariable("AIHUB_SD_HARDWARE_MODELS");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));
        using var installation = new ImageGenerationInstallation();
        foreach (var modelId in new[] { "z-image", "krea" })
        {
            var cards = await installation.CheckAsync(Path.GetFullPath(root), modelId, null, CancellationToken.None);
            Assert.IsTrue(cards.All(card => card.Status == ManagedModelStatuses.Installed),
                string.Join(";", cards.Select(card => card.ModelArtifactId + ":" + card.Status)));
            Assert.IsTrue(installation.IsReady(root.Replace('\\', '/'), modelId));
        }
    }

    [TestMethod]
    [DataRow("cpu", "z-image")]
    [DataRow("vulkan", "z-image")]
    [DataRow("cpu", "krea")]
    [DataRow("vulkan", "krea")]
    public async Task PinnedBackendGeneratesCheckedImage(string backend, string modelId)
    {
        var stand = Environment.GetEnvironmentVariable("AIHUB_SD_HARDWARE_STAND");
        var modelRoot = Environment.GetEnvironmentVariable("AIHUB_SD_HARDWARE_MODELS");
        if (string.IsNullOrWhiteSpace(stand) || string.IsNullOrWhiteSpace(modelRoot))
            Assert.Inconclusive("Requires explicitly downloaded pinned runtime and previously verified scenario weights.");
        var runtime = Path.Combine(stand, "sd-packages", "payload-" + backend);
        var executable = Directory.EnumerateFiles(runtime, "sd-cli.exe", SearchOption.AllDirectories).Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var version = await RuntimeDeviceProbe.RunAsync(executable, "--version", timeout.Token);
        Assert.IsTrue(version.Contains("3f8527a", StringComparison.OrdinalIgnoreCase), version);
        TestContext.WriteLine(version);
        var devices = await RuntimeDeviceProbe.RunAsync(executable, "--list-devices", timeout.Token);
        TestContext.WriteLine(devices);
        if (backend == "vulkan") Assert.IsTrue(devices.Contains("Vulkan", StringComparison.OrdinalIgnoreCase), devices);
        modelRoot = Path.GetFullPath(modelRoot);
        var componentIds = ImageGenerationCatalog.Get(modelId).Components;
        var store = new ManagedModelLibraryStore();
        var cards = componentIds.Select(id => store.Load(id) ?? throw new InvalidDataException("Missing verified artifact: " + id)).ToArray();
        Assert.IsTrue(cards.All(card => card.Status == ManagedModelStatuses.Installed && card.Files.All(file =>
        {
            var item = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
            return item.Exists && item.Length == file.VerifiedSizeBytes && item.LastWriteTimeUtc == file.VerifiedLastWriteTimeUtc;
        })), "Requires already verified scenario artifacts.");
        var directory = Path.Combine(stand, "sd-native-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), modelId,
            "A red ceramic teapot on a white table, studio photograph.", 256, 256, [12345], modelRoot, directory);
        var prompt = Path.Combine(directory, "prompt.txt"); var output = Path.Combine(directory, "output.png");
        await File.WriteAllTextAsync(prompt, request.Prompt, timeout.Token);
        var selection = await SdRuntimeSelector.SelectAsync(cards, backend == "cpu", timeout.Token);
        Assert.AreEqual(backend == "vulkan", selection.UsesGpu);
        var info = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, prompt, output, selection);
        using var process = OwnedProcessRegistry.Shared.Start(info, "SD.HardwareNativeTest");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var log = await stdout + "\n" + await stderr;
            await File.WriteAllTextAsync(Path.Combine(directory, "runtime.log"), log);
            Assert.AreEqual(0, process.ExitCode, log);
            Assert.IsTrue(ImageGenerationRuntime.IsValidImage(output, request));
            TestContext.WriteLine(modelId + "/" + backend + ": checked PNG at " + output);
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }
}
