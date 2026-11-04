using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoUpscaleTests
{
    [TestMethod]
    public void EmptyManualChainCanBeSavedAndEditedButNeverRun()
    {
        var project = new UpscaleProject { Mode = UpscaleMode.Manual, Steps = [] };
        UpscaleCatalog.Validate(project, allowEmptyChain: true);
        var restored = UpscaleStore.Clone(project); Assert.AreEqual(0, restored.Steps.Count);
        Assert.Throws<InvalidOperationException>(() => UpscaleCatalog.Validate(restored));
        restored.Steps.Add(UpscaleCatalog.New(UpscaleEngine.Scale)); UpscaleCatalog.Validate(restored);
    }
    [TestMethod]
    public void PresetsAreRealChainsAndRejectUnsupportedCalibration()
    {
        foreach (var mode in Enum.GetValues<UpscaleMode>()) {
            var p = new UpscaleProject { Mode = mode, Steps = UpscaleCatalog.Preset(mode) }; UpscaleCatalog.Validate(p);
            Assert.AreEqual(UpscaleEngine.Scale, p.Steps[^1].Engine); Assert.AreEqual(3840, p.Steps[^1].Width);
            if (mode == UpscaleMode.Heavy) { Assert.AreEqual(2, p.Steps.Count(s => s.Engine == UpscaleEngine.Flash)); Assert.AreEqual(.25, p.Steps[^2].Mix); }
        }
        var invalid = new UpscaleProject { Steps = [UpscaleCatalog.New(UpscaleEngine.Flash)] };
        invalid.Steps[0].Parameters["decode_every"] = "6";
        Assert.Throws<InvalidOperationException>(() => UpscaleCatalog.Validate(invalid));
        invalid.Steps[0].Parameters["decode_every"] = "4"; invalid.Steps[0].Parameters["nr_passes"] = "2";
        Assert.Throws<InvalidOperationException>(() => UpscaleCatalog.Validate(invalid));
    }
    [TestMethod]
    public void PreviewIsBoundedAndProcessingFingerprintIgnoresPlaybackPosition()
    {
        var p = new UpscaleProject { Source = Path.GetFullPath("example.mkv"), Steps = UpscaleCatalog.Preset(UpscaleMode.Manual), Media = new() { Frames = 2400, Duration = 100, Fps = 24 } };
        var before = UpscaleCatalog.Fingerprint(p); p.PreviewStart = 80; Assert.AreEqual(before, UpscaleCatalog.Fingerprint(p));
        p.PreviewLength = 6; Assert.Throws<InvalidDataException>(() => UpscaleCatalog.Validate(p));
        p.PreviewLength = 5; p.Steps[0].Mix = .5; Assert.AreNotEqual(before, UpscaleCatalog.Fingerprint(p));
        Assert.Throws<InvalidDataException>(() => UpscaleStore.ProjectPath("../../escape"));
    }
    [TestMethod]
    public void AtomicProjectCanRecoverPreviousRecord()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-upscale-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "project.json"); var p = new UpscaleProject { Name = "первый" };
        UpscaleStore.Save(file, p); p.Name = "второй"; UpscaleStore.Save(file, p);
        File.WriteAllText(file, "{interrupted"); Assert.AreEqual("первый", UpscaleStore.Load<UpscaleProject>(file).Name);
    }
    [TestMethod]
    public void EveryMethodHasLocalizedNamesAndHumanParameterHelp()
    {
        foreach (var language in new[] { "ru", "en" }) {
            var l = new LocalizationService(); l.Load(language);
            foreach (var engine in Enum.GetValues<UpscaleEngine>()) {
                var name = "VideoUpscale.Engine." + engine; Assert.AreNotEqual(name, l.T(name)); Assert.AreNotEqual(name + ".Help", l.T(name + ".Help"));
                foreach (var p in UpscaleCatalog.Parameters(engine)) { Assert.AreNotEqual(p.NameKey, l.T(p.NameKey)); Assert.AreNotEqual(p.HelpKey, l.T(p.HelpKey)); }
            }
        }
    }
    [TestMethod]
    public async Task RealPipelineStopsResumesAndPreservesFramesAudioAndProvenance()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-upscale-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var storage = UpscaleStore.UseTestRoot(Path.Combine(folder, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(folder, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await UpscaleFfmpeg.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(folder, "source.mkv");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1", "-f", "lavfi", "-i", "sine=duration=1", "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", source], CancellationToken.None);
            var p = new UpscaleProject { Source = source, SourceLength = new FileInfo(source).Length, SourceSha256 = await UpscaleMediaTools.Hash(source, CancellationToken.None),
                Media = await UpscaleMediaTools.Describe(source, CancellationToken.None), Steps = [UpscaleCatalog.New(UpscaleEngine.Scale, 640, 360)], PreviewLength = 1 };
            var job = UpscalePipeline.Create(p, false, Path.Combine(folder, "result.mp4")); job.ChunkFrames = 12; UpscaleStore.Save(UpscaleStore.JobPath(job), job);
            Assert.AreEqual(UpscaleStore.JobPath(job), job.Project.LastJob);
            using var stop = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value.Detail.StartsWith("2/2", StringComparison.Ordinal)) stop.Cancel(); });
            await Assert.ThrowsAsync<OperationCanceledException>(() => new UpscalePipeline().Start(job, "", progress, stop.Token));
            var interrupted = UpscaleStore.Load<UpscaleJob>(UpscaleStore.JobPath(job)); Assert.IsTrue(interrupted.Parts.ContainsKey("0/ready"));
            var partial = UpscaleAssembly.Available(interrupted, Path.Combine(folder, "partial.mp4"));
            await UpscaleAssembly.StartPartial(partial, new InlineProgress(_ => {}), CancellationToken.None, "Partial export");
            await UpscaleMediaTools.Check(partial.Output, 12, 640, 360, CancellationToken.None);
            using (var info = await UpscaleFfmpeg.ProbeAsync(partial.Output, CancellationToken.None))
                StringAssert.Contains(info.RootElement.GetProperty("format").GetProperty("tags").GetProperty("comment").GetString(), "video-upscale/partial/frames-0-11");
            var partTime = File.GetLastWriteTimeUtc(interrupted.Parts["0/ready"].Path);
            await new UpscalePipeline().Start(job, "", new InlineProgress(_ => {}), CancellationToken.None);
            Assert.AreEqual(partTime, File.GetLastWriteTimeUtc(interrupted.Parts["0/ready"].Path));
            using var probe = await UpscaleFfmpeg.ProbeAsync(job.Output, CancellationToken.None);
            Assert.AreEqual(2, probe.RootElement.GetProperty("streams").GetArrayLength());
            var provenance = probe.RootElement.GetProperty("format").GetProperty("tags").GetProperty("comment").GetString()!;
            Assert.AreEqual("LOPATA", JsonDocument.Parse(provenance).RootElement.GetProperty("Program").GetString());
            Assert.IsFalse(provenance.Contains(folder, StringComparison.Ordinal));
            Assert.AreEqual(p.SourceSha256, await UpscaleMediaTools.Hash(source, CancellationToken.None));
            await UpscaleMediaTools.Check(job.Output, 24, 640, 360, CancellationToken.None);
            // A damaged ready part is repaired on explicit resume; the other ready part is reused.
            var saved = UpscaleStore.Load<UpscaleJob>(UpscaleStore.JobPath(job));
            var secondTime = File.GetLastWriteTimeUtc(saved.Parts["1/ready"].Path);
            File.WriteAllText(saved.Parts["0/ready"].Path, "interrupted part");
            saved.Complete = false; saved.FinalArtifact = null; saved.Output = Path.Combine(folder, "repaired.mp4"); UpscaleStore.Save(UpscaleStore.JobPath(saved), saved);
            await new UpscalePipeline().Start(saved, "", new InlineProgress(_ => {}), CancellationToken.None);
            Assert.AreEqual(secondTime, File.GetLastWriteTimeUtc(saved.Parts["1/ready"].Path));
            await UpscaleMediaTools.Check(saved.Output, 24, 640, 360, CancellationToken.None);
        } finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    private sealed class InlineProgress(Action<UpscaleProgress> callback) : IProgress<UpscaleProgress> { public void Report(UpscaleProgress value) => callback(value); }
    [TestMethod]
    public async Task FastSeekKeepsExactlyTheSameFramesAsFullDecodeAtFractionalRate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-upscale-seek-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.mkv"); var fast = Path.Combine(folder, "fast.mkv"); var reference = Path.Combine(folder, "reference.mkv");
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24000/1001:duration=2", "-c:v", "ffv1", source], CancellationToken.None);
        var media = await UpscaleMediaTools.Describe(source, CancellationToken.None);
        await UpscaleMediaTools.Cut(source, fast, media, 12, 24, CancellationToken.None);
        await UpscaleMediaTools.Filter(source, reference, "fps=24000/1001,trim=start_frame=12:end_frame=36,setpts=PTS-STARTPTS,setsar=1", CancellationToken.None);
        await UpscaleMediaTools.Check(fast, 24, 320, 180, CancellationToken.None);
        foreach (var p in new[] { fast, reference }) await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-i", p, "-c:v", "rawvideo", "-pix_fmt", "gbrp16le", "-f", "rawvideo", p + ".raw"], CancellationToken.None);
        Assert.AreEqual(await UpscaleMediaTools.Hash(reference + ".raw", CancellationToken.None), await UpscaleMediaTools.Hash(fast + ".raw", CancellationToken.None));
    }
}
