using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicKaraokeReadinessTests
{
    [TestMethod]
    public async Task PendingPreparationSurvivesRestartWithoutShowingOrRegeneratingAudio()
    {
        using var files = new MusicProjectTests.Files(); var folder = Path.Combine(files.Root, "jobs"); var jobs = new MusicGenerationJobs(folder);
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Pending", 2, 12, "rock", "Кот зовёт солнце");
        var worker = new Worker(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<int>(); using var stop = new CancellationTokenSource();
        var runner = new MusicGenerationRunner(jobs, worker, synchronizeLyrics: async (_, _, token) => {
            started.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
        }); runner.TrackReady += track => events.Add(track.Variant);
        var run = runner.RunAsync(job.Id, stop.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = new MusicGenerationJobs(folder).Load(job.Id);
        Assert.IsTrue(saved.Variants.All(v => v.Completed && v.KaraokeReadiness == MusicKaraokeReadiness.Pending));
        Assert.HasCount(0, new MusicGenerationJobs(folder).Tracks(job.Id)); Assert.HasCount(0, events);
        var hashes = saved.Variants.Select(v => FileHash(v.ResultPath)).ToArray(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.HasCount(0, new MusicGenerationJobs(folder).Tracks(job.Id));
        var restoredJobs = new MusicGenerationJobs(folder); var resumed = new MusicGenerationRunner(restoredJobs, new Worker { FailOnUse = true },
            synchronizeLyrics: (_, _, _) => throw new IOException("ASR definitively unavailable"));
        resumed.TrackReady += track => { Assert.IsTrue(track.KaraokeUnavailable); events.Add(track.Variant); };
        await resumed.RunAsync(job.Id, default);
        CollectionAssert.AreEqual(new[] { 0, 1 }, events); Assert.AreEqual(2, worker.Synths);
        Assert.IsTrue(restoredJobs.Load(job.Id).Variants.All(v => v.Completed && v.KaraokeReadiness == MusicKaraokeReadiness.Unavailable));
        CollectionAssert.AreEqual(hashes, saved.Variants.Select(v => FileHash(v.ResultPath)).ToArray());
        Assert.HasCount(2, restoredJobs.Tracks(job.Id));
    }
    [TestMethod]
    public async Task CommonPauseKeepsPendingAndResumeOnlyRepeatsPreparation()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Pause", 1, 12, "rock", "Кот зовёт солнце");
        var worker = new Worker(); var prepared = 0; var delivered = 0; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new BackgroundOperationController(new(Path.Combine(files.Root, "background.json")));
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = controller;
        try {
            var runner = new MusicGenerationRunner(jobs, worker, synchronizeLyrics: async (_, _, token) => {
                if (Interlocked.Increment(ref prepared) == 1) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                throw new IOException("No ASR timings after resume");
            }); runner.TrackReady += _ => delivered++;
            var run = ApplicationBackgroundOperations.RunAsync(MusicGenerationRunner.BackgroundKind, job.Title, job.Id, new { JobId = job.Id },
                async token => { await runner.RunAsync(job.Id, token); return job.Id; }, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.AreEqual(0, delivered);
            Assert.AreEqual(MusicKaraokeReadiness.Pending, jobs.Load(job.Id).Variants[0].KaraokeReadiness); Assert.HasCount(0, jobs.Tracks(job.Id));
            await controller.ResumeAsync(default); await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, worker.Synths); Assert.AreEqual(2, prepared); Assert.AreEqual(1, delivered);
            Assert.AreEqual(MusicKaraokeReadiness.Unavailable, jobs.Load(job.Id).Variants[0].KaraokeReadiness);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public async Task ExplicitCancelPublishesExistingPendingAudioButRejectsDamagedReceipt()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Cancel", 1, 12, "rock", "Кот зовёт солнце");
        await ProducePending(jobs, job, new Worker()); var oldHash = FileHash(job.Variants[0].ResultPath);
        var canceled = await jobs.CancelPendingKaraokeAsync(job.Id, default);
        Assert.HasCount(1, canceled); Assert.IsTrue(canceled[0].KaraokeUnavailable); Assert.AreEqual(oldHash, FileHash(job.Variants[0].ResultPath));
        Assert.HasCount(1, new MusicGenerationJobs(Path.Combine(files.Root, "jobs")).Tracks(job.Id));
        var bad = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Damaged", 1, 12, "rock", "Кот зовёт солнце");
        await ProducePending(jobs, bad, new Worker()); File.AppendAllText(bad.Variants[0].ResultPath, "damaged");
        await Assert.ThrowsAsync<InvalidDataException>(() => jobs.CancelPendingKaraokeAsync(bad.Id, default));
        Assert.HasCount(0, jobs.Tracks(bad.Id));
    }
    [TestMethod]
    public async Task ExitDuringPreparationLeavesRecoverablePendingAudio()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Exit", 1, 12, "rock", "Кот зовёт солнце");
        var backgroundPath = Path.Combine(files.Root, "background.json"); var controller = new BackgroundOperationController(new(backgroundPath));
        var previous = ApplicationBackgroundOperations.Current; var oldExit = ApplicationBackgroundOperations.ExitToken;
        using var exit = new CancellationTokenSource(); ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = exit.Token;
        try {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var delivered = 0;
            var runner = new MusicGenerationRunner(jobs, new Worker(), synchronizeLyrics: async (_, _, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            runner.TrackReady += _ => delivered++;
            var run = ApplicationBackgroundOperations.RunAsync(MusicGenerationRunner.BackgroundKind, job.Title, job.Id, new { JobId = job.Id },
                async token => { await runner.RunAsync(job.Id, token); return job.Id; }, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); exit.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => run);
            Assert.AreEqual(0, delivered); Assert.HasCount(0, new MusicGenerationJobs(Path.Combine(files.Root, "jobs")).Tracks(job.Id));
            var restored = new BackgroundOperationStore(backgroundPath).Load(); Assert.IsNotNull(restored);
            Assert.AreEqual(BackgroundOperationPhase.Waiting, restored.Phase); Assert.AreEqual(job.Id, restored.Input.GetProperty("JobId").GetString());
            Assert.IsTrue(jobs.Load(job.Id).Variants[0].Completed); Assert.AreEqual(MusicKaraokeReadiness.Pending, jobs.Load(job.Id).Variants[0].KaraokeReadiness);
        }
        finally { ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = oldExit; }
    }
    [TestMethod]
    public async Task OldCompletedJobWithoutReadinessRemainsVisibleAndInstrumentalDoesNotNeedAsr()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var legacy = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Old", 1, 12, "rock", "Кот зовёт солнце");
        MusicOutputTests.WriteWave(legacy.Variants[0].ResultPath);
        jobs.Save(legacy with { Variants = [legacy.Variants[0] with { Completed = true, AudioHash = FileHash(legacy.Variants[0].ResultPath) }] });
        Assert.HasCount(1, new MusicGenerationJobs(Path.Combine(files.Root, "jobs")).Tracks(legacy.Id));
        Assert.IsNull(jobs.Tracks(legacy.Id)[0].KaraokeReadiness);
        var instrumental = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Instrumental", 1, 12, "rock", "",
            wishes: new MusicWishSnapshot(true, false, false, [], []));
        var asrCalls = 0; var runner = new MusicGenerationRunner(jobs, new Worker(), synchronizeLyrics: (_, _, _) => { asrCalls++; throw new AssertFailedException("Instrumental requested ASR"); });
        await runner.RunAsync(instrumental.Id, default); Assert.AreEqual(0, asrCalls);
        Assert.AreEqual(MusicKaraokeReadiness.Ready, jobs.Load(instrumental.Id).Variants[0].KaraokeReadiness);
        Assert.IsFalse(jobs.Tracks(instrumental.Id)[0].KaraokeUnavailable);
    }
    [TestMethod]
    public Task ControllerRequiresActualTimingAndUnavailableBadgeSurvivesProjectReopen() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        try {
            using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
            var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "No karaoke", 1, 12, "rock", "Кот зовёт солнце");
            using var view = new MusicWorkspaceControl(new(Path.Combine(files.Root, "projects")), jobs, new(Path.Combine(files.Root, "output.json")), new NoTiming());
            var localization = new LocalizationService(); localization.Load("ru"); view.Localize(localization.T); view.LyricsPlayback.Configure(localization.T);
            job = view.Projects.Record(job, MusicProjectSnapshot.FromJob(job));
            var runner = new MusicGenerationRunner(jobs, new Worker(), synchronizeLyrics: view.LyricsPlayback.SynchronizeGeneratedAsync);
            runner.TrackReady += track => view.Dispatcher.Invoke(() => view.Tracks.AddTrack(track));
            Complete(runner.RunAsync(job.Id, default));
            Assert.AreEqual(MusicKaraokeReadiness.Unavailable, jobs.Load(job.Id).Variants[0].KaraokeReadiness);
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(view.Tracks).OfType<TextBlock>().Any(t => t.Text == "Караоке недоступно"));
            var projectId = view.Projects.Current.Id; view.Projects.Open(projectId);
            Assert.IsTrue(view.Tracks.SelectedTrack!.KaraokeUnavailable); Assert.AreEqual(MusicProjectOutcome.Completed, view.Projects.Current.Steps.Single().Outcome);
            localization.Load("en"); view.Localize(localization.T);
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(view.Tracks).OfType<TextBlock>().Any(t => t.Text == "Karaoke unavailable"));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });
    [TestMethod]
    public Task PendingProjectReopenDoesNotShowTrackOrDeclareCompleted() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(new(Path.Combine(files.Root, "projects")), jobs, new(Path.Combine(files.Root, "output.json")), new NoTiming());
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Pending project", 1, 12, "rock", "Кот зовёт солнце");
        job = view.Projects.Record(job, MusicProjectSnapshot.FromJob(job)); Complete(ProducePending(jobs, job, new Worker()));
        view.Projects.Outcome(job, MusicProjectOutcome.Completed); view.Projects.Open(view.Projects.Current.Id);
        Assert.IsNull(view.Tracks.SelectedTrack); Assert.IsNull(view.Player.CurrentTrack);
        Assert.AreNotEqual(MusicProjectOutcome.Completed, view.Projects.Current.Steps.Single().Outcome);
    });
    [TestMethod]
    public Task ValidPersistedTimingMakesResultReadyOnlyAfterPreparation() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        try {
            using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
            var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Ready", 1, 12, "rock", "Кот зовёт солнце");
            using var view = new MusicWorkspaceControl(new(Path.Combine(files.Root, "projects")), jobs, new(Path.Combine(files.Root, "output.json")), new ValidTiming(jobs));
            var localization = new LocalizationService(); localization.Load("ru"); view.Localize(localization.T); view.LyricsPlayback.Configure(localization.T);
            var count = 0; var runner = new MusicGenerationRunner(jobs, new Worker(), synchronizeLyrics: view.LyricsPlayback.SynchronizeGeneratedAsync);
            runner.TrackReady += track => { Assert.AreEqual(MusicKaraokeReadiness.Ready, track.KaraokeReadiness); count++; };
            Complete(runner.RunAsync(job.Id, default)); Assert.AreEqual(1, count);
            var completed = jobs.Load(job.Id); Assert.AreEqual(MusicKaraokeReadiness.Ready, completed.Variants[0].KaraokeReadiness);
            Assert.HasCount(1, jobs.Tracks(job.Id)); Assert.IsFalse(jobs.Tracks(job.Id)[0].KaraokeUnavailable);
            Assert.IsNotNull(CompleteRead(new MusicLyricsTimingStore(jobs).ReadAsync(completed, 0, default)));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });
    private static async Task ProducePending(MusicGenerationJobs jobs, MusicGenerationJob job, Worker worker)
    {
        using var cancel = new CancellationTokenSource();
        var runner = new MusicGenerationRunner(jobs, worker, synchronizeLyrics: (_, _, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, cancel.Token));
    }
    private static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)); }
    private static T CompleteRead<T>(Task<T> task) { Complete(task); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(task.IsCompleted, "Karaoke readiness orchestration timed out."); task.GetAwaiter().GetResult();
    }
    private sealed class Worker : IMusicYueWorker
    {
        public int Synths; public bool FailOnUse;
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
        { if (FailOnUse) throw new AssertFailedException("Ready audio was replanned"); File.WriteAllText(planPath, "X:1\nK:C\nC D|"); return Task.CompletedTask; }
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { if (FailOnUse) throw new AssertFailedException("Ready audio was regenerated"); Synths++; MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
    private sealed class NoTiming : IMusicLyricsSynchronization
    {
        public event Action<string>? Log { add { } remove { } }
        public Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token) => Task.FromResult<MusicTimedLyrics?>(null);
        public Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token) => Task.FromResult<MusicTimedLyrics?>(null);
    }
    private sealed class ValidTiming(MusicGenerationJobs jobs) : IMusicLyricsSynchronization
    {
        public event Action<string>? Log { add { } remove { } }
        public Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token) => new MusicLyricsTimingStore(jobs).ReadAsync(job, index, token);
        public async Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token)
        {
            var job = jobs.Load(jobId); var value = new MusicTimedLyrics { JobId = job.Id, Variant = index,
                ProjectId = job.ProjectId, ProjectStep = job.ProjectStep, SourceLyrics = MusicTimedLyrics.Normalize(job.Lyrics),
                LyricsHash = MusicTimedLyrics.HashLyrics(job.Lyrics), AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(job.Variants[index].ResultPath, token),
                DurationSeconds = 1, Provider = "readiness-dependency-fixture", ProviderVersion = "1", Lines = [new(0, job.Lyrics.Length, .1, .8)] };
            await new MusicLyricsTimingStore(jobs).PublishAsync(job.Id, index, value, token); return value;
        }
    }
}
