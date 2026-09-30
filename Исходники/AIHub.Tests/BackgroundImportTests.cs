using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundImportTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static Task Run(LiteraryImportControl control, ImportBackgroundInput input, BackgroundOperationState? state = null)
        => (Task)typeof(LiteraryImportControl).GetMethod("RunBackgroundImportAsync", Private)!
            .Invoke(control, [input, state, CancellationToken.None])!;
    private static void Host(BackgroundOperationController? controller, CancellationToken token = default)
    {
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null, controller);
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null, token);
    }
    private static async Task Sta(Func<Task> test)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completed.SetResult(); }
                catch (Exception error) { completed.SetException(error); }
                finally { Host(null); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public Task AnalysisPauseKeepsConfirmedChunksAndEditableAnswers(bool legacy) => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Background);
        Host(controller); var backend = new Backend { BlockCall = 2 }; using var control = fixture.Control(backend);
        var run = Run(control, fixture.Input with { Legacy = legacy });
        await backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var answers = (Dictionary<string, TextBox>)typeof(LiteraryImportControl).GetField("_answerInputs", Private)!.GetValue(control)!;
        answers["test"] = new TextBox { Text = "EDITED_WHILE_ANALYZING" };
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(BackgroundOperationPhase.Paused, fixture.Background.Load()!.Phase);
        Assert.AreEqual("literary.import", controller.State!.Kind); Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual("EDITED_WHILE_ANALYZING", new ImportPreparationAnswers(fixture.SessionRoot).Values["test"]);
        backend.BlockCall = 0; await controller.ResumeAsync(CancellationToken.None); await run;
        Assert.AreEqual(BackgroundOperationPhase.Completed, fixture.Background.Load()!.Phase);
        Assert.AreEqual(1, backend.CallsByText[fixture.Units[0].Text]);
        Assert.AreEqual(2, backend.CallsByText[fixture.Units[1].Text]);
        Assert.AreEqual(1, backend.CallsByText[fixture.Units[2].Text]);
    });

    [TestMethod]
    public Task ExitAndRestartUseSameSessionAndOperation() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var exit = new CancellationTokenSource();
        var firstController = new BackgroundOperationController(fixture.Background); Host(firstController, exit.Token);
        var firstBackend = new Backend { BlockCall = 2 }; var first = fixture.Control(firstBackend);
        var run = Run(first, fixture.Input); await firstBackend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstController.CheckpointForExit(); exit.Cancel(); await run; first.Dispose();
        var saved = fixture.Background.Load()!; Assert.AreEqual(BackgroundOperationPhase.Waiting, saved.Phase);
        var secondController = new BackgroundOperationController(fixture.Background); secondController.Load(); Host(secondController);
        var secondBackend = new Backend(); using var second = fixture.Control(secondBackend);
        var resume = typeof(LiteraryImportControl).GetMethod("ResumeBackgroundImportAsync", Private)!;
        await (Task)resume.Invoke(second, [secondController.State, CancellationToken.None])!;
        Assert.AreEqual(saved.Id, secondController.State!.Id);
        Assert.AreEqual(BackgroundOperationPhase.Completed, secondController.State.Phase);
        Assert.IsFalse(secondBackend.CallsByText.ContainsKey(fixture.Units[0].Text));
        Assert.AreEqual(2, secondBackend.Calls);
        second.Dispose();
        using var session = ImportSession.Open(fixture.SessionRoot);
        Assert.IsNotNull(session.ReadLast<ImportBackgroundResult>("background-result/" + saved.Id));
    });

    [TestMethod]
    public Task FinishedAssemblyReceiptPreventsRepeatedModelAndProjectCreation() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Background); Host(controller);
        var backend = new Backend(); using var control = fixture.Control(backend);
        await Run(control, fixture.Input);
        var assembly = fixture.Input with { Action = "assembly", Work = "Book", Parent = fixture.Root, Name = "Imported",
            Units = fixture.Units.Select(u => u.Id).ToArray() };
        await Run(control, assembly); var saved = controller.State!; var calls = backend.Calls;
        Assert.AreEqual(BackgroundOperationPhase.Completed, saved.Phase);
        Assert.AreEqual(1, fixture.Projects.Load().Projects.Count);
        // Reproduce a crash after the durable receipt and before the root completion save.
        var interrupted = saved with { Phase = BackgroundOperationPhase.Waiting, NeedsAttention = false };
        fixture.Background.Save(interrupted); var restored = new BackgroundOperationController(fixture.Background); restored.Load(); Host(restored);
        await Run(control, assembly, restored.State);
        Assert.AreEqual(calls, backend.Calls); Assert.AreEqual(1, fixture.Projects.Load().Projects.Count);
        Assert.AreEqual(saved.Id, restored.State!.Id); Assert.AreEqual(BackgroundOperationPhase.Completed, restored.State.Phase);
    });

    [TestMethod]
    public Task ChangedSourceCannotRestartInference() => Sta(async () =>
    {
        using var fixture = new Fixture(); File.AppendAllText(Path.Combine(fixture.SessionRoot, "source.json"), "changed");
        var controller = new BackgroundOperationController(fixture.Background); Host(controller);
        var backend = new Backend(); using var control = fixture.Control(backend); await Run(control, fixture.Input);
        Assert.AreEqual(0, backend.Calls); Assert.IsTrue(controller.HasPending);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, controller.State!.Phase); Assert.IsTrue(controller.State.RequiresDecision);
    });

    [TestMethod]
    public Task ChangedSourceDuringLivePauseCannotUseCachedClassification() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Background); Host(controller);
        var backend = new Backend { BlockCall = 2 }; using var control = fixture.Control(backend);
        var run = Run(control, fixture.Input); await backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.PauseAsync(); File.AppendAllText(Path.Combine(fixture.SessionRoot, "source.json"), "changed");
        backend.BlockCall = 0; await controller.ResumeAsync(CancellationToken.None); await run;
        Assert.AreEqual(2, backend.Calls); Assert.AreEqual(BackgroundOperationPhase.Waiting, controller.State!.Phase);
        Assert.IsTrue(controller.State.RequiresDecision);
    });

    [TestMethod]
    public Task FormRetryResumesItsFailedOperationInsteadOfBlockingItself() => Sta(async () =>
    {
        using var fixture = new Fixture(); var source = Path.Combine(fixture.SessionRoot, "source.json"); File.AppendAllText(source, "changed");
        var controller = new BackgroundOperationController(fixture.Background); Host(controller);
        var backend = new Backend(); using var control = fixture.Control(backend); await Run(control, fixture.Input);
        var id = controller.State!.Id; File.WriteAllText(source, "[]");
        typeof(LiteraryImportControl).GetMethod("StartBackgroundImport", Private)!
            .Invoke(control, [(Func<ImportBackgroundInput>)(() => fixture.Input)]);
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (control.IsBusy) await Task.Delay(10, timeout.Token); // The form's UI cleanup follows the root's idle signal.
        Assert.AreEqual(id, controller.State!.Id); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase);
        Assert.AreEqual(3, backend.Calls);
    });

    [TestMethod]
    public Task DisposingBusyFormReleasesSessionAfterCancellationUnwinds() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Background); Host(controller);
        var backend = new Backend { BlockCall = 2 }; using var control = fixture.Control(backend);
        var run = Run(control, fixture.Input); await backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        control.Dispose(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(control.IsBusy);
        Assert.IsNull(typeof(LiteraryImportControl).GetField("_session", Private)!.GetValue(control));
        using var reopened = ImportSession.Open(fixture.SessionRoot);
        Assert.AreEqual(fixture.Input.SessionId, reopened.State.Id);
    });

    private sealed class Backend
    {
        public int BlockCall, Calls;
        public Dictionary<string, int> CallsByText { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> Infer(IReadOnlyList<ImageAnalysisHiddenMessage> messages, string step, int tokens, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(messages.Last().Content);
            var units = payload.RootElement.GetProperty("units"); var text = units[0].GetProperty("text").GetString()!;
            Calls++; CallsByText[text] = CallsByText.GetValueOrDefault(text) + 1;
            if (Calls == BlockCall) { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var final = step.StartsWith("resolve/", StringComparison.Ordinal);
            return JsonSerializer.Serialize(new { units = new object[][] { [0, units.GetArrayLength() - 1, final ? "MAIN" : "KEEP", "Book", "Chapter", ""] } });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-background-import-" + Guid.NewGuid().ToString("N"));
        public string SessionRoot { get; }
        public ImportBackgroundInput Input { get; }
        public List<ImportUnit> Units { get; }
        public BackgroundOperationStore Background => new(Path.Combine(Root, "background.json"));
        public LiteraryProjectStore Projects => new(Path.Combine(Root, "projects.json"));
        public Fixture()
        {
            Directory.CreateDirectory(Root); var source = Path.Combine(Root, "input.json"); File.WriteAllText(source, "[]");
            using var session = ImportSession.Create(Root, source, CancellationToken.None); SessionRoot = session.Root;
            Units = Enumerable.Range(0, 3).Select(i => new ImportUnit("u" + i, "c", "m" + i, "", "assistant", i, 0,
                new string((char)('a' + i), 9000), false)).ToList();
            session.AddJson("normalized-v2", new ImportInput([new("c", "Chat", 3)], Units, [], []));
            Input = new(session.State.Id, session.State.SourceHash,
                new("draft", Root, source, "Imported", "Book", SessionRoot, "analysis", ["c"], DateTimeOffset.Now),
                "analysis", false, ["c"], "", null, "", "", "");
        }
        public LiteraryImportControl Control(Backend backend)
        {
            var control = new LiteraryImportControl(key => key, "ru", Root, Projects,
                new LiteraryImportDraftStore(Path.Combine(Root, "drafts.json")));
            typeof(LiteraryImportControl).GetProperty("BackgroundInference", Private)!.SetValue(control, (ImportInference)backend.Infer);
            return control;
        }
        public void Dispose()
        {
            Host(null); var path = Path.GetFullPath(Root); var temporary = Path.GetFullPath(Path.GetTempPath());
            if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("lopata-background-import-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
}
