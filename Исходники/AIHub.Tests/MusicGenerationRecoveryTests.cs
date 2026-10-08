using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicGenerationRecoveryTests
{
    [TestMethod]
    [DataRow(MusicComponentCatalog.ModelId)]
    [DataRow(MusicModelVariants.Bf16)]
    public async Task PausePreservesPlanAndResumeDoesNotDuplicateCompletedTracks(string variation)
    {
        using var files = new Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(Path.Combine(files.Root, "models"), Path.Combine(files.Root, "audio"), "Тест", 1, 30, "rock", "Текст",
            expert: MusicModelVariants.Defaults(variation));
        var worker = new FakeWorker { PauseFirst = true }; var runner = new MusicGenerationRunner(jobs, worker);
        var controller = new BackgroundOperationController(new(Path.Combine(files.Root, "background.json")));
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = controller;
        try
        {
            var count = 0; runner.TrackReady += _ => count++;
            var run = ApplicationBackgroundOperations.RunAsync(MusicGenerationRunner.BackgroundKind, job.Title, job.Id,
                new { JobId = job.Id }, async token => { await runner.RunAsync(job.Id, token); return job.Id; }, default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            Assert.IsNotNull(jobs.Load(job.Id).Variants[0].PlanHash); Assert.AreEqual(0, count);
            await controller.ResumeAsync(default); await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, worker.Plans); Assert.AreEqual(2, worker.Synths); Assert.AreEqual(1, count);
            await runner.RunAsync(job.Id, default);
            Assert.AreEqual(1, worker.Plans); Assert.AreEqual(2, worker.Synths);
            Assert.AreEqual(1, jobs.Tracks(job.Id).Count); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public async Task RestartAfterResultPublicationRecognisesExistingAudioWithoutInference()
    {
        using var files = new Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(Path.Combine(files.Root, "models"), Path.Combine(files.Root, "audio"), "CON", 1, 30, "rock", "Текст");
        var worker = new FakeWorker(); var runner = new MusicGenerationRunner(jobs, worker);
        runner.TrackReady += _ => throw new IOException("Simulated delivery interruption.");
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(job.Id, default));
        var restored = new MusicGenerationRunner(new(Path.Combine(files.Root, "jobs")), new FakeWorker { FailOnUse = true });
        await restored.RunAsync(job.Id, default);
        Assert.AreEqual(1, jobs.Tracks(job.Id).Count); Assert.IsTrue(Path.GetFileName(job.Variants[0].ResultPath).StartsWith("_CON"));
        Assert.HasCount(1, Directory.GetFiles(job.OutputFolder, "*.wav"));
    }
    [TestMethod]
    public async Task OutputCollisionCannotOverwriteAnUnrelatedFile()
    {
        using var files = new Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(Path.Combine(files.Root, "models"), Path.Combine(files.Root, "audio"), "Тест", 1, 30, "", "");
        await File.WriteAllTextAsync(job.Variants[0].ResultPath, "Keep this original.");
        await Assert.ThrowsAsync<InvalidDataException>(() => new MusicGenerationRunner(jobs, new FakeWorker()).RunAsync(job.Id, default));
        Assert.AreEqual("Keep this original.", await File.ReadAllTextAsync(job.Variants[0].ResultPath));
        Assert.IsFalse(jobs.Load(job.Id).Variants[0].Completed);
    }
    private sealed class FakeWorker : IMusicYueWorker
    {
        public event Action<string>? Log;
        public int Plans, Synths; public bool PauseFirst, FailOnUse;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
        {
            if (FailOnUse) throw new AssertFailedException("Completed audio was regenerated.");
            Plans++; Log?.Invoke("[ABC] test"); return File.WriteAllTextAsync(planPath, request.EffectiveExpert.Variation == MusicModelVariants.Bf16
                ? "{\"abc\":\"X:1\\nK:C\\nC D E F|\",\"abc_ids\":[52,25,16]}" : "X:1\nK:C\nC D E F|", token);
        }
        public async Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        {
            if (FailOnUse) throw new AssertFailedException("Completed audio was regenerated.");
            if (request.EffectiveExpert.Variation == MusicModelVariants.Bf16) {
                Assert.EndsWith("model.safetensors", model); Assert.Contains(MusicModelVariants.Bf16Vae, decoder);
                Assert.AreEqual(request.LanguageSeed, request.SoundSeed);
                using var saved = System.Text.Json.JsonDocument.Parse(request.Abc);
                CollectionAssert.AreEqual(new[] { 52, 25, 16 }, saved.RootElement.GetProperty("abc_ids").EnumerateArray().Select(t => t.GetInt32()).ToArray());
            }
            Synths++; Started.TrySetResult();
            if (PauseFirst && Synths == 1) await Task.Delay(Timeout.Infinite, token);
            token.ThrowIfCancellationRequested();
            using var writer = new BinaryWriter(File.Create(outputPath));
            writer.Write(0x46464952u); writer.Write(192036u); writer.Write(0x45564157u);
            writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write(48000u); writer.Write(192000u); writer.Write((ushort)4); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write(192000u); writer.Write(new byte[192000]);
        }
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-music-recovery-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root)); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
