using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeFocusTests
{
    [TestMethod]
    public Task AutomaticCpuCheckReusesAnUnchangedInputAndNeverLoadsAModel() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var calls = 0; var gate = NewSignal();
        fixture.Owner.ProgramRunner = async (step, save, _, _) => {
            calls++; await gate.Task; return save(Checked(step));
        };
        var firstCheck = fixture.Owner.CheckAfterEditingAsync();
        var firstId = fixture.Owner.History.Single().Request.Id;
        var duplicate = fixture.Owner.CheckAfterEditingAsync(); Assert.AreEqual(1, calls);
        gate.SetResult(); await Task.WhenAll(firstCheck, duplicate);
        var first = fixture.Owner.History.Single(); Assert.AreEqual(firstId, first.Request.Id);
        await fixture.Owner.CheckAfterEditingAsync();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(first.Request.Id, fixture.Owner.History.Single().Request.Id);
        Assert.IsTrue(first.AutomaticPreview); Assert.IsTrue(first.ProgramReady);
        Assert.IsFalse(first.GenerationRequested); Assert.IsFalse(first.ModelReady);
        Assert.AreEqual(0, fixture.ModelLoads); Assert.IsFalse(fixture.Owner.IsRunning(fixture.Project));
    });

    [TestMethod]
    public Task RealEditorBlurStartsCpuCheckButTypingAndRefocusingDoNot() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var calls = 0;
        fixture.Owner.ProgramRunner = (step, save, _, _) => {
            calls++; return Task.FromResult(save(Checked(step)));
        };
        var host = new Window { Content = fixture.View, Width = 1100, Height = 750, ShowInTaskbar = false };
        try
        {
            host.Show(); host.Activate(); host.UpdateLayout();
            var text = ScenarioNavigationTests.LogicalDescendants(fixture.View.Editor).OfType<TextBox>().Single(item =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(item) == "Music.Lyrics");
            Assert.IsTrue(text.Focus()); Keyboard.Focus(text);
            Assert.IsTrue(fixture.View.Editor.IsEditing);
            Assert.IsTrue(fixture.Owner.ApplyOptions(fixture.Owner.Options with {
                Tuning = new() { Values = new() { ["history.reanalysis_delay"] = 100 } }
            }));
            text.AppendText("\nТретья строка"); await Task.Delay(250);
            Assert.AreEqual(0, calls, "Typing while the lyrics editor owns keyboard focus must not launch a check.");
            Assert.IsTrue(fixture.View.TextActions.RhymeButton.Focus());
            Assert.IsFalse(fixture.View.Editor.IsEditing);
            await Until(() => calls == 1 && fixture.Owner.History.LastOrDefault()?.ProgramReady == true);
            Assert.IsTrue(text.Focus()); Keyboard.Focus(text);
            text.AppendText("\nЕщё одна строка"); await Task.Delay(250);
            Assert.AreEqual(1, calls, "Returning focus and editing must wait for the next blur.");
            Assert.IsTrue(fixture.View.TextActions.RhymeButton.Focus());
            await Until(() => calls == 2 && fixture.Owner.History.LastOrDefault()?.ProgramReady == true);
            Assert.AreEqual(0, fixture.ModelLoads);
        }
        finally { host.Close(); }
    });

    [TestMethod]
    public Task StaleCpuResultIsIgnoredEvenWhenTheWorkerDoesNotHonorCancellation() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var gate = NewSignal(); var calls = 0;
        CancellationToken firstToken = default;
        fixture.Owner.ProgramRunner = async (step, save, _, token) => {
            calls++;
            if (calls == 1) { firstToken = token; await gate.Task; }
            return save(Checked(step));
        };
        var old = fixture.Owner.CheckAfterEditingAsync(); var oldId = fixture.Owner.History.Single().Request.Id;
        fixture.View.Editor.Lyrics = "Белый сад\nТихий дом";
        Assert.IsTrue(firstToken.IsCancellationRequested);
        await fixture.Owner.CheckAfterEditingAsync();
        var current = fixture.Owner.History.Single();
        Assert.AreNotEqual(oldId, current.Request.Id); Assert.IsTrue(current.ProgramReady);
        gate.SetResult(); await old;
        Assert.AreEqual(current.Request.Id, fixture.Owner.History.Single().Request.Id);
        Assert.AreEqual("Белый сад\nТихий дом", fixture.Owner.History.Single().Request.Source);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    [TestMethod]
    public Task AutomaticRefreshReplacesOnlyUntouchedAutomaticPreviews() => StaAsync(async () =>
    {
        using var fixture = new Fixture();
        var legacy = fixture.Step("Старый текст\nВторая строка");
        var automatic = fixture.Step("Предыдущий набросок\nДругая строка") with { AutomaticPreview = true, ProgramReady = true };
        var manual = fixture.Step("Ручной набросок\nЕщё строка") with { AutomaticPreview = true, ProgramEdited = true, Program = "Моя правка" };
        var generated = fixture.Step("Запущенный вариант\nЕщё одна строка") with { AutomaticPreview = true, GenerationRequested = true };
        fixture.Owner.Restore([legacy, automatic, manual, generated]);
        fixture.Owner.ProgramRunner = (step, save, _, _) => Task.FromResult(save(Checked(step)));
        await fixture.Owner.CheckAfterEditingAsync();
        Assert.AreEqual(4, fixture.Owner.History.Count);
        Assert.IsFalse(fixture.Owner.History.Any(step => step.Request.Id == automatic.Request.Id));
        foreach (var preserved in new[] { legacy, manual, generated })
            Assert.IsTrue(fixture.Owner.History.Any(step => step.Request.Id == preserved.Request.Id));
        Assert.AreEqual("Моя правка", fixture.Owner.History.Single(step => step.Request.Id == manual.Request.Id).Program);
    });

    [TestMethod]
    public Task MainButtonOpensTheWorkshopImmediatelyWithoutConfiguredModels() => StaAsync(async () =>
    {
        using var fixture = new Fixture(configure: false);
        fixture.View.TextActions.RhymeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsTrue(fixture.Workshop().IsVisible);
        Assert.AreEqual("Music.Rhyme.Unavailable", fixture.Owner.LaunchError);
        Assert.AreEqual(0, fixture.Owner.History.Count); Assert.AreEqual(0, fixture.ModelLoads);
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task MainButtonOpensTheWorkshopBeforeReportingAnotherOperation() => StaAsync(async () =>
    {
        using var fixture = new Fixture();
        var background = new BackgroundOperationController(new(Path.Combine(fixture.Directory, "background.json")));
        ApplicationBackgroundOperations.Current = background;
        using (background.BeginAuxiliary())
        {
            fixture.View.TextActions.RhymeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(fixture.Workshop().IsVisible);
            Assert.AreEqual("Music.Poetry.OtherOperation", fixture.Owner.LaunchError);
            Assert.AreEqual(0, fixture.ModelLoads);
        }
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task ClickDuringCpuCheckWaitsAndStartsExactlyOneFullRun() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var cpuGate = NewSignal(); var modelGate = NewSignal();
        var cpuCalls = 0; var fullCalls = 0; MusicRhymeStep? received = null;
        fixture.Owner.ProgramRunner = async (step, save, _, _) => {
            cpuCalls++; await cpuGate.Task; return save(Checked(step));
        };
        fixture.Owner.WorkRunner = async (step, save, _, token, _) => {
            fullCalls++; received = step; await modelGate.Task.WaitAsync(token);
            return save(step with { Model = "Новый вариант", ModelReady = true, Stage = "Ready" });
        };
        var check = fixture.Owner.CheckAfterEditingAsync(); var id = fixture.Owner.History.Single().Request.Id;
        fixture.Click(); fixture.Click();
        Assert.IsTrue(fixture.Workshop().IsVisible);
        Assert.AreEqual(0, fullCalls); Assert.AreEqual(1, cpuCalls);
        cpuGate.SetResult(); await check;
        await Until(() => fullCalls == 1);
        Assert.IsNotNull(received); Assert.AreEqual(id, received.Request.Id);
        Assert.IsTrue(received.ProgramReady); Assert.IsTrue(received.GenerationRequested);
        fixture.Click(); Assert.AreEqual(1, fullCalls);
        modelGate.SetResult(); await Until(() => !fixture.Owner.IsRunning(fixture.Project));
        Assert.AreEqual(1, fixture.Owner.History.Count); Assert.IsTrue(fixture.Owner.History.Single().ModelReady);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    [TestMethod]
    public Task AnotherOperationStartingDuringCpuWaitPreventsTheLaterModelLaunch() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var cpuGate = NewSignal(); var fullCalls = 0;
        fixture.Owner.ProgramRunner = async (step, save, _, _) => {
            await cpuGate.Task; return save(Checked(step));
        };
        fixture.Owner.WorkRunner = (step, save, _, _, _) => {
            fullCalls++; return Task.FromResult(save(step with { ModelReady = true, Stage = "Ready" }));
        };
        var check = fixture.Owner.CheckAfterEditingAsync(); fixture.Click();
        Assert.AreEqual(0, fullCalls);
        var background = new BackgroundOperationController(new(Path.Combine(fixture.Directory, "background.json")));
        ApplicationBackgroundOperations.Current = background;
        using (background.BeginAuxiliary())
        {
            cpuGate.SetResult(); await check;
            await Until(() => fixture.Owner.LaunchError == "Music.Poetry.OtherOperation");
            Assert.AreEqual(0, fullCalls); Assert.AreEqual(0, fixture.ModelLoads);
            Assert.IsTrue(fixture.Owner.History.Single().ProgramReady);
            Assert.IsFalse(fixture.Owner.History.Single().ModelReady);
        }
    });

    [TestMethod]
    public Task RestartWaitsForOwnCancellationThenUsesNewIdAndSettingsAndKeepsTheOldDraft() => StaAsync(async () =>
    {
        using var fixture = new Fixture(); var oldCancelled = NewSignal(); var releaseOld = NewSignal();
        var calls = new List<MusicRhymeStep>();
        fixture.Owner.WorkRunner = async (step, save, _, token, _) => {
            calls.Add(step);
            if (calls.Count == 1)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException)
                {
                    oldCancelled.SetResult(); await releaseOld.Task; throw;
                }
            }
            return save(step with { Model = "Результат повторного запуска", ModelReady = true, Stage = "Ready" });
        };
        fixture.Click(); await Until(() => calls.Count == 1);
        var oldId = calls[0].Request.Id;
        Assert.IsTrue(fixture.Owner.ApplyOptions(fixture.Owner.Options with { AutoAnalyze = false,
            Tuning = new() { Values = new() { ["analysis.max_fragments"] = 7 } } }));
        var restart = fixture.Owner.RestartAsync(fixture.Project);
        await oldCancelled.Task;
        Assert.AreEqual(1, calls.Count); Assert.IsFalse(restart.IsCompleted);
        releaseOld.SetResult(); await restart;
        await Until(() => calls.Count == 2 && !fixture.Owner.IsRunning(fixture.Project));
        Assert.AreNotEqual(oldId, calls[1].Request.Id);
        Assert.AreEqual(7, calls[1].Request.Tuning.Integer("analysis.max_fragments"));
        Assert.AreEqual(3, calls[0].Request.Tuning.Integer("analysis.max_fragments"));
        Assert.AreEqual(2, fixture.Owner.History.Count);
        var retained = fixture.Owner.History.Single(step => step.Request.Id == oldId);
        Assert.AreEqual("Partial", retained.Stage); Assert.AreEqual("Music.Rhyme.Stopped", retained.Error);
        Assert.IsTrue(fixture.Owner.History.Single(step => step.Request.Id == calls[1].Request.Id).ModelReady);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    [TestMethod]
    public Task RestartDiscardsOnlyItsOwnWaitingBackgroundJobAndAdmitsTheNewRun() => StaAsync(async () =>
    {
        using var fixture = new Fixture();
        var store = new BackgroundOperationStore(Path.Combine(fixture.Directory, "background.json"));
        var foreignRequest = fixture.Step("Чужой сохранённый текст").Request with { ProjectId = Guid.NewGuid().ToString("N") };
        var foreign = new BackgroundOperationState { Kind = MusicRhymeWork.Kind, Title = "Foreign parked rhyme task",
            Project = foreignRequest.ProjectId, Input = JsonSerializer.SerializeToElement(foreignRequest),
            Checkpoint = JsonSerializer.SerializeToElement(new MusicRhymeStep { Request = foreignRequest, Original = foreignRequest.Text }),
            Private = true, Phase = BackgroundOperationPhase.Paused, UserPaused = true };
        store.Operation(foreign.Id).Save(foreign);
        var background = new BackgroundOperationController(store); background.Load();
        ApplicationBackgroundOperations.Current = background;
        var observed = new List<BackgroundOperationState>(); var operations = new List<BackgroundOperationState>();
        var calls = new List<MusicRhymeStep>(); var retired = 0;
        background.Changed += () => { if (background.State is { } state) observed.Add(state); };
        fixture.Owner.WorkRunner = (step, save, _, token, _) => {
            var operation = new BackgroundOperationState { Kind = MusicRhymeWork.Kind, Title = "Rhyme test task",
                Project = step.Request.ProjectId, Input = JsonSerializer.SerializeToElement(step.Request), Private = true };
            operations.Add(operation);
            return background.RunAsync(operation, async attempt => {
                calls.Add(step);
                var checkpoint = save(step with { Stage = "Model", ProgramReady = true, Program = "Сохранённая правка" });
                background.SaveCheckpoint(checkpoint);
                if (calls.Count == 1) await Task.Delay(Timeout.Infinite, attempt);
                var complete = save(checkpoint with { Model = "Новый результат", ModelReady = true, Stage = "Ready" });
                background.SaveCheckpoint(complete); return complete;
            }, () => { retired++; return Task.CompletedTask; }, token);
        };
        fixture.Click(); await Until(() => calls.Count == 1);
        var oldOperation = operations.Single(); var oldRequest = calls.Single().Request.Id;
        Assert.IsTrue(background.BlocksNewWork);
        Assert.AreEqual(oldRequest, background.State!.Checkpoint!.Value.Deserialize<MusicRhymeStep>()!.Request.Id);
        await fixture.Owner.RestartAsync(fixture.Project);
        await Until(() => calls.Count == 2 && !fixture.Owner.IsRunning(fixture.Project));
        Assert.AreNotEqual(oldRequest, calls[1].Request.Id);
        Assert.AreEqual(1, retired);
        Assert.IsTrue(observed.Any(state => state.Id == oldOperation.Id && state.Phase == BackgroundOperationPhase.Waiting));
        Assert.IsTrue(observed.Any(state => state.Id == oldOperation.Id && state.Phase == BackgroundOperationPhase.Canceled));
        Assert.IsFalse(background.BlocksNewWork);
        var retained = background.PausedOperations.Single();
        Assert.AreEqual(foreign.Id, retained.Id); Assert.AreEqual(foreignRequest.ProjectId, retained.Project);
        Assert.AreEqual(BackgroundOperationPhase.Paused, retained.Phase);
        Assert.IsFalse(observed.Any(state => state.Id == foreign.Id && state.Phase == BackgroundOperationPhase.Canceled));
        Assert.AreEqual(BackgroundOperationPhase.Paused, store.Operation(foreign.Id).Load()!.Phase);
        Assert.IsNull(store.Operation(oldOperation.Id).Load());
        Assert.IsTrue(fixture.Owner.History.Single(step => step.Request.Id == calls[1].Request.Id).ModelReady);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    private static MusicRhymeStep Checked(MusicRhymeStep step) => step with {
        Analysis = MusicRhymeAnalyzer.Analyze(step.Request.Source, _ => [], tuning: step.Request.Tuning),
        Program = "Программное предложение", ProgramDraft = "Программное предложение", ProgramReady = true, Stage = "Reference"
    };
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) Assert.Fail("The expected orchestration state was not reached.");
            await Task.Delay(10);
        }
    }
    private static Task StaAsync(Func<Task> action) => ScenarioNavigationTests.Sta(() => {
        var dispatcher = Dispatcher.CurrentDispatcher; var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action(); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "lopata-rhyme-focus-" + Guid.NewGuid().ToString("N"));
        public MusicWorkspaceControl View { get; }
        public MusicRhymeController Owner { get; }
        public string Project => View.Projects.Current.Id;
        public int ModelLoads { get; private set; }
        private readonly BackgroundOperationController? previous = ApplicationBackgroundOperations.Current;
        private readonly CancellationToken previousExit = ApplicationBackgroundOperations.ExitToken;
        public Fixture(bool configure = true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            View = new(new MusicProjects(Path.Combine(Directory, "projects")), new MusicGenerationJobs(Path.Combine(Directory, "jobs")));
            View.Rhyme.Dispose(); Owner = new(View, new(Path.Combine(Directory, "preferences.json")));
            Owner.Localize(key => key == "Music.Rhyme.Language" ? "ru" : key); Owner.ProgramAvailable = () => true;
            View.Editor.Lyrics = "Спит кот\nВидит сад";
            if (!configure) return;
            WriteCore(); var storage = new StorageSettings { Models = new() { Locations = [new() { Path = Directory }] } };
            Assert.IsNotNull(MusicRhymePreferences.Choices(storage).Single(choice => choice.Candidate.Id == "core").Model);
            Owner.Configure(storage, () => { ModelLoads++; throw new AssertFailedException("An orchestration test must not load a model."); });
        }
        public void Click() => View.TextActions.RhymeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public MusicRhymeWindow Workshop() => PresentationSource.CurrentSources.OfType<HwndSource>().Select(source => source.RootVisual)
            .OfType<MusicRhymeWindow>().Single(window => window.Project == Project);
        public MusicRhymeStep Step(string source) => new() {
            Request = new(Guid.NewGuid().ToString("N"), Project, source, 0, source.Length, "core", null, Directory, "ru"), Original = source
        };
        private void WriteCore()
        {
            using var file = File.Create(Path.Combine(Directory, CoreModelManager.CoreModelFileName));
            using var writer = new BinaryWriter(file, Encoding.UTF8);
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(3ul);
            Text("general.architecture"); writer.Write(8u); Text("qwen3");
            Text("qwen3.block_count"); writer.Write(4u); writer.Write(32u);
            Text("qwen3.context_length"); writer.Write(4u); writer.Write(32768u);
            void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        }
        public void Dispose()
        {
            Owner.Dispose(); View.Dispose(); ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousExit;
            var full = Path.GetFullPath(Directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("lopata-rhyme-focus-", StringComparison.Ordinal)) throw new InvalidOperationException();
            System.IO.Directory.Delete(full, true);
        }
    }
}
