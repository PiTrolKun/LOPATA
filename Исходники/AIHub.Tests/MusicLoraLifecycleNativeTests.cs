using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraLifecycleNativeTests
{
    [TestMethod]
    public async Task StudioPauseDuringBothMergesReleasesFilesAndResumesTheSavedPlan()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_LIFECYCLE");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Explicit native lifecycle stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        stand = Path.Combine(stand, "studio-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(stand);
        var uses = new[] { ("ntc-house", .6), ("grvl-ember", .3) }.Select(p => {
            var d = MusicLoraCatalog.Find(p.Item1); return new MusicLoraUse(d.Id, d.Revision, p.Item2);
        }).ToArray();
        Assert.IsTrue(await MusicLoraAssets.PrepareAsync(root, uses, false, null, default), "Prepare the stand's existing adapters first.");
        var originals = uses.SelectMany(u => {
            var d = MusicLoraCatalog.Find(u.Id); return d.Files.Select(f => Path.Combine(d.Card(root).InstallDirectory, f.RelativePath));
        }).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var previousSet = EngineSetSelection.ActiveId; var recommended = EngineSetSelection.RecommendedId;
        EngineSetSelection.Publish("002", recommended);
        var settings = MusicModelVariants.Defaults(MusicStudioRuntime.Variation);
        settings.Values["abc_sampling.max_tokens"] = 128; settings.Values["steps"] = 8;
        var jobs = new MusicGenerationJobs(Path.Combine(stand, "jobs"));
        var job = jobs.Create(root, Path.Combine(stand, "result"), "LoRA lifecycle stand", 1, 8,
            "house, steady drums, female vocal", "[Verse]\nWe follow sparks across the rain\nAnd find our way back home again",
            expert: settings, outputSettings: new() { Format = MusicAudioFormat.Flac }, loras: uses);
        var worker = new ObservedWorker(); var runner = new MusicGenerationRunner(jobs, worker);
        var controller = new BackgroundOperationController(new(Path.Combine(stand, "background.json")));
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = controller;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        Task<string>? run = null; var delivered = 0;
        var trigger = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wanted = "plan"; var armed = 1;
        worker.Log += line => {
            lock (worker) File.AppendAllText(Path.Combine(stand, "live.log"), worker.Phase + " " + line + "\n");
            var marker = worker.Phase == "plan" ? "[Adapter] AR merged" : "[Adapter] NAR merged";
            if (worker.Phase == Volatile.Read(ref wanted) && line.Contains(marker) && Interlocked.Exchange(ref armed, 0) == 1)
                trigger.TrySetResult(controller.PauseAsync());
        };
        runner.TrackReady += _ => delivered++;
        try {
            run = ApplicationBackgroundOperations.RunAsync(MusicGenerationRunner.BackgroundKind, job.Title, job.Id,
                new { JobId = job.Id }, async token => { await runner.RunAsync(job.Id, token); return job.Id; }, lifetime.Token);
            await (await trigger.Task.WaitAsync(TimeSpan.FromMinutes(3))).WaitAsync(TimeSpan.FromMinutes(1));
            await AssertPausedAsync("ar"); Assert.IsNull(jobs.Load(job.Id).Variants[0].PlanHash);

            wanted = "synth"; trigger = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref armed, 1);
            await controller.ResumeAsync(default);
            await (await trigger.Task.WaitAsync(TimeSpan.FromMinutes(3))).WaitAsync(TimeSpan.FromMinutes(1));
            await AssertPausedAsync("nar");
            var saved = jobs.Load(job.Id).Variants[0]; Assert.IsNotNull(saved.PlanHash); Assert.IsNull(saved.AudioHash);
            var planBytes = await File.ReadAllBytesAsync(saved.PlanFile!); var planTime = File.GetLastWriteTimeUtc(saved.PlanFile!);
            var plansBeforeResume = worker.Plans;
            await controller.ResumeAsync(default); await run.WaitAsync(TimeSpan.FromMinutes(3));
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
            Assert.AreEqual(plansBeforeResume, worker.Plans); Assert.AreEqual(2, worker.Plans); Assert.AreEqual(2, worker.Synths);
            var completed = jobs.Load(job.Id); Assert.IsTrue(completed.Variants[0].Completed); Assert.AreEqual(1, delivered);
            Assert.AreEqual(saved.PlanHash, completed.Variants[0].PlanHash); Assert.AreEqual(planTime, File.GetLastWriteTimeUtc(saved.PlanFile!));
            CollectionAssert.AreEqual(planBytes, await File.ReadAllBytesAsync(saved.PlanFile!));
            var result = completed.Variants[0].ResultPath; var resultTime = File.GetLastWriteTimeUtc(result);
            await new MusicGenerationRunner(jobs, new NoInferenceWorker()).RunAsync(job.Id, lifetime.Token);
            Assert.AreEqual(resultTime, File.GetLastWriteTimeUtc(result)); Assert.HasCount(1, Directory.GetFiles(job.OutputFolder, "*.flac"));
            foreach (var pair in originals) Assert.AreEqual(pair.Value, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pair.Key))));
            var probe = await MusicAudioRuntime.Default.RunAsync(true, ["-v", "error", "-show_format", "-show_streams", "-of", "json", result], lifetime.Token);
            await File.WriteAllTextAsync(Path.Combine(stand, "export-ffprobe.json"), probe);
            using var metadata = JsonDocument.Parse(probe);
            var tags = metadata.RootElement.GetProperty("format").GetProperty("tags");
            var actual = tags.EnumerateObject().Single(p => p.Name.Equals("LOPATA_LORAS", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            using var adapters = JsonDocument.Parse(actual); Assert.AreEqual(2, adapters.RootElement.GetArrayLength());
            foreach (var u in uses) {
                var applied = adapters.RootElement.EnumerateArray().Single(e => e.GetProperty("Id").GetString() == u.Id);
                Assert.AreEqual(u.Strength, applied.GetProperty("Strength").GetDouble()); Assert.AreEqual(u.Revision, applied.GetProperty("Revision").GetString());
            }
            Assert.IsFalse(actual.Contains(root, StringComparison.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(Path.Combine(stand, "summary.json"), JsonSerializer.Serialize(new {
                pass = true, worker.Plans, worker.Synths, delivered, savedPlanReused = true, originalsUnchanged = true,
                noDuplicateInference = true, metadataVerified = true, result }, new JsonSerializerOptions { WriteIndented = true }));
        } finally {
            try {
                lifetime.Cancel();
                if (run is not null) try { await run; } catch (OperationCanceledException) { }
            } finally { ApplicationBackgroundOperations.Current = previous; EngineSetSelection.Publish(previousSet, recommended); }
        }

        async Task AssertPausedAsync(string name)
        {
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            var snapshots = OwnedProcessRegistry.Shared.GetSnapshot();
            Assert.IsFalse(snapshots.Any(s => s.Component.StartsWith("Music.Studio") || s.Component == "Music.HardwareProbe"));
            Assert.IsFalse(Directory.EnumerateFiles(jobs.Folder(job.Id), "*.safetensors", SearchOption.AllDirectories).Any());
            CollectionAssert.AreEqual(uses, jobs.Load(job.Id).Loras); Assert.AreEqual(0, delivered);
            await File.WriteAllTextAsync(Path.Combine(stand, "paused-" + name + ".json"), JsonSerializer.Serialize(new {
                state = controller.State, job = jobs.Load(job.Id), processes = snapshots }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class ObservedWorker : IMusicYueWorker
    {
        private readonly MusicStudioWorker _inner = new();
        public int Plans, Synths; public string Phase = "";
        public event Action<string>? Log;
        public ObservedWorker() => _inner.Log += line => Log?.Invoke(line);
        public string? LastHardware => _inner.LastHardware;
        public string? LastRuntimePack => _inner.LastRuntimePack;
        public string? LastRequestReceipt => _inner.LastRequestReceipt;
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
        { Phase = "plan"; Plans++; return _inner.PlanAsync(model, request, requestPath, planPath, token); }
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { Phase = "synth"; Synths++; return _inner.SynthesizeAsync(model, decoder, request, requestPath, outputPath, token); }
    }
    private sealed class NoInferenceWorker : IMusicYueWorker
    {
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) => throw new AssertFailedException("Completed plan was regenerated.");
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) => throw new AssertFailedException("Completed audio was regenerated.");
    }
}
