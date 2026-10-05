using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class YuENativeSmokeTests
{
    [TestMethod]
    public async Task ExplicitlyAuthorizedShortCpuGenerationProducesValidatedAudio()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_YUE_AUTHORIZED_SMOKE") != "1") Assert.Inconclusive("Requires explicit model launch and runtime license acknowledgement.");
        var root = Environment.GetEnvironmentVariable("LOPATA_YUE_MODELS_ROOT") ?? throw new InvalidOperationException("Models root required.");
        var folder = Environment.GetEnvironmentVariable("LOPATA_YUE_SMOKE_OUTPUT") ?? throw new InvalidOperationException("Isolated output required.");
        Directory.CreateDirectory(folder);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (ids, token) => licenses.EnsureAsync(ids, entries =>
        {
            // The human explicitly acknowledged these MIT terms in the task. Weight terms must already have a receipt.
            if (entries.Any(e => e.Id != MusicYueRuntime.ComponentId && e.Id != MusicYueRuntime.CudaComponentId))
                throw new InvalidOperationException("Model weight licence acknowledgement is missing.");
            return Task.FromResult(true);
        }, token);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var worker = new MusicYueWorker(MusicYueRuntime.DirectoryPath);
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
            if (MusicYueRuntime.IsCuda(MusicYueRuntime.DirectoryPath))
            {
                var transcript = await File.ReadAllTextAsync(log, cancel.Token);
                StringAssert.Contains(transcript, "[Load] LM backend: CUDA0");
                StringAssert.Contains(transcript, "[Load] NAR backend: CUDA0");
                StringAssert.Contains(transcript, "[Load] VAE backend: CUDA0");
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
