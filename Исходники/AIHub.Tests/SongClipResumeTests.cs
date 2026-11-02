using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipResumeTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task StartContinuesAnInProcessPauseWithoutCreatingAnotherAttempt(string language) => OnUi(async () =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project(); var attempt = fixture.Attempt(project);
        var snapshot = fixture.CommitAttempt(project, attempt);
        var chunk = Path.Combine(Path.GetDirectoryName(snapshot)!, "motion-chunks", "000001.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(chunk)!); File.WriteAllBytes(chunk, [13, 27, 41]);
        var originalSnapshot = File.ReadAllBytes(snapshot); var originalChunk = File.ReadAllBytes(chunk);
        var snapshotTime = File.GetLastWriteTimeUtc(snapshot); var chunkTime = File.GetLastWriteTimeUtc(chunk);
        var control = fixture.Control(project);
        Set(control, "_busy", true);
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = fixture.State(attempt) with { Phase = BackgroundOperationPhase.Running, UserPaused = false };
        var executions = 0;
        var work = fixture.Controller.RunAsync(state, async token =>
        {
            var count = ++executions;
            if (count == 1)
            {
                fixture.Controller.SaveCheckpoint(new { completedChunks = 1 }); entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            resumed.TrySetResult(); await finish.Task.WaitAsync(token); return "finished";
        }, () => Task.CompletedTask, lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await InvokeTask(control, "PauseResume"); await Drain();
            Assert.AreEqual(BackgroundOperationPhase.Paused, fixture.Controller.State!.Phase);
            Assert.IsTrue(Find<Button>(control, "Generate").IsEnabled, "The green action must be available after a confirmed pause even while the original UI call is awaiting resume.");
            Assert.IsTrue(Find<Button>(control, "Pause").IsEnabled);
            Assert.AreEqual("▶", Find<Button>(control, "Pause").Content);
            Assert.AreEqual(fixture.Localize.T("SongClip.Resume"), AutomationProperties.GetName(Find<Button>(control, "Generate")));
            await Start(control);
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(state.Id, fixture.Controller.State.Id);
            Assert.AreEqual(1, fixture.Controller.State.Checkpoint!.Value.GetProperty("completedChunks").GetInt32());
            Assert.AreEqual(2, executions, "Only the unfinished stage should execute again.");
            CollectionAssert.AreEqual(originalSnapshot, File.ReadAllBytes(snapshot));
            CollectionAssert.AreEqual(originalChunk, File.ReadAllBytes(chunk));
            Assert.AreEqual(snapshotTime, File.GetLastWriteTimeUtc(snapshot));
            Assert.AreEqual(chunkTime, File.GetLastWriteTimeUtc(chunk));
            Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(fixture.Store.Folder(project.Id), "attempts")).Length);
            Assert.AreEqual(attempt.Id, fixture.Store.Load(project.Id).PendingAttemptId);
            finish.TrySetResult(); Assert.AreEqual("finished", await work.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            lifetime.Cancel(); finish.TrySetResult();
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Set(control, "_busy", false);
        }
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ReopeningFindsItsParkedJobWhenAnotherScenarioIsSelected(string language) => OnUi(async () =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project(); var attempt = fixture.Attempt(project);
        var snapshot = fixture.CommitAttempt(project, attempt); var before = File.ReadAllBytes(snapshot);
        var own = fixture.State(attempt);
        var other = fixture.OtherState("video.h3.generate") with { RequiresDecision = true,
            Detail = "previous retirement failed", Checkpoint = JsonSerializer.SerializeToElement(new { completed = 4 }) };
        fixture.Host(own, other);
        var otherFile = fixture.JobFile(other.Id);
        var releases = 0;
        await fixture.Controller.RetryPausedRetirementAsync(() => { releases++; return Task.CompletedTask; });
        var retired = new BackgroundOperationStore(otherFile).Load()!;
        Assert.AreEqual(1, releases);
        Assert.AreEqual(BackgroundOperationPhase.Paused, retired.Phase);
        Assert.IsTrue(retired.UserPaused); Assert.IsFalse(retired.RequiresDecision);
        Assert.AreEqual(other.Detail, retired.Detail);
        Assert.IsTrue(JsonElement.DeepEquals(other.Input, retired.Input));
        Assert.IsTrue(JsonElement.DeepEquals(other.Checkpoint!.Value, retired.Checkpoint!.Value));
        var otherBytes = File.ReadAllBytes(otherFile);
        string? resumedId = null;
        fixture.Controller.Register(H3MusicRunner.Kind, async (state, token) =>
        {
            resumedId = state.Id;
            await fixture.Controller.RunAsync(state, _ => Task.FromResult(1), () => Task.CompletedTask, token);
        });
        var control = fixture.Control(project);
        Assert.AreEqual(other.Id, fixture.Controller.State!.Id);
        Assert.IsTrue(Find<Button>(control, "Generate").IsEnabled);
        Assert.IsTrue(Find<Button>(control, "Pause").IsEnabled);
        await Start(control);
        Assert.AreEqual(own.Id, resumedId, "Resume must use the open project's job ID instead of the global selected job.");
        Assert.AreEqual(BackgroundOperationPhase.Completed, fixture.Controller.State!.Phase);
        CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(otherFile));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(snapshot));
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(fixture.Store.Folder(project.Id), "attempts")).Length);
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task AnotherBlockingJobDoesNotLeaveAnEmptyAttemptOrChangeItsPendingId(string language) => OnUi(async () =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project();
        var other = fixture.OtherState("video.h3.generate") with { UserPaused = false, RequiresDecision = true };
        fixture.Host(other);
        var before = File.ReadAllBytes(fixture.JobFile(other.Id));
        var control = fixture.Control(project);
        await Start(control);
        Assert.AreEqual(fixture.Localize.T("SongClip.OtherOperation"), Field<TextBlock>(control, "_status").Text);
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Store.Folder(project.Id), "attempts")));
        Assert.AreEqual(other.Id, fixture.Controller.State!.Id);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.JobFile(other.Id)));
    });

    [TestMethod]
    public Task SameModelInAnotherProjectIsNotOwnedByTheOpenWorkspace() => OnUi(async () =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(); var otherProject = fixture.Project();
        var otherAttempt = fixture.Attempt(otherProject); fixture.CommitAttempt(otherProject, otherAttempt);
        var other = fixture.State(otherAttempt); fixture.Host(other);
        var otherBytes = File.ReadAllBytes(fixture.JobFile(other.Id));
        var control = fixture.Control(project);
        Assert.IsFalse(Find<Button>(control, "Pause").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "Stop").IsEnabled);
        control.StopGeneration(); await InvokeTask(control, "PauseResume");
        Assert.AreEqual(other.Id, fixture.Controller.State!.Id);
        CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(fixture.JobFile(other.Id)));
        Assert.AreEqual(otherAttempt.Id, fixture.Store.Load(otherProject.Id).PendingAttemptId);
    });

    [TestMethod]
    public Task StopTargetsItsParkedJobAndLeavesTheOtherSelectedJobIntact() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(); var attempt = fixture.Attempt(project);
        var snapshot = fixture.CommitAttempt(project, attempt); var snapshotBytes = File.ReadAllBytes(snapshot);
        var own = fixture.State(attempt); var other = fixture.OtherState("video.h3.generate");
        fixture.Host(own, other);
        var otherBytes = File.ReadAllBytes(fixture.JobFile(other.Id));
        var control = fixture.Control(project); control.StopGeneration();
        Assert.AreEqual(other.Id, fixture.Controller.State!.Id);
        CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(fixture.JobFile(other.Id)));
        CollectionAssert.AreEqual(snapshotBytes, File.ReadAllBytes(snapshot));
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        var stopped = new BackgroundOperationStore(fixture.JobFile(own.Id)).Load();
        Assert.AreEqual(BackgroundOperationPhase.Canceled, stopped!.Phase);
        Assert.IsFalse(Find<Button>(control, "Pause").IsEnabled);
        return Task.CompletedTask;
    });

    [TestMethod]
    public async Task FailedRetirementLeavesThePausedFailureAndItsCheckpointUnchanged()
    {
        using var fixture = new Fixture("en");
        var failed = fixture.OtherState("video.h3.generate") with { RequiresDecision = true,
            Detail = "failed release", Checkpoint = JsonSerializer.SerializeToElement(new { completed = 7 }) };
        fixture.Host(failed);
        var jobBytes = File.ReadAllBytes(fixture.JobFile(failed.Id));
        var selectedBytes = File.ReadAllBytes(fixture.BackgroundPath);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Controller.RetryPausedRetirementAsync(
            () => Task.FromException(new IOException("Still owns a model process."))));
        CollectionAssert.AreEqual(jobBytes, File.ReadAllBytes(fixture.JobFile(failed.Id)));
        CollectionAssert.AreEqual(selectedBytes, File.ReadAllBytes(fixture.BackgroundPath));
        Assert.IsTrue(fixture.Controller.BlocksNewWork);
        Assert.IsFalse(fixture.Controller.IsRunning);
    }

    [TestMethod]
    public async Task RetirementHoldsTheComputeLeaseUntilReleaseCompletesWithoutExecutingJobs()
    {
        using var fixture = new Fixture("en");
        var failed = fixture.OtherState("video.h3.generate") with { RequiresDecision = true, Detail = "release failed" };
        fixture.Host(failed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = 0;
        fixture.Controller.Register(failed.Kind, (_, _) => { restored++; return Task.CompletedTask; });
        var retirement = fixture.Controller.RetryPausedRetirementAsync(async () => { entered.TrySetResult(); await release.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(fixture.Controller.IsRunning);
            Assert.IsTrue(fixture.Controller.BlocksNewWork);
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Controller.BeginAuxiliary());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Controller.ResumeAsync(failed.Id, default));
            var before = new BackgroundOperationStore(fixture.JobFile(failed.Id)).Load()!;
            Assert.IsTrue(before.RequiresDecision);
            release.TrySetResult(); await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(fixture.Controller.IsRunning);
            Assert.IsFalse(fixture.Controller.BlocksNewWork);
            Assert.AreEqual(0, restored, "Retiring resources must never execute the paused operation.");
            var after = new BackgroundOperationStore(fixture.JobFile(failed.Id)).Load()!;
            Assert.IsTrue(after.UserPaused); Assert.IsFalse(after.RequiresDecision);
            Assert.AreEqual(BackgroundOperationPhase.Paused, after.Phase);
            Assert.AreEqual(failed.Detail, after.Detail);
        }
        finally { release.TrySetResult(); await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task RetirementDoesNotBypassANonpausedDecisionBlocker()
    {
        using var fixture = new Fixture("en");
        var failed = fixture.OtherState("video.h3.generate") with { RequiresDecision = true };
        var decision = fixture.OtherState("another.scenario") with { UserPaused = false, RequiresDecision = true };
        fixture.Host(failed, decision);
        var failedBytes = File.ReadAllBytes(fixture.JobFile(failed.Id));
        var decisionBytes = File.ReadAllBytes(fixture.JobFile(decision.Id));
        var releases = 0;
        await fixture.Controller.RetryPausedRetirementAsync(() => { releases++; return Task.CompletedTask; });
        Assert.AreEqual(0, releases);
        Assert.IsTrue(fixture.Controller.BlocksNewWork);
        CollectionAssert.AreEqual(failedBytes, File.ReadAllBytes(fixture.JobFile(failed.Id)));
        CollectionAssert.AreEqual(decisionBytes, File.ReadAllBytes(fixture.JobFile(decision.Id)));
    }

    [TestMethod]
    public Task PendingAttemptWinsOverANewerParkedAttemptOfTheSameProject() => OnUi(async () =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(); var pending = fixture.Attempt(project);
        fixture.CommitAttempt(project, pending);
        var newer = fixture.Attempt(project);
        var own = fixture.State(pending) with { UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var unrelated = fixture.State(newer);
        fixture.Host(own, unrelated);
        var otherBytes = File.ReadAllBytes(fixture.JobFile(unrelated.Id));
        string? resumedId = null;
        fixture.Controller.Register(H3MusicRunner.Kind, async (state, token) =>
        {
            resumedId = state.Id;
            await fixture.Controller.RunAsync(state, _ => Task.FromResult(1), () => Task.CompletedTask, token);
        });
        var control = fixture.Control(project); await Start(control);
        Assert.AreEqual(own.Id, resumedId);
        CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(fixture.JobFile(unrelated.Id)));
        Assert.AreEqual(pending.Id, fixture.Store.Load(project.Id).PendingAttemptId);
    });

    private static Task Start(SongClipControl control) => (Task)typeof(SongClipControl)
        .GetMethod("StartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(control, [null, null, CancellationToken.None])!;
    private static Task InvokeTask(SongClipControl control, string method) => (Task)typeof(SongClipControl)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, null)!;
    private static T Field<T>(SongClipControl control, string name) => (T)typeof(SongClipControl)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
    private static void Set(SongClipControl control, string name, object value) => typeof(SongClipControl)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, value);
    private static T Find<T>(SongClipControl control, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(control).OfType<T>()
            .Single(node => AutomationProperties.GetAutomationId(node) == "SongClip." + id);
    private static async Task Drain() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

    private sealed class Fixture : IDisposable
    {
        private readonly VideoPreparationTests.TestFiles _files = new();
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        private readonly CancellationToken _exit = ApplicationBackgroundOperations.ExitToken;
        internal SongClipStore Store { get; }
        internal LocalizationService Localize { get; } = new();
        internal string BackgroundPath { get; }
        internal BackgroundOperationController Controller { get; }
        internal Fixture(string language)
        {
            Store = new(_files.File("projects")); BackgroundPath = _files.File("background.json");
            Controller = new(new BackgroundOperationStore(BackgroundPath)); Localize.Load(language);
            ApplicationBackgroundOperations.Current = Controller; ApplicationBackgroundOperations.ExitToken = default;
        }
        internal SongClipProject Project()
        {
            var project = new SongClipProject { Name = "A song", Model = H3MusicRunner.ModelId, Song = "attachments/song.wav" };
            Directory.CreateDirectory(Path.GetDirectoryName(Store.Resolve(project.Id, project.Song))!);
            File.WriteAllBytes(Store.Resolve(project.Id, project.Song), [1, 2, 3]); Store.Save(project); return project;
        }
        internal SongClipAttempt Attempt(SongClipProject project) => new(Guid.NewGuid().ToString("N"), project.Id,
            project.Song, project.Reference, project.Name, project.Lyrics, project.Timings,
            project.Settings with { Motion = project.Settings.Motion with { } }, _files.File("models"), 8) { Model = project.Model };
        internal string CommitAttempt(SongClipProject project, SongClipAttempt attempt)
        {
            project.PendingAttemptId = attempt.Id; Store.Save(project);
            var path = Store.Resolve(project.Id, "attempts/" + attempt.Id + "/snapshot.json");
            SongClipStore.Atomic(path, attempt); return path;
        }
        internal BackgroundOperationState State(SongClipAttempt attempt) => new() { Kind = H3MusicRunner.Kind,
            Title = "H3", Project = attempt.ProjectId, Input = JsonSerializer.SerializeToElement(attempt),
            Phase = BackgroundOperationPhase.Waiting, UserPaused = true };
        internal BackgroundOperationState OtherState(string kind) => new() { Kind = kind, Title = "Other scenario",
            Project = Guid.NewGuid().ToString("N"), Input = JsonSerializer.SerializeToElement(new { keep = "unchanged" }),
            Phase = BackgroundOperationPhase.Waiting, UserPaused = true };
        internal string JobFile(string id) => Path.Combine(Path.GetDirectoryName(BackgroundPath)!, "Jobs", id + ".json");
        internal void Host(params BackgroundOperationState[] states)
        {
            foreach (var state in states) new BackgroundOperationStore(JobFile(state.Id), false).Save(state);
            new BackgroundOperationStore(BackgroundPath).Save(states[^1]); Controller.Load();
        }
        internal SongClipControl Control(SongClipProject project)
        {
            var control = new SongClipControl(Localize.T, Store); control.Configure(Localize.T, _files.File("models"), 8);
            control.OpenProject(project.Id); return control;
        }
        public void Dispose()
        { ApplicationBackgroundOperations.Current = _previous; ApplicationBackgroundOperations.ExitToken = _exit; _files.Dispose(); }
    }

    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
