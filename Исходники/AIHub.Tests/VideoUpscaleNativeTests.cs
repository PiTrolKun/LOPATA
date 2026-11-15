using System.IO;
using AIHub.Services;
using AIHub.Services.VideoUpscale;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoUpscaleNativeTests
{
    [DataRow("Flash", "turbo")]
    [DataRow("NeuralRendering", "")]
    [DataRow("RtxVideo", "")]
    [DataRow("VfxVsr", "")]
    [DataRow("VfxUpscale", "")]
    [DataRow("DlssSr", "Performance")]
    [DataRow("Flash", "base")]
    [TestMethod]
    public async Task NeuralMethodRunsThroughActualPipelineAtNonIntegerScale(string method, string variant)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_UPSCALE_NATIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) { Assert.Inconclusive("Opt-in real GPU integration: LOPATA_UPSCALE_NATIVE_ROOT"); return; }
        var allowDownload = Environment.GetEnvironmentVariable("LOPATA_UPSCALE_NATIVE_DOWNLOAD") == "1";
        if (variant == "base" && !allowDownload) { Assert.Inconclusive("Explicit opt-in for Base component download."); return; }
        var engine = Enum.Parse<UpscaleEngine>(method);
        var evidence = Path.Combine(root, "Integration", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + method + "-" + variant);
        Directory.CreateDirectory(evidence);
        using var storage = UpscaleStore.UseTestRoot(Path.Combine(evidence, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "Integration", "audit"), engine == UpscaleEngine.DlssSr);
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await UpscaleFfmpeg.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(evidence, "source.mkv");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=864x480:rate=24:duration=1", "-f", "lavfi", "-i", "sine=duration=1", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", source], CancellationToken.None);
            var media = await UpscaleMediaTools.Describe(source, CancellationToken.None);
            var hash = await UpscaleMediaTools.Hash(source, CancellationToken.None);
            var step = UpscaleCatalog.New(engine, 1920, 1080);
            if (engine == UpscaleEngine.Flash) step.Parameters["mode"] = variant;
            if (engine == UpscaleEngine.DlssSr) step.Parameters["upscale_dlss_mode"] = variant;
            var p = new UpscaleProject { Source = source, SourceLength = new FileInfo(source).Length, SourceSha256 = hash, Media = media,
                Mode = UpscaleMode.Manual, PreviewLength = 1, Steps = [step] };
            using var prepare = new UpscaleComponents();
            var prepared = await prepare.PrepareAsync(root, p, false, null, CancellationToken.None);
            if (!prepared && variant == "base")
                prepared = await prepare.PrepareAsync(root, p, true, new DownloadProgress(), CancellationToken.None);
            Assert.IsTrue(prepared, engine + " components");
            var job = UpscalePipeline.Create(p, true, Path.Combine(evidence, method + ".mp4"));
            Console.WriteLine("INTEGRATION " + engine + " " + UpscaleStore.JobPath(job));
            await new UpscalePipeline().Start(job, root, new PrintProgress(), CancellationToken.None);
            await UpscaleMediaTools.Check(job.Output, 24, 1920, 1080, CancellationToken.None);
            Console.WriteLine("PASS " + engine + " " + job.Output);
        } finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }

    private sealed class DownloadProgress : IProgress<AIHub.Models.ManagedModelDownloadProgress>
    {
        private long _last;
        public void Report(AIHub.Models.ManagedModelDownloadProgress progress)
        {
            if (progress.Stage != "downloading" || progress.DownloadedBytes - _last < 64L * 1024 * 1024) return;
            _last = progress.DownloadedBytes;
            Console.WriteLine($"Download {progress.FileName} {_last}/{progress.TotalBytes} {progress.BytesPerSecond:0} B/s");
        }
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
