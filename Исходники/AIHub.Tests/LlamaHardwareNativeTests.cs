using System.Diagnostics;
using System.IO;
using AIHub.Services;
using AIHub.Models;
using System.Net.Http;
using System.Net.Http.Json;

namespace AIHub.Tests;

[TestClass]
public sealed class LlamaHardwareNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CliServiceGeneratesRealReplyAndRetiresProcess(bool forceCpu)
    {
        var model = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_MODEL");
        if (string.IsNullOrWhiteSpace(model)) Assert.Inconclusive("Explicit verified model path required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var runtime = new LlamaCliRuntimeService(new(new UserProfileStore(), new IpLocationService()), forceCpu);
        var diagnostics = new System.Text.StringBuilder();
        var reply = await runtime.GenerateAsync(new DebugModelInfo { Path = model, Name = "Qwen3 CLI hardware test" }, [],
            "Name the capital of France. Reply with one word.", line => diagnostics.AppendLine(line), timeout.Token);
        TestContext.WriteLine(diagnostics.ToString());
        TestContext.WriteLine(reply);
        Assert.IsTrue(reply.Contains("Paris", StringComparison.OrdinalIgnoreCase), reply);
        Assert.IsTrue(diagnostics.ToString().Contains(forceCpu ? "CPU" : "Vulkan", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LiteraryScenarioUsesManagedHardwareAndReportsActualContext(bool forceCpu)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_LITERARY_MANAGED_NATIVE") != "1")
            Assert.Inconclusive("Explicit real literary scenario runtime check only.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var runtime = new LiteraryChatRuntime(null, false, forceCpu);
        try
        {
            await runtime.PrepareAsync(timeout.Token);
            Assert.IsTrue(runtime.CurrentExecutable!.Contains(forceCpu ? "runtime.llama.cpu" : "runtime.llama.vulkan", StringComparison.Ordinal));
            Assert.IsTrue(runtime.ContextCapacity >= 1024);
            Assert.IsTrue(runtime.ContextCapacity <= runtime.ModelContextTokens);
            using var client = new HttpClient();
            using var slots = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(new Uri(runtime.Endpoint, "slots"), timeout.Token));
            Assert.AreEqual(runtime.ContextCapacity, slots.RootElement[0].GetProperty("n_ctx").GetInt32());
            TestContext.WriteLine(runtime.CurrentExecutable + "; context=" + runtime.ContextCapacity);
            using var response = await client.PostAsJsonAsync(new Uri(runtime.Endpoint, "v1/chat/completions"),
                new { messages = new[] { new { role = "user", content = "Name the capital of France. Reply with one word." } },
                    max_tokens = 24, temperature = 0, stream = false, chat_template_kwargs = new { enable_thinking = false } }, timeout.Token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            TestContext.WriteLine(text);
            Assert.IsTrue(text.Contains("Paris", StringComparison.OrdinalIgnoreCase), text);
        }
        finally { runtime.Stop(); await runtime.AwaitProcessRetirementAsync(CancellationToken.None); }
    }

    [TestMethod]
    public async Task VerifiedHybridLiteraryWeightsProduceMeaningfulCpuAndVulkanReplies()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_LLAMA_HYBRID_NATIVE") != "1")
            Assert.Inconclusive("Explicit native hybrid-model verification only.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var model = await LiteraryModelLocation.ResolveAsync(timeout.Token);
        var metadata = LiteraryModelMemoryMetadata.Read(model);
        TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(metadata));
        Assert.AreEqual("qwen35", metadata.Architecture);
        foreach (var cpu in new[] { true, false })
        {
            var selection = await LlamaRuntimeSelector.SelectAsync(LlamaDenseMemoryPolicy.GpuRequired(metadata, 512), cpu, TestContext.WriteLine, timeout.Token);
            Assert.AreEqual(!cpu, selection.UsesGpu);
            var info = new ProcessStartInfo(selection.Bundle.Cli) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = selection.Bundle.Directory, RedirectStandardOutput = true, RedirectStandardError = true };
            RuntimeDeviceProbe.ClearBackendOverrides(info);
            foreach (var arg in new[] { "-m", model, "-p", "Name the capital of France. Reply with one word.", "-n", "24", "-c", "512",
                "-ngl", selection.GpuLayers.ToString(), "--device", selection.DeviceId, "--simple-io", "--single-turn", "--no-display-prompt",
                "--reasoning", "off", "--temp", "0", "--seed", "12345", "--offline" }) info.ArgumentList.Add(arg);
            using var process = OwnedProcessRegistry.Shared.Start(info, "HybridHardware.NativeQualityTest");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(0, process.ExitCode, await error);
                var reply = await output;
                TestContext.WriteLine(selection.Bundle.Backend + ": " + reply + "\n" + await error);
                Assert.IsTrue(reply.Contains("Paris", StringComparison.OrdinalIgnoreCase), reply);
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CoreServerUsesManagedRuntimeAndPreservesFullContext(bool forceCpu)
    {
        var model = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_MODEL");
        if (string.IsNullOrWhiteSpace(model)) Assert.Inconclusive("Requires explicit verified model path and installed hardware runtimes.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()), forceCpu);
        try
        {
            await runtime.PrepareAsync(new DebugModelInfo { Path = model, Name = "Qwen3 native hardware test" }, TestContext.WriteLine, timeout.Token);
            Assert.IsTrue(runtime.ExpectedExecutablePath.Contains(forceCpu ? "runtime.llama.cpu" : "runtime.llama.vulkan", StringComparison.Ordinal));
            using var client = new HttpClient();
            using var slots = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(runtime.Endpoint + "/slots", timeout.Token));
            Assert.AreEqual(CoreContextRuntimeLimits.CurrentBackendContextLimit, slots.RootElement[0].GetProperty("n_ctx").GetInt32());
            using var response = await client.PostAsJsonAsync(runtime.Endpoint + "/completion",
                new { prompt = "Say OK.", n_predict = 16, temperature = 0 }, timeout.Token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            Assert.IsTrue(text.Contains("content", StringComparison.Ordinal));
            TestContext.WriteLine(text);
        }
        finally { runtime.Stop(); }
    }

    [TestMethod]
    public async Task ExplicitCachedInstallationUsesOrdinaryComponentManager()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_INSTALL_HARDWARE_NATIVE") != "1")
            Assert.Inconclusive("Installation into the development runtime requires explicit opt-in.");
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        Assert.IsFalse(string.IsNullOrWhiteSpace(stand));
        AppDataPaths.EnsureComponentDirectories();
        if (File.Exists(AppDataPaths.ComponentStatePath))
            File.Copy(AppDataPaths.ComponentStatePath, Path.Combine(stand, "component-state-before-" + Guid.NewGuid().ToString("N") + ".json"));
        var manager = new ComponentManager();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var entry in HardwareRuntimeCatalog.Components)
        {
            var packageDirectory = entry.Id is HardwareRuntimeCatalog.SdCpuId or HardwareRuntimeCatalog.SdVulkanId ? "sd-packages" : "packages-v2";
            var source = Path.Combine(stand, packageDirectory, entry.FileName);
            var cache = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
            if (!File.Exists(cache)) File.Copy(source, cache);
            var result = await manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token);
            Assert.IsTrue(result.IsAvailable);
            await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(entry.Id, result.Record.InstallPath, timeout.Token);
            Assert.IsTrue((await manager.DownloadAndInstallAsync(entry.Id, null, timeout.Token)).IsAvailable);
            TestContext.WriteLine("Managed cached installation and reuse passed: " + result.Record.InstallPath);
        }
    }

    [TestMethod]
    [DataRow(HardwareRuntimeCatalog.LlamaCpuId, "cpu")]
    [DataRow(HardwareRuntimeCatalog.LlamaVulkanId, "vulkan")]
    [DataRow(HardwareRuntimeCatalog.SdCpuId, "cpu")]
    [DataRow(HardwareRuntimeCatalog.SdVulkanId, "vulkan")]
    public async Task DeliveredBundleVerifiesAndRejectsSameSizeCorruption(string id, string backend)
    {
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        if (string.IsNullOrWhiteSpace(stand)) Assert.Inconclusive("Requires explicitly prepared native packages.");
        var isSd = id is HardwareRuntimeCatalog.SdCpuId or HardwareRuntimeCatalog.SdVulkanId;
        var source = Path.Combine(stand, isSd ? "sd-packages" : "packages-v2", "payload-" + backend);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(id, source, timeout.Token);
        Assert.IsTrue(HardwareRuntimeBundleVerifier.HasCompleteLayout(id, source));
        // Separate test copy: never mutate the artifact intended for publication.
        var copy = Path.Combine(stand, "verification-tests", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var unlisted = Path.Combine(copy, "unexpected-backend.dll");
        await File.WriteAllTextAsync(unlisted, "unlisted dependency", timeout.Token);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HardwareRuntimeBundleVerifier.VerifyFilesAsync(id, copy, timeout.Token));
        File.Delete(unlisted);
        var library = Path.Combine(copy, "ggml-base.dll");
        using (var stream = new FileStream(library, FileMode.Open, FileAccess.ReadWrite))
        {
            var original = stream.ReadByte(); stream.Position = 0; stream.WriteByte((byte)(original ^ 1));
        }
        Assert.IsTrue(HardwareRuntimeBundleVerifier.HasCompleteLayout(id, copy));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => HardwareRuntimeBundleVerifier.VerifyFilesAsync(id, copy, timeout.Token));
        File.Delete(Path.Combine(copy, isSd ? "sd-cli.exe" : "llama-cli.exe"));
        Assert.IsFalse(HardwareRuntimeBundleVerifier.HasCompleteLayout(id, copy));
        TestContext.WriteLine("Delivered runtime version and hashes verified; modified DLL and missing CLI rejected: " + backend);
    }

    [TestMethod]
    public async Task PinnedCpuAndVulkanBundlesPerformRealInference()
    {
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        var model = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_MODEL");
        if (string.IsNullOrWhiteSpace(stand) || string.IsNullOrWhiteSpace(model)) Assert.Inconclusive("Native test requires explicit stand and verified model paths.");
        var bundles = new[] {
            new LlamaRuntimeBundle(Path.Combine(stand, "packages-v2", "payload-cpu"), "CPU", ["runtime.llama-engine", "runtime.llama-msvc"]) { ComponentId = HardwareRuntimeCatalog.LlamaCpuId },
            new LlamaRuntimeBundle(Path.Combine(stand, "packages-v2", "payload-vulkan"), "Vulkan", ["runtime.llama-engine", "runtime.llama-msvc", "runtime.llama-vulkan-loader"]) { ComponentId = HardwareRuntimeCatalog.LlamaVulkanId } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        foreach (var forceCpu in new[] { true, false })
        {
            var selection = await LlamaRuntimeSelector.SelectAsync(bundles, 1024 * 1024, forceCpu, TestContext.WriteLine, timeout.Token);
            Assert.AreEqual(forceCpu ? "CPU" : "Vulkan", selection.Bundle.Backend);
            var info = new ProcessStartInfo(selection.Bundle.Cli) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = selection.Bundle.Directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)
                || key is "GGML_BACKEND_PATH" or "GGML_BACKEND" or "GGML_VK_VISIBLE_DEVICES" or "CUDA_VISIBLE_DEVICES").ToArray()) info.Environment.Remove(key);
            foreach (var arg in new[] { "-m", model, "-p", "Say OK.", "-n", "16", "-c", "512", "-ngl", selection.GpuLayers.ToString(),
                "--device", selection.DeviceId, "--simple-io", "--single-turn", "--no-display-prompt", "--reasoning", "off", "--offline" }) info.ArgumentList.Add(arg);
            using var process = OwnedProcessRegistry.Shared.Start(info, "LlamaHardware.NativeTest");
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(0, process.ExitCode, await error);
                Assert.IsFalse(string.IsNullOrWhiteSpace(await output));
                TestContext.WriteLine($"{selection.Bundle.Backend}: {await output}\n{await error}");
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }
}
