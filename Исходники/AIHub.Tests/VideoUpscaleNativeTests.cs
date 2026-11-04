using System.IO;
using AIHub.Services;
using AIHub.Services.VideoUpscale;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoUpscaleNativeTests
{
    [TestMethod]
    public async Task ManagedInstallAndEveryNeuralMethodRunThroughActualPipeline()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_UPSCALE_NATIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) { Assert.Inconclusive("Opt-in real GPU integration: LOPATA_UPSCALE_NATIVE_ROOT"); return; }
        var evidence = Path.Combine(root, "Integration", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(evidence);
        using var storage = UpscaleStore.UseTestRoot(Path.Combine(evidence, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(evidence, "audit"), false);
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await UpscaleFfmpeg.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(evidence, "source.mkv");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1", "-f", "lavfi", "-i", "sine=duration=1", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", source], CancellationToken.None);
            var media = await UpscaleMediaTools.Describe(source, CancellationToken.None); var hash = await UpscaleMediaTools.Hash(source, CancellationToken.None);
            foreach (var engine in Enum.GetValues<UpscaleEngine>().Where(e => e != UpscaleEngine.Scale)) {
                var p = new UpscaleProject { Source = source, SourceLength = new FileInfo(source).Length, SourceSha256 = hash, Media = media,
                    Mode = UpscaleMode.Manual, PreviewLength = 1,
                    Steps = [UpscaleCatalog.New(engine, engine == UpscaleEngine.Flash ? 1920 : 640, engine == UpscaleEngine.Flash ? 1080 : 360), UpscaleCatalog.New(UpscaleEngine.Scale, 3840, 2160)] };
                using var prepare = new UpscaleComponents();
                Assert.IsTrue(await prepare.PrepareAsync(root, p, false, null, CancellationToken.None), engine + " components");
                var job = UpscalePipeline.Create(p, true, Path.Combine(evidence, engine + ".mp4"));
                Console.WriteLine("INTEGRATION " + engine + " " + UpscaleStore.JobPath(job));
                await new UpscalePipeline().Start(job, root, new PrintProgress(), CancellationToken.None);
                await UpscaleMediaTools.Check(job.Output, 24, 3840, 2160, CancellationToken.None);
                Console.WriteLine("PASS " + engine + " " + job.Output);
            }
        } finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
    private sealed class PrintProgress : IProgress<UpscaleProgress>
    { public void Report(UpscaleProgress p) => Console.WriteLine(p.Detail); }

    [TestMethod]
    public async Task HeavyPresetRunsBothFlashPassesBlendAndEncodedRecoveryParts()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_UPSCALE_NATIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) { Assert.Inconclusive("Opt-in real GPU integration: LOPATA_UPSCALE_NATIVE_ROOT"); return; }
        var evidence = Path.Combine(root, "Integration", "heavy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(evidence);
        using var storage = UpscaleStore.UseTestRoot(Path.Combine(evidence, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(evidence, "audit"), false);
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await UpscaleFfmpeg.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(evidence, "source.mkv");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1", "-c:v", "ffv1", source], CancellationToken.None);
            var p = new UpscaleProject { Source = source, SourceLength = new FileInfo(source).Length, SourceSha256 = await UpscaleMediaTools.Hash(source, CancellationToken.None),
                Media = await UpscaleMediaTools.Describe(source, CancellationToken.None), Mode = UpscaleMode.Heavy, Steps = UpscaleCatalog.Preset(UpscaleMode.Heavy), PreviewLength = 1 };
            var job = UpscalePipeline.Create(p, true, Path.Combine(evidence, "Heavy.mp4"));
            await new UpscalePipeline().Start(job, root, new PrintProgress(), CancellationToken.None);
            await UpscaleMediaTools.Check(job.Output, 24, 3840, 2160, CancellationToken.None);
            var saved = UpscaleStore.Load<UpscaleJob>(UpscaleStore.JobPath(job));
            Assert.IsTrue(saved.Complete); Assert.AreEqual(1, saved.Parts.Count);
            Console.WriteLine("PASS HEAVY " + job.Output);
        } finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
}
