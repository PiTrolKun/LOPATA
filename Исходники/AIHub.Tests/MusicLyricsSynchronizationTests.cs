using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLyricsSynchronizationTests
{
    [TestMethod]
    public async Task RunnerWithholdsTracksDuringAnalysisAndCancellationReplaysOnlyAnalysis()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Cat", 2, 12, "rock", "Кот зовёт солнце");
        var worker = new Worker(); var events = new List<string>(); using var cancel = new CancellationTokenSource();
        var runner = new MusicGenerationRunner(jobs, worker, synchronizeLyrics: (completed, index, token) => {
            Assert.IsTrue(completed.Variants.All(v => v.Completed));
            Assert.AreEqual(0, events.Count(e => e.StartsWith("ready:", StringComparison.Ordinal)));
            events.Add("sync:" + index); cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        });
        runner.TrackReady += track => events.Add("ready:" + track.Variant);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, cancel.Token));
        CollectionAssert.AreEqual(new[] { "sync:0" }, events);
        Assert.HasCount(0, jobs.Tracks(job.Id));
        Assert.IsTrue(jobs.Load(job.Id).Variants.All(v => v.Completed)); Assert.AreEqual(2, worker.Synths);
        var hashes = job.Variants.Select(v => Hash(v.ResultPath)).ToArray(); var syncs = new List<int>();
        var replay = new MusicGenerationRunner(jobs, new Worker { FailOnUse = true }, synchronizeLyrics: (_, index, _) => { syncs.Add(index); return Task.CompletedTask; });
        await replay.RunAsync(job.Id, default);
        CollectionAssert.AreEqual(new[] { 0, 1 }, syncs);
        CollectionAssert.AreEqual(hashes, job.Variants.Select(v => Hash(v.ResultPath)).ToArray());
        Assert.HasCount(2, jobs.Tracks(job.Id));
    }

    [TestMethod]
    public Task GeneratedAnalysisFailureLeavesPublishedAudioSuccessful() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        try {
            using var f = new Fixture(); var fake = new Synchronization { Analyze = (_, _, _) => throw new IOException("Isolated ASR failure") };
            using var view = f.View(fake); var job = f.Jobs.Create(f.Files.Root, Path.Combine(f.Files.Root, "audio"), "Fresh", 2, 12, "rock", "Кот зовёт солнце");
            var worker = new Worker(); var ready = 0;
            var runner = new MusicGenerationRunner(f.Jobs, worker, synchronizeLyrics: view.LyricsPlayback.SynchronizeGeneratedAsync);
            runner.TrackReady += _ => ready++;
            Complete(runner.RunAsync(job.Id, default));
            Assert.AreEqual(2, ready); Assert.AreEqual(2, fake.Analyses); Assert.AreEqual(2, worker.Synths);
            Assert.IsTrue(f.Jobs.Load(job.Id).Variants.All(v => v.Completed)); Assert.HasCount(2, f.Jobs.Tracks(job.Id));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    [TestMethod]
    public Task AnalysisDoesNotLoopAfterErrorButNewUserSelectionCanRetry() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        try {
            using var f = new Fixture(); var fake = new Synchronization { Analyze = (_, _, _) => throw new IOException("Temporary failure") };
            using var view = f.View(fake); var job = f.Completed("First", "Кот зовёт солнце");
            view.Tracks.AddTrack(f.Jobs.Tracks(job.Id).Single());
            Pump(() => fake.Analyses == 1 && fake.Reads >= 2);
            DrainFor(TimeSpan.FromMilliseconds(200)); Assert.AreEqual(1, fake.Analyses, "Internal rereads caused a retry loop.");
            var list = ScenarioNavigationTests.LogicalDescendants(view.Tracks).OfType<ListBox>().Single();
            var item = list.SelectedItem; list.SelectedItem = null; list.SelectedItem = item;
            Pump(() => fake.Analyses == 2 && fake.Reads >= 4);
            DrainFor(TimeSpan.FromMilliseconds(200)); Assert.AreEqual(2, fake.Analyses);
            Assert.IsTrue(f.Jobs.Load(job.Id).Variants.All(v => v.Completed));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    [TestMethod]
    public Task SelectionReadCompletingOutOfOrderCannotReplaceTheNewTrackTiming() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        try {
            using var f = new Fixture(); var firstRead = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRead = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = f.Completed("First", "Первый кот зовёт солнце"); var second = f.Completed("Second", "Второй кот зовёт солнце");
            var fake = new Synchronization { Read = (job, _, _) => job.Id == first.Id ? firstRead.Task : secondRead.Task };
            using var view = f.View(fake); view.Editor.Lyrics = second.Lyrics;
            first = view.Projects.Record(first, MusicProjectSnapshot.FromJob(first)); second = view.Projects.Record(second, MusicProjectSnapshot.FromJob(second));
            view.Tracks.AddTrack(f.Jobs.Tracks(first.Id).Single()); Pump(() => fake.Reads == 1);
            view.Tracks.AddTrack(f.Jobs.Tracks(second.Id).Single()); Pump(() => fake.Reads == 2);
            secondRead.SetResult(Timing(second)); Pump(() => Cached(view)?.JobId == second.Id);
            firstRead.SetResult(Timing(first)); DrainFor(TimeSpan.FromMilliseconds(150));
            Assert.AreEqual(second.Id, Cached(view)?.JobId); Assert.AreEqual(second.Id, view.Player.CurrentTrack?.JobId);
            Assert.AreEqual(second.Lyrics, view.Editor.Lyrics); Assert.AreEqual(second.Id, view.Projects.DisplayedLyricsJobId);
            Assert.AreEqual(0, fake.Analyses);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    [TestMethod]
    public Task AutomaticAnalysisWaitsForBusyAndExplicitlyPausedCommonOperation() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; using var f = new Fixture();
        var background = new BackgroundOperationController(new(Path.Combine(f.Files.Root, "background.json")));
        ApplicationBackgroundOperations.Current = background; using var lifetime = new CancellationTokenSource();
        Task<int>? busy = null; var release = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            busy = background.RunAsync(new() { Kind = "test.busy", Title = "Other model", Input = JsonSerializer.SerializeToElement(new { }) },
                async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 1; }, () => Task.CompletedTask, lifetime.Token);
            Pump(() => started.Task.IsCompleted);
            var fake = new Synchronization { Analyze = (_, _, _) => release.Task };
            using var view = f.View(fake); var job = f.Completed("Held", "Кот зовёт солнце");
            view.Tracks.AddTrack(f.Jobs.Tracks(job.Id).Single()); Pump(() => fake.Reads >= 1);
            DrainFor(TimeSpan.FromMilliseconds(100)); Assert.AreEqual(0, fake.Analyses);
            Complete(background.PauseAsync()); Assert.AreEqual(BackgroundOperationPhase.Paused, background.State!.Phase);
            lifetime.Cancel(); Pump(() => busy.IsCompleted); Assert.Throws<OperationCanceledException>(() => busy.GetAwaiter().GetResult());
            Assert.IsFalse(background.IsRunning); Assert.IsTrue(background.HasPending); Assert.IsTrue(background.State!.UserPaused);
            DrainFor(TimeSpan.FromMilliseconds(100)); Assert.AreEqual(0, fake.Analyses, "Explicit paused work was silently replaced.");
            background.DiscardPending(); Pump(() => fake.Analyses == 1);
            Assert.AreEqual(MusicLyricsSynchronization.BackgroundKind, background.State!.Kind);
            release.SetResult(null); Pump(() => !background.IsRunning && background.State!.Phase == BackgroundOperationPhase.Completed);
            DrainFor(TimeSpan.FromMilliseconds(100)); Assert.AreEqual(1, fake.Analyses);
        }
        finally {
            lifetime.Cancel(); release.TrySetResult(null);
            if (busy is { IsCompleted: false }) Pump(() => busy.IsCompleted);
            ApplicationBackgroundOperations.Current = previous;
        }
    });

    [TestMethod]
    public Task DurableLyricsRestorationUsesTheExistingOperationAndCompletedVariant() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; using var f = new Fixture();
        var job = f.Completed("Restore", "Кот зовёт солнце"); var originalHash = Hash(job.Variants[0].ResultPath);
        var store = new BackgroundOperationStore(Path.Combine(f.Files.Root, "background.json"));
        var state = new BackgroundOperationState { Kind = MusicLyricsSynchronization.BackgroundKind, Title = "Lyrics", Project = job.Id,
            Phase = BackgroundOperationPhase.Paused, UserPaused = true, Input = JsonSerializer.SerializeToElement(new { JobId = job.Id, Variant = 0 }) };
        store.Save(state); var background = new BackgroundOperationController(store); background.Load();
        ApplicationBackgroundOperations.Current = background;
        try {
            var fake = new Synchronization(); using var view = f.View(fake);
            background.Register(MusicLyricsSynchronization.BackgroundKind, view.LyricsPlayback.ResumeAsync);
            Assert.IsTrue(background.CanRestore); Assert.IsTrue(background.State!.UserPaused);
            Complete(background.ResumeAsync(default));
            Assert.AreEqual(1, fake.Analyses); Assert.AreEqual(state.Id, background.State!.Id);
            Assert.AreEqual(BackgroundOperationPhase.Completed, background.State.Phase);
            Assert.AreEqual(job.Id, background.State.Checkpoint!.Value.GetProperty("JobId").GetString());
            Assert.IsTrue(background.State.Checkpoint.Value.GetProperty("AnalysisFinished").GetBoolean());
            Assert.IsTrue(f.Jobs.Load(job.Id).Variants[0].Completed); Assert.AreEqual(originalHash, Hash(job.Variants[0].ResultPath));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    [TestMethod]
    public Task CanceledGenerationDoesNotRestartDeferredAnalysisWhenTheGateBecomesIdle() => ScenarioNavigationTests.Sta(() => {
        var previous = ApplicationBackgroundOperations.Current; using var f = new Fixture();
        var background = new BackgroundOperationController(new(Path.Combine(f.Files.Root, "background.json")));
        ApplicationBackgroundOperations.Current = background;
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            var busy = background.RunAsync(new() { Kind = "test.generation", Title = "Generation", Input = JsonSerializer.SerializeToElement(new { }) },
                _ => release.Task, () => Task.CompletedTask, default);
            var fake = new Synchronization(); using var view = f.View(fake);
            var job = f.Completed("Canceled timing stage", "Кот зовёт солнце");
            view.Tracks.AddTrack(f.Jobs.Tracks(job.Id).Single()); Pump(() => fake.Reads >= 1);
            view.LyricsPlayback.GenerationCanceled(job);
            release.SetResult(1); Complete(busy); DrainFor(TimeSpan.FromMilliseconds(150));
            Assert.AreEqual(0, fake.Analyses, "A user cancellation silently restarted ASR.");
            var list = ScenarioNavigationTests.LogicalDescendants(view.Tracks).OfType<ListBox>().Single();
            var item = list.SelectedItem; list.SelectedItem = null; list.SelectedItem = item;
            Pump(() => fake.Analyses == 1 && !background.IsRunning);
            Assert.IsTrue(f.Jobs.Load(job.Id).Variants.All(v => v.Completed));
        }
        finally { release.TrySetResult(1); ApplicationBackgroundOperations.Current = previous; }
    });

    private static MusicTimedLyrics? Cached(MusicWorkspaceControl view) => (MusicTimedLyrics?)typeof(MusicLyricsPlaybackController)
        .GetField("_timing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view.LyricsPlayback);
    private static MusicTimedLyrics Timing(MusicGenerationJob job) => new() { JobId = job.Id, Variant = 0,
        ProjectId = job.ProjectId, ProjectStep = job.ProjectStep, SourceLyrics = job.Lyrics,
        LyricsHash = MusicTimedLyrics.HashLyrics(job.Lyrics), AudioPayloadHash = new string('a', 64), DurationSeconds = 1,
        Provider = "isolated", ProviderVersion = "1", Lines = [new(0, job.Lyrics.Length, .1, .8)] };
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Complete(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(done(), "Orchestration did not reach the expected state.");
    }
    private static void DrainFor(TimeSpan duration) { var deadline = DateTime.UtcNow + duration; Pump(() => DateTime.UtcNow >= deadline); }
    private sealed class Fixture : IDisposable
    {
        public MusicProjectTests.Files Files { get; } = new(); public MusicGenerationJobs Jobs { get; }
        public Fixture() => Jobs = new(Path.Combine(Files.Root, "jobs"));
        public MusicGenerationJob Completed(string title, string lyrics)
        {
            var job = Jobs.Create(Files.Root, Path.Combine(Files.Root, "audio"), title, 1, 12, "rock", lyrics);
            MusicOutputTests.WriteWave(job.Variants[0].ResultPath);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = Hash(job.Variants[0].ResultPath), DurationSeconds = 1 }] };
            Jobs.Save(job); return job;
        }
        public MusicWorkspaceControl View(Synchronization fake)
        {
            var view = new MusicWorkspaceControl(new(Path.Combine(Files.Root, "projects")), Jobs,
                new MusicOutputPreferences(Path.Combine(Files.Root, "output.json")), fake);
            var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T); view.LyricsPlayback.Configure(l.T); return view;
        }
        public void Dispose() => Files.Dispose();
    }
    private sealed class Synchronization : IMusicLyricsSynchronization
    {
        private int _reads, _analyses;
        public int Reads => Volatile.Read(ref _reads); public int Analyses => Volatile.Read(ref _analyses);
        public Func<MusicGenerationJob, int, CancellationToken, Task<MusicTimedLyrics?>> Read { get; init; } = (_, _, _) => Task.FromResult<MusicTimedLyrics?>(null);
        public Func<string, int, CancellationToken, Task<MusicTimedLyrics?>> Analyze { get; init; } = (_, _, _) => Task.FromResult<MusicTimedLyrics?>(null);
        public event Action<string>? Log { add { } remove { } }
        public Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token) { Interlocked.Increment(ref _reads); return Read(job, index, token); }
        public Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token) { Interlocked.Increment(ref _analyses); return Analyze(jobId, index, token); }
    }
    private sealed class Worker : IMusicYueWorker
    {
        public int Synths; public bool FailOnUse;
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
        { if (FailOnUse) throw new AssertFailedException("Completed audio was replanned."); File.WriteAllText(planPath, "X:1\nK:C\nC D E F|"); return Task.CompletedTask; }
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { if (FailOnUse) throw new AssertFailedException("Completed audio was regenerated."); Synths++; MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
}
