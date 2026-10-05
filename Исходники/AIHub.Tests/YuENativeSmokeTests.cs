using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class YuENativeSmokeTests
{
    [TestMethod]
    [DataRow(MusicYueRuntime.CpuPack)]
    [DataRow(MusicYueRuntime.CudaPack)]
    public async Task ExplicitlyAuthorizedShortGenerationProducesValidatedAudio(string pack)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_YUE_AUTHORIZED_SMOKE") != "1") Assert.Inconclusive("Requires explicit model launch and runtime license acknowledgement.");
        var root = Environment.GetEnvironmentVariable("LOPATA_YUE_MODELS_ROOT") ?? throw new InvalidOperationException("Models root required.");
        var folder = Environment.GetEnvironmentVariable("LOPATA_YUE_SMOKE_OUTPUT") ?? throw new InvalidOperationException("Isolated output required.");
        folder = Path.Combine(folder, pack); Directory.CreateDirectory(folder);
        var receipts = Path.Combine(folder, "test-license-receipts.json");
        File.Copy(Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"), receipts, false);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            receipts);
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (ids, token) => licenses.EnsureAsync(ids, entries =>
        {
            // Isolated test consent only. Never write a new component acknowledgement to the user's profile.
            // Weight terms must retain their existing human-issued receipt.
            if (entries.Any(e => e.Id != MusicYueRuntime.ComponentId && e.Id != MusicYueRuntime.CudaComponentId && e.Id != MusicYueRuntime.VulkanComponentId))
                throw new InvalidOperationException("Model weight licence acknowledgement is missing.");
            return Task.FromResult(true);
        }, token);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var directory = MusicYueRuntime.DirectoryForPack(pack);
            var worker = new MusicYueWorker(directory);
            var log = Path.Combine(folder, "native.log");
            worker.Log += line => File.AppendAllText(log, line + Environment.NewLine);
            var cards = MusicComponentCatalog.CreateCards(root);
            string Artifact(string id) { var c = cards.Single(c => c.ModelArtifactId == id); return Path.Combine(c.InstallDirectory, c.Files.Single().RelativePath); }
            var request = new MusicYueRequest("instrumental, solo piano, calm", "", 12345, 67890, 1) { PlanTokenLimit = 128 };
            var plan = Path.Combine(folder, "план.abc");
            await worker.PlanAsync(Artifact(MusicComponentCatalog.ModelId), request, Path.Combine(folder, "задание-план.json"), plan, cancel.Token);
            request = request with { Abc = await File.ReadAllTextAsync(plan, cancel.Token) };
            var audio = Path.Combine(folder, "Проверка YuE2.wav");
            await worker.SynthesizeAsync(Artifact(MusicComponentCatalog.ModelId), Artifact(MusicComponentCatalog.DecoderId), request,
                Path.Combine(folder, "задание-аудио.json"), audio, cancel.Token);
            Assert.IsTrue(MusicWaveFile.ReadDuration(audio) > TimeSpan.Zero);
            Assert.IsTrue(new FileInfo(audio).Length > 44);
            if (MusicYueRuntime.IsCuda(directory))
            {
                var transcript = await File.ReadAllTextAsync(log, cancel.Token);
                StringAssert.Contains(transcript, "[Load] LM backend: CUDA0");
                StringAssert.Contains(transcript, "[Load] NAR backend: CUDA0");
                StringAssert.Contains(transcript, "[Load] VAE backend: CUDA0");
                // Physical Vulkan coverage on this NVIDIA card; this is not a physical AMD/Intel test.
                var vulkanAudio = Path.Combine(folder, "Vulkan.wav");
                var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(directory, "yue-synth.exe"))
                { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                MusicHardwareProbe.CleanEnvironment(info); info.Environment["GGML_BACKEND"] = "Vulkan0";
                foreach (var argument in new[] { "--model", Artifact(MusicComponentCatalog.ModelId), "--vae", Artifact(MusicComponentCatalog.DecoderId),
                    "--request", Path.Combine(folder, "задание-аудио.json"), "--out", vulkanAudio, "--max-seq", "512" }) info.ArgumentList.Add(argument);
                using var vk = OwnedProcessRegistry.Shared.Start(info, "Music.VulkanSmoke");
                var vkOut = vk.StandardOutput.ReadToEndAsync(); var vkErr = vk.StandardError.ReadToEndAsync();
                try { await vk.WaitForExitAsync(cancel.Token); }
                finally { if (!vk.HasExited) { vk.Kill(entireProcessTree: true); await vk.WaitForExitAsync(); } }
                var vkLog = await vkOut + await vkErr; await File.WriteAllTextAsync(Path.Combine(folder, "vulkan.log"), vkLog);
                Assert.AreEqual(0, vk.ExitCode, vkLog); Assert.IsTrue(MusicWaveFile.ReadDuration(vulkanAudio) > TimeSpan.Zero);
                StringAssert.Contains(vkLog, "[Load] LM backend: Vulkan0");
                StringAssert.Contains(vkLog, "[Load] NAR backend: Vulkan0");
                StringAssert.Contains(vkLog, "[Load] VAE backend: Vulkan0");
            }
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            worker.Log += line => { if (line.StartsWith("[AR] Score 0/", StringComparison.Ordinal)) stop.Cancel(); };
            var cancelledPlan = Path.Combine(folder, "отменённый-план.abc");
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker.PlanAsync(Artifact(MusicComponentCatalog.ModelId),
                request with { Abc = "" }, Path.Combine(folder, "задание-отмена.json"), cancelledPlan, stop.Token));
            Assert.IsFalse(File.Exists(cancelledPlan));
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "Music.YuE2"));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}
