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
public sealed class SongClipStartValidationTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task EmptyLegacyPromptIsRejectedBeforeAnAttemptOrBackgroundOperationIsCreated(string language) => OnUi(() =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project();
        var control = fixture.Control(project);
        var window = new Window { Content = control, Width = 1700, Height = 950, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.IsFalse(control.TryValidateStartAttempt(fixture.Attempt(project)));
            Assert.IsNull(fixture.Controller.State, "An invalid form must not reserve the model gate.");
            Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Store.Folder(project.Id), "attempts")));
            Assert.AreEqual(fixture.Localize.T("SongClip.PromptRequired"), Field<TextBlock>(control, "_status").Text);
            Assert.AreNotEqual("SongClip.PromptRequired", Field<TextBlock>(control, "_status").Text);
            Assert.IsTrue(Find<Button>(control, "Model").IsEnabled);
            Assert.IsTrue(Find<Button>(control, "Generate").IsEnabled);
            Assert.IsFalse(Find<Button>(control, "Pause").IsEnabled);
            Assert.IsFalse(Find<Button>(control, "Stop").IsEnabled);
            var prompt = Find<TextBox>(control, "Prompt");
            prompt.Text = "A cat playing beside a bright window.";
            Assert.IsTrue(control.Checkpoint());
            var corrected = fixture.Store.Load(project.Id);
            Assert.IsTrue(control.TryValidateStartAttempt(fixture.Attempt(corrected)));
            Assert.IsNull(fixture.Controller.State, "Preflight does not start inference on its own.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RecoveringTheExactPreflightFailureReleasesTheGateWithoutChangingItsSnapshot() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project();
        var attempt = fixture.Attempt(project);
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project);
        var snapshot = fixture.WriteSnapshot(attempt);
        var original = File.ReadAllBytes(snapshot);
        var control = fixture.Control(project);
        var waiting = fixture.Waiting(attempt);
        fixture.Host(waiting);
        Assert.IsTrue(control.RecoverInvalidWaitingAttempt());
        Assert.AreEqual(BackgroundOperationPhase.Failed, fixture.Controller.State!.Phase);
        Assert.IsFalse(fixture.Controller.HasPending);
        Assert.IsTrue(JsonElement.DeepEquals(waiting.Input, fixture.Controller.State.Input));
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(snapshot));
        Assert.IsFalse(control.RecoverInvalidWaitingAttempt(), "Recovery must be idempotent.");
        using (fixture.Controller.BeginAuxiliary()) { }
        Assert.IsTrue(Find<Button>(control, "Model").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "Pause").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "Stop").IsEnabled);
        return Task.CompletedTask;
    });

    [TestMethod]
    [DataRow("valid-input")]
    [DataRow("other-project")]
    [DataRow("other-kind")]
    [DataRow("explicit-pause")]
    [DataRow("worker-artifact")]
    [DataRow("no-decision")]
    [DataRow("mismatch-snapshot")]
    public Task RecoveryPreservesOtherOrStartedOperations(string scenario) => OnUi(() =>
    {
        using var fixture = new Fixture("en");
        var project = fixture.Project();
        var attempt = fixture.Attempt(project);
        if (scenario == "valid-input") attempt.Settings.Prompt = "Keep this valid task.";
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project);
        var snapshot = fixture.WriteSnapshot(attempt);
        if (scenario == "worker-artifact") File.WriteAllText(Path.Combine(Path.GetDirectoryName(snapshot)!, "progress.jsonl"), "preserve");
        if (scenario == "mismatch-snapshot") SongClipStore.Atomic(snapshot, attempt with
        {
            Settings = attempt.Settings with { Prompt = "A corrected immutable snapshot must not be discarded." }
        });
        var control = fixture.Control(project);
        var waiting = fixture.Waiting(attempt);
        waiting = scenario switch
        {
            "other-project" => waiting with { Project = Guid.NewGuid().ToString("N") },
            "other-kind" => waiting with { Kind = SongClipAttempt.Kind },
            "explicit-pause" => waiting with { UserPaused = true },
            "no-decision" => waiting with { RequiresDecision = false },
            _ => waiting
        };
        fixture.Host(waiting);
        var operationBytes = File.ReadAllBytes(fixture.BackgroundPath);
        var snapshotBytes = File.ReadAllBytes(snapshot);
        Assert.IsFalse(control.RecoverInvalidWaitingAttempt());
        CollectionAssert.AreEqual(operationBytes, File.ReadAllBytes(fixture.BackgroundPath));
        CollectionAssert.AreEqual(snapshotBytes, File.ReadAllBytes(snapshot));
        Assert.AreEqual(attempt.Id, fixture.Store.Load(project.Id).PendingAttemptId);
        Assert.IsTrue(fixture.Controller.HasPending);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task OpeningAProjectRecoversItsInvalidWaitingStartWithoutLaunchingIt() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(); var attempt = fixture.Attempt(project);
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project); fixture.WriteSnapshot(attempt);
        fixture.Host(fixture.Waiting(attempt));
        var control = new SongClipControl(fixture.Localize.T, fixture.Store);
        control.OpenProject(project.Id);
        Assert.AreEqual(BackgroundOperationPhase.Failed, fixture.Controller.State!.Phase);
        Assert.IsFalse(fixture.Controller.HasPending);
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        Assert.AreEqual("", Find<TextBox>(control, "Prompt").Text);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task UserStopCancelsItsPausedAttemptAndPreservesCommittedArtifacts() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(); project.Settings.Prompt = "A cat in a sunlit room.";
        var attempt = fixture.Attempt(project);
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project);
        var snapshot = fixture.WriteSnapshot(attempt);
        var artifact = Path.Combine(Path.GetDirectoryName(snapshot)!, "chunk-0001.pt");
        File.WriteAllBytes(artifact, [11,22,33,44]);
        var snapshotBytes = File.ReadAllBytes(snapshot); var artifactBytes = File.ReadAllBytes(artifact);
        fixture.Host(fixture.Waiting(attempt) with { UserPaused = true });
        var control = fixture.Control(project);
        Assert.IsFalse(Find<Button>(control, "Model").IsEnabled);
        control.StopGeneration();
        Assert.AreEqual(BackgroundOperationPhase.Canceled, fixture.Controller.State!.Phase);
        Assert.IsFalse(fixture.Controller.HasPending);
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        CollectionAssert.AreEqual(snapshotBytes, File.ReadAllBytes(snapshot));
        CollectionAssert.AreEqual(artifactBytes, File.ReadAllBytes(artifact));
        Assert.IsTrue(Find<Button>(control, "Model").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "Pause").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "Stop").IsEnabled);
        Assert.AreEqual(fixture.Localize.T("SongClip.Canceled"), Field<TextBlock>(control, "_status").Text);
        return Task.CompletedTask;
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task OpeningAndConfiguringPreserveAValidPauseAndProgressKeepsTheActualDevice(string language) => OnUi(() =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project(); project.Settings.Prompt = "A singer beside a glowing window.";
        var attempt = fixture.Attempt(project);
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project);
        var snapshot = fixture.WriteSnapshot(attempt);
        var snapshotBytes = File.ReadAllBytes(snapshot);
        fixture.Host(fixture.Waiting(attempt) with { UserPaused = true });
        var operationBytes = File.ReadAllBytes(fixture.BackgroundPath);
        var control = fixture.Control(project);
        control.Configure(fixture.Localize.T, "unused-test-models", 8);
        Assert.IsTrue(fixture.Controller.HasPending);
        Assert.IsTrue(fixture.Controller.State!.UserPaused);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, fixture.Controller.State.Phase);
        Assert.AreEqual(attempt.Id, fixture.Store.Load(project.Id).PendingAttemptId);
        CollectionAssert.AreEqual(operationBytes, File.ReadAllBytes(fixture.BackgroundPath));
        CollectionAssert.AreEqual(snapshotBytes, File.ReadAllBytes(snapshot));

        var type = typeof(SongClipControl);
        var format = type.GetMethod("FormatEngineProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var update = type.GetMethod("UpdateStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        type.GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, true);
        type.GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, DateTimeOffset.UtcNow);
        type.GetField("_stage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, fixture.Localize.T("SongClip.MotionRender"));
        foreach (var device in new[] { "cpu", "cuda:1", "xpu:0" })
        {
            var expected = device == "cpu" ? fixture.Localize.T("SongClip.MotionDeviceCpu")
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, fixture.Localize.T("SongClip.MotionDeviceGpu"), device);
            Assert.AreEqual(expected, format.Invoke(control, [JsonSerializer.Serialize(new { kind = "hardware", device })]));
            update.Invoke(control, null);
            var status = Field<TextBlock>(control, "_status").Text;
            Assert.StartsWith(expected + " · ", status);
            Assert.Contains(fixture.Localize.T("SongClip.MotionRender"), status);
            Assert.DoesNotContain("SongClip.", status);
        }
        type.GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, false);
        return Task.CompletedTask;
    });

    private static T Field<T>(SongClipControl control, string name) => (T)typeof(SongClipControl)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
    private static T Find<T>(SongClipControl control, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(control).OfType<T>()
            .Single(n => AutomationProperties.GetAutomationId(n) == "SongClip." + id);

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
            var project = new SongClipProject { Model = H3MusicRunner.ModelId, Song = "attachments/song.wav",
                Settings = new SongClipSettings { Prompt = "" } };
            Directory.CreateDirectory(Path.GetDirectoryName(Store.Resolve(project.Id, project.Song))!);
            File.WriteAllBytes(Store.Resolve(project.Id, project.Song), [1,2,3]); Store.Save(project); return project;
        }
        internal SongClipAttempt Attempt(SongClipProject project) => new(Guid.NewGuid().ToString("N"), project.Id,
            project.Song, project.Reference, project.Name, project.Lyrics, project.Timings,
            project.Settings with { Motion = project.Settings.Motion with { } }, _files.File("models"), 8) { Model = project.Model };
        internal SongClipControl Control(SongClipProject project)
        { var control = new SongClipControl(Localize.T, Store); control.OpenProject(project.Id); return control; }
        internal string WriteSnapshot(SongClipAttempt attempt)
        { var path = Store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id + "/snapshot.json"); SongClipStore.Atomic(path, attempt); return path; }
        internal BackgroundOperationState Waiting(SongClipAttempt attempt) => new() { Kind = H3MusicRunner.Kind,
            Title = "H3", Project = attempt.ProjectId, Input = JsonSerializer.SerializeToElement(attempt),
            Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true, Detail = "InvalidDataException" };
        internal void Host(BackgroundOperationState state)
        { new BackgroundOperationStore(BackgroundPath).Save(state); Controller.Load(); }
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
