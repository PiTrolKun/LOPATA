using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundStudioTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static Task Invoke(LiteraryStudioControl control, string method, params object?[] args)
        => (Task)typeof(LiteraryStudioControl).GetMethod(method, Private)!.Invoke(control, args)!;
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

    [TestMethod]
    public Task PausedDirectHandoffRestoresWriterWithoutRepeatingConfirmedTask() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var lifetime = new CancellationTokenSource();
        var controller = new BackgroundOperationController(fixture.Store); Host(controller, lifetime.Token);
        var firstBackend = new Backend { DelayWriter = true };
        var first = fixture.Control(firstBackend); first.State.Input = "AUTHOR_REQUEST";
        var send = Invoke(first, "SendAsync", true);
        await firstBackend.WriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, first.State.BackgroundOperationStep);
        Assert.AreEqual("MODEL_TASK", first.State.Task);
        Assert.IsTrue(first.State.Messages.Any(m => m.Role == "Writer" && !m.Complete));
        controller.CheckpointForExit(); lifetime.Cancel(); await send.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = fixture.Store.Load()!; Assert.IsTrue(saved.UserPaused);
        var secondController = new BackgroundOperationController(fixture.Store); secondController.Load(); Host(secondController);
        var secondBackend = new Backend(); var second = fixture.Control(secondBackend);
        await Invoke(second, "ResumeBackgroundRequestAsync", secondController.State, CancellationToken.None);
        Assert.AreEqual(0, secondBackend.AdvisorCalls); Assert.AreEqual(1, secondBackend.WriterCalls);
        Assert.AreEqual("FINAL_TEXT", second.State.Result);
        Assert.AreEqual(1, second.State.Messages.Count(m => m.Role == "Task" && m.Complete));
        Assert.AreEqual(saved.Id, second.State.Messages.Last(m => m.Role == "Writer" && m.Complete).BackgroundOperationId);
        Assert.AreEqual(BackgroundOperationPhase.Completed, fixture.Store.Load()!.Phase);
    });

    [TestMethod]
    public Task RefusedGenerationReturnsInputAndAllowsContextRecovery() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Store); Host(controller);
        var control = fixture.Control(new Backend { Failure = new InvalidOperationException("REFUSED") });
        control.State.Input = "DO_NOT_LOSE"; await Invoke(control, "SendAsync", false);
        Assert.AreEqual("DO_NOT_LOSE", control.State.Input); Assert.IsNull(control.State.Pending);
        Assert.IsFalse(controller.HasPending); Assert.IsFalse(control.State.Messages.Single(m => m.Role == "User").InContext);
    });

    [TestMethod]
    public Task HiddenDecisionStaysPendingAndDoesNotBecomeSuccessfulResult() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Store); Host(controller);
        var control = fixture.Control(new Backend { Failure = new BackgroundOperationWaitingException("Tray.NeedsInput") });
        control.State.Input = "QUESTION"; await Invoke(control, "SendAsync", false);
        Assert.IsTrue(controller.HasPending); Assert.IsTrue(controller.State!.RequiresDecision);
        Assert.AreEqual("QUESTION", control.State.Input); Assert.IsFalse(controller.State.NeedsAttention);
    });

    [TestMethod]
    public Task OlderResultCanBeViewedAfterStartingAnotherSession() => Sta(async () =>
    {
        using var fixture = new Fixture(); var controller = new BackgroundOperationController(fixture.Store); Host(controller);
        var control = fixture.Control(new Backend()); control.State.Input = "FIRST";
        await Invoke(control, "SendAsync", false); var oldId = controller.State!.Id;
        control.State.Clear(); control.State.Input = "SECOND"; await Invoke(control, "SendAsync", false);
        var notice = new BackgroundOperationNotice(oldId, "literary.studio", "first", fixture.Root);
        var view = typeof(LiteraryStudioControl).GetMethod("ViewBackgroundResult", Private)!;
        Assert.IsTrue((bool)view.Invoke(control, [notice])!);
        Assert.IsFalse((bool)view.Invoke(control, [notice with { Id = Guid.NewGuid().ToString("N") }])!);
    });

    private sealed class Backend : ILiteraryStudioRequests
    {
        public bool IsBusy => false;
        public int ContextCapacity => 16384;
        public bool DelayWriter { get; init; }
        public Exception? Failure { get; init; }
        public int AdvisorCalls, WriterCalls;
        public TaskCompletionSource WriterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ParagraphReply> StudioAsync(StudioRequest request, Func<string,string> localize,
            Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk> progress,
            CancellationToken cancellation, Action? attemptStarting = null, Action? preparationStarted = null)
        {
            if (Failure is not null) throw Failure;
            if (request.Base.Role == LiteraryChatProfile.Advisor) { AdvisorCalls++; return new("MODEL_TASK", [], new([], [])); }
            WriterCalls++;
            if (DelayWriter)
            {
                progress.Report(new("PARTIAL_TEXT")); WriterStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return new("FINAL_TEXT", [], new([], []));
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-background-studio-" + Guid.NewGuid().ToString("N"));
        public BackgroundOperationStore Store => new(Path.Combine(Root, "background.json"));
        private readonly List<LiteraryChatRuntime> _runtimes = [];
        public Fixture()
        {
            Directory.CreateDirectory(Root); var project = new LiteraryProject { Genres = ["comedy"] };
            File.WriteAllText(Path.Combine(Root, "project.json"), JsonSerializer.Serialize(project));
            new LiteraryProjectLayout(Root).Initialize(); var chapters = new LiteraryChapterStore(Root); chapters.Open(); chapters.Save("DRAFT");
        }
        public LiteraryStudioControl Control(Backend requests)
        {
            var draft = new LiteraryDraftControl(Root, key => key); var runtime = new LiteraryChatRuntime(Root); _runtimes.Add(runtime);
            var control = new LiteraryStudioControl(Root, draft, new ContentControl(), runtime, () => false,
                key => key, "ru", new TextBlock(), new TextBlock(), requests);
            control.AttentionNotification = (_, _) => { }; return control;
        }
        public void Dispose()
        {
            Host(null); foreach (var runtime in _runtimes) runtime.Dispose();
            var path = Path.GetFullPath(Root); var temporary = Path.GetFullPath(Path.GetTempPath());
            if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("lopata-background-studio-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
}
