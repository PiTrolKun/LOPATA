using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    [DataRow("ru", true)]
    [DataRow("ru", false)]
    [DataRow("en", true)]
    [DataRow("en", false)]
    public Task StreamingKeepsNextDraftEditableAndNextTurnIncludesTheCompletedExchange(string language, bool dark) => Sta(() =>
    {
        using var fixture = new Fixture(language, dark);
        fixture.Input.Text = "Today was difficult.";
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 1 && fixture.AssistantTexts.Contains("I am listening. "));
        Assert.IsTrue(fixture.Control.IsWorking);
        Assert.IsFalse(fixture.Runtime.Replies[0].Task.IsCompleted);
        Assert.IsTrue(fixture.Input.IsEnabled);
        Assert.IsFalse(fixture.Input.IsReadOnly);
        Assert.AreEqual("", fixture.Input.Text);
        Assert.IsFalse(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.IsTrue(fixture.Button("LetItOut.Stop").IsEnabled);
        fixture.Input.Text = "The meeting was the hardest part.";
        fixture.Click("LetItOut.Send"); // Even a queued/double event must not start a second request.
        Assert.AreEqual(1, fixture.Runtime.Requests.Count);
        Screenshot(fixture.Window, language + "-" + dark + "-stream.png");
        fixture.Window.Width = 620;
        fixture.Window.Height = 700;
        fixture.Window.UpdateLayout();
        Assert.IsTrue(fixture.Input.ActualWidth > 100);
        Assert.IsTrue(fixture.Input.ActualWidth < fixture.Window.ActualWidth);
        Screenshot(fixture.Window, language + "-" + dark + "-compact.png");

        fixture.Runtime.Complete(0, "I am listening. What felt most difficult?");
        Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual("The meeting was the hardest part.", fixture.Input.Text);
        Assert.IsTrue(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.IsFalse(fixture.Button("LetItOut.Stop").IsEnabled);
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 2);
        var messages = fixture.Runtime.Requests[1].Messages;
        CollectionAssert.AreEqual(new[] { "user", "assistant", "user" }, messages.Select(message => message.Role).ToArray());
        CollectionAssert.AreEqual(new[] { "Today was difficult.", "I am listening. What felt most difficult?", "The meeting was the hardest part." },
            messages.Select(message => message.Content).ToArray());
        Assert.AreEqual(language, fixture.Runtime.Requests[1].Language);
        fixture.Runtime.Complete(1, "Tell me about that meeting.");
        Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual(2, fixture.Completed.Count);
        Assert.IsTrue(fixture.Completed.All(checkpoint => checkpoint.Completed));
        Assert.AreEqual(0, fixture.Runtime.Retirements, "Successful turns keep the core ready for the next message.");
    });

    [TestMethod]
    public Task StopLabelsPartialAndNextRequestDoesNotTreatItAsAnAssistantAnswer() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true);
        fixture.Input.Text = "Unfinished first topic.";
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 1 && fixture.AssistantTexts.Contains("I am listening. "));
        fixture.Input.Text = "Please listen to this instead.";
        fixture.Click("LetItOut.Stop");
        Pump(() => !fixture.Control.IsWorking && !fixture.Controller.BlocksNewWork);
        Assert.AreEqual(0, fixture.Completed.Count);
        Assert.IsTrue(fixture.AssistantTexts.Contains("I am listening. "));
        Assert.IsTrue(fixture.Descendants.OfType<TextBlock>().Any(text => text.Text == fixture.Localize("LetItOut.Incomplete")));
        Assert.AreEqual(fixture.Localize("LetItOut.Stopped"), fixture.StatusDetail);
        Assert.AreEqual("Please listen to this instead.", fixture.Input.Text);
        Assert.IsTrue(fixture.Runtime.Retirements > 0, "Stopping a turn must retire its runtime.");

        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 2 || !fixture.Control.IsWorking);
        Assert.AreEqual(2, fixture.Runtime.Requests.Count, "A stopped turn must not make the next conversation request invalid.");
        Assert.IsFalse(fixture.Runtime.Requests[1].Messages.Any(message => message.Role == "assistant"));
        Assert.IsFalse(fixture.Runtime.Requests[1].Messages.Any(message => message.Content.Contains("I am listening.", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "Unfinished first topic.", "Please listen to this instead." },
            fixture.Runtime.Requests[1].Messages.Select(message => message.Content).ToArray());
        Assert.AreEqual("Please listen to this instead.", fixture.Runtime.Requests[1].Messages[^1].Content);
        fixture.Runtime.Complete(1, "I am here to listen.");
        Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual(1, fixture.Completed.Count);
    });

    [TestMethod]
    public Task LanguageRefreshPreservesConversationAndDraftWhileNewConversationClearsBoth() => Sta(() =>
    {
        using var fixture = new Fixture("ru", false);
        fixture.Input.Text = "First topic.";
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 1);
        fixture.Runtime.Complete(0, "First complete answer.");
        Pump(() => !fixture.Control.IsWorking);
        var previousSession = fixture.Control.SessionId;
        fixture.Input.Text = "Unsaved next draft.";
        fixture.Language = "en";
        fixture.Control.Configure(fixture.Localize);
        Assert.AreEqual(previousSession, fixture.Control.SessionId);
        Assert.AreEqual("Unsaved next draft.", fixture.Input.Text);
        Assert.IsTrue(fixture.AssistantTexts.Contains("First complete answer."));
        Assert.IsTrue(fixture.Descendants.OfType<TextBox>().Any(text => text.Text == "First topic."));
        Assert.IsInstanceOfType<System.Windows.Shapes.Path>(fixture.Button("LetItOut.Send").Content);
        Assert.AreEqual(fixture.Localize("LetItOut.Send"), AutomationProperties.GetName(fixture.Button("LetItOut.Send")));
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 2);
        Assert.AreEqual("en", fixture.Runtime.Requests[1].Language);
        fixture.Runtime.Complete(1, "Second complete answer.");
        Pump(() => !fixture.Control.IsWorking);

        fixture.Input.Text = "Draft to discard.";
        fixture.Click("LetItOut.New");
        Assert.AreNotEqual(previousSession, fixture.Control.SessionId);
        Assert.AreEqual("", fixture.Input.Text);
        Assert.AreEqual(0, fixture.AssistantTexts.Length);
        Assert.IsFalse(fixture.Descendants.OfType<TextBox>().Any(text => text.Text == "First topic."));
        fixture.Input.Text = "A different conversation.";
        fixture.Click("LetItOut.Send");
        Pump(() => fixture.Runtime.Requests.Count == 3);
        Assert.AreEqual(1, fixture.Runtime.Requests[2].Messages.Count);
        Assert.AreNotEqual(previousSession, fixture.Runtime.Requests[2].SessionId);
        fixture.Runtime.Complete(2, "A fresh answer.");
        Pump(() => !fixture.Control.IsWorking);
    });

    [TestMethod]
    public Task RestoredParkedConversationKeepsPartialAndBlocksFreshSendAndNew() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true);
        var request = new LetItOutChatRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            Path.Combine(Path.GetTempPath(), LetItOutModel.FileName),
            [new("user", "Pending user message.")], "ru");
        var checkpoint = new LetItOutChatCheckpoint(request, "A saved incomplete answer.", null, false);
        var state = new BackgroundOperationState { Id = request.TurnId, Kind = LetItOutChatWork.Kind,
            Title = "Paused conversation", Project = request.SessionId, Private = true, UserPaused = true,
            Phase = BackgroundOperationPhase.Paused, Input = JsonSerializer.SerializeToElement(request),
            Checkpoint = JsonSerializer.SerializeToElement(checkpoint) };
        fixture.Store.Save(state);
        fixture.Controller.Load();
        Assert.IsFalse(fixture.Controller.BlocksNewWork, "The fixture must represent a safely parked job, not a global block.");
        var resident = new GateResident(); fixture.Control.ConfigureWarmup(resident);
        Assert.HasCount(0, resident.Calls, "Revealing a saved conversation must not silently warm or resume it.");
        fixture.Control.ShowPending(fixture.Controller.FindOperations(LetItOutChatWork.Kind, request.SessionId).Single());
        fixture.Input.Text = "Keep this as an editable draft.";
        Assert.IsFalse(fixture.Control.IsWorking);
        Assert.IsTrue(fixture.AssistantTexts.Contains(checkpoint.Partial));
        Assert.IsTrue(fixture.Descendants.OfType<TextBox>().Any(text => text.Text == "Pending user message."));
        Assert.AreEqual(fixture.Localize("LetItOut.Paused"), fixture.StatusDetail);
        Assert.IsFalse(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.IsFalse(fixture.Button("LetItOut.New").IsEnabled);
        fixture.Click("LetItOut.Send"); fixture.Click("LetItOut.New");
        Assert.AreEqual(0, fixture.Runtime.Requests.Count);
        Assert.AreEqual(request.SessionId, fixture.Control.SessionId);
        Assert.AreEqual("Keep this as an editable draft.", fixture.Input.Text);
        Assert.IsTrue(fixture.AssistantTexts.Contains(checkpoint.Partial));
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task EntryWarmsWithoutASyntheticMessageAndKeepsTheDraftEditable(string language) => Sta(() =>
    {
        using var fixture = new Fixture(language, true); var resident = new GateResident();
        fixture.Control.ConfigureWarmup(resident);
        Pump(() => resident.Calls.Count == 1);
        fixture.Input.Text = "Draft while Gemma is loading.";
        Assert.IsTrue(fixture.Input.IsEnabled);
        Assert.IsFalse(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.AreEqual(fixture.Localize("LetItOut.Warming"), fixture.StatusDetail);
        Assert.AreEqual(0, fixture.Runtime.Requests.Count);
        Assert.HasCount(0, fixture.AssistantTexts);
        fixture.Control.ConfigureWarmup(resident);
        Assert.AreEqual(1, resident.Calls.Count);
        resident.Calls[0].SetResult(); Pump(() => !fixture.Control.IsWarming);
        Assert.IsTrue(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.AreEqual("Draft while Gemma is loading.", fixture.Input.Text);
        fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
        Assert.HasCount(1, fixture.Runtime.Requests[0].Messages);
        fixture.Runtime.Complete(0, "A real answer."); Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual(1, resident.Calls.Count);
    });

    [TestMethod]
    public Task BusyEntryWaitsAndLeavingCancelsBeforeASecondEntryWarms() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true); var resident = new GateResident();
        using (fixture.Controller.BeginAuxiliary()) {
            fixture.Control.ConfigureWarmup(resident);
            Assert.HasCount(0, resident.Calls);
            Assert.AreEqual(fixture.Localize("LetItOut.WarmDeferred"), fixture.StatusDetail);
        }
        Pump(() => resident.Calls.Count == 1);
        fixture.Input.Text = "Preserve this draft.";
        fixture.Window.Hide(); fixture.Window.Show();
        Pump(() => resident.Calls.Count == 2);
        Assert.AreEqual(1, resident.Cancellations);
        resident.Calls[1].SetResult(); Pump(() => !fixture.Control.IsWarming);
        Assert.AreEqual("Preserve this draft.", fixture.Input.Text);
        Assert.AreEqual(0, fixture.Runtime.Requests.Count);
    });

    [TestMethod]
    public Task WarmupUsesCommonPauseResumeAndStopDoesNotRestartIt() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true); var resident = new GateResident();
        fixture.Control.ConfigureWarmup(resident); Pump(() => resident.Calls.Count == 1);
        var pause = fixture.Controller.PauseAsync();
        Pump(() => pause.IsCompleted && fixture.Controller.State?.Phase == BackgroundOperationPhase.Paused
            && fixture.StatusDetail == fixture.Localize("LetItOut.WarmPaused"));
        Assert.AreEqual(fixture.Localize("LetItOut.WarmPaused"), fixture.StatusDetail);
        var resume = fixture.Controller.ResumeAsync(CancellationToken.None);
        Pump(() => resident.Calls.Count == 2);
        fixture.Click("LetItOut.Stop");
        Pump(() => !fixture.Control.IsWarming && !fixture.Controller.BlocksNewWork && resume.IsCompleted);
        Assert.AreEqual(2, resident.Calls.Count);
        Assert.AreEqual(fixture.Localize("LetItOut.WarmStopped"), fixture.StatusDetail);
        Assert.AreEqual(0, fixture.Runtime.Requests.Count);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task FailedWarmupPreservesDraftAndResumesOnlyByExplicitDecision(bool memory) => Sta(() =>
    {
        using var fixture = new Fixture("en", true); var resident = new GateResident();
        fixture.Controller.Register(LetItOutChatWarmup.Kind, fixture.Control.ResumeWarmupAsync);
        fixture.Control.ConfigureWarmup(resident); Pump(() => resident.Calls.Count == 1);
        fixture.Input.Text = "Keep my private draft.";
        resident.Calls[0].SetException(memory ? new LetItOutMemoryException("LetItOut.RamRequired") : new IOException("Warmup fixture failure."));
        Pump(() => !fixture.Control.IsWarming && fixture.Controller.State?.Phase == BackgroundOperationPhase.Waiting);
        Assert.AreEqual(fixture.Localize(memory ? "LetItOut.RamRequired" : "LetItOut.WarmFailed"), fixture.StatusDetail);
        Assert.AreEqual("Keep my private draft.", fixture.Input.Text);
        Assert.IsTrue(fixture.Controller.State!.Private);
        Assert.IsFalse(fixture.Controller.State.Input.GetRawText().Contains(fixture.Input.Text, StringComparison.Ordinal));
        var resume = fixture.Controller.ResumeAsync(fixture.Controller.State.Id, CancellationToken.None);
        Pump(() => resident.Calls.Count == 2);
        resident.Calls[1].SetResult(); Pump(() => !fixture.Control.IsWarming && resume.IsCompleted);
        Assert.IsTrue(fixture.Button("LetItOut.Send").IsEnabled);
        Assert.AreEqual("Keep my private draft.", fixture.Input.Text);
    });

    private sealed class GateResident : ILetItOutChatResident
    {
        internal List<TaskCompletionSource> Calls { get; } = [];
        internal int Cancellations { get; private set; }
        public async Task WarmAsync(string path, CancellationToken token)
        {
            var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Calls.Add(call);
            try { await call.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancellations++; throw; }
        }
        public Task RetireAsync() => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-let-it-out-ui-" + Guid.NewGuid().ToString("N"));
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        private readonly CancellationToken _previousExit = ApplicationBackgroundOperations.ExitToken;
        private readonly Dictionary<string, LocalizationService> _localizers = [];
        internal string Language { get; set; }
        internal GateRuntime Runtime { get; } = new();
        internal BackgroundOperationStore Store => new(Path.Combine(_root, "operation.json"));
        internal BackgroundOperationController Controller { get; }
        internal LetItOutChatControl Control { get; }
        internal Window Window { get; }
        internal List<LetItOutChatCheckpoint> Completed { get; } = [];
        internal IEnumerable<DependencyObject> Descendants => ScenarioNavigationTests.LogicalDescendants(Control);
        internal TextBox Input => Descendants.OfType<TextBox>().Single(element => AutomationProperties.GetAutomationId(element) == "LetItOut.Input");
        internal TextBlock Status => Descendants.OfType<TextBlock>().Single(element => AutomationProperties.GetAutomationId(element) == "LetItOut.Status");
        internal string StatusDetail => AutomationProperties.GetHelpText(Status);
        internal string[] AssistantTexts => Descendants.OfType<TextBox>().Where(element => AutomationProperties.GetAutomationId(element) == "LetItOut.AssistantMessage").Select(element => element.Text).ToArray();
        internal Button Button(string id) => Descendants.OfType<Button>().Single(element => AutomationProperties.GetAutomationId(element) == id);
        internal void Click(string id) => Button(id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        internal string Localize(string key) => _localizers[Language].T(key);

        internal Fixture(string language, bool dark, LetItOutConversationStore? history = null, bool memory = false, bool context = false)
        {
            Language = language;
            Runtime.MemoryStore = memory ? history : null;
            Runtime.HasContext = context;
            foreach (var item in new[] { "ru", "en" }) { var localizer = new LocalizationService(); localizer.Load(item); _localizers[item] = localizer; }
            Controller = new(Store);
            ApplicationBackgroundOperations.Current = Controller;
            ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            Control = new(Localize);
            foreach (var key in new[] { "PanelBrush", "InputBackgroundBrush" }) Control.Resources[key] = dark ? Brushes.Black : Brushes.White;
            Control.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
            Control.Resources["TextSecondaryBrush"] = Control.Resources["LineBrush"] = Brushes.Gray;
            Control.Resources["PrimaryButtonStyle"] = new Style(typeof(Button));
            Control.Resources["SecondaryButtonStyle"] = new Style(typeof(Button));
            Control.ConfigureRuntime(() => Path.Combine(_root, LetItOutModel.FileName), new LetItOutChatWork(Runtime), () => Language);
            if (history is not null) Control.ConfigureHistory(history);
            Control.AnswerCompleted += Completed.Add;
            Window = new() { Content = Control, Width = 1100, Height = 780, ShowInTaskbar = false, ShowActivated = false,
                Background = dark ? Brushes.Black : Brushes.White };
            Window.Show(); Window.UpdateLayout();
        }

        public void Dispose()
        {
            try
            {
                Control.Dispose();
                foreach (var reply in Runtime.Replies) reply.TrySetResult("Cleanup answer.");
                foreach (var reply in Runtime.TitleReplies) reply.TrySetResult("Cleanup title");
                Pump(() => !Control.IsWorking && !Control.IsWarming);
                Window.Close();
            }
            finally
            {
                ApplicationBackgroundOperations.Current = _previous;
                ApplicationBackgroundOperations.ExitToken = _previousExit;
                var resolved = Path.GetFullPath(_root);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("lopata-let-it-out-ui-", StringComparison.Ordinal)) throw new InvalidOperationException();
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }
    }

    private sealed class GateRuntime : ILetItOutChatRuntime, ILetItOutChatTitleRuntime, ILetItOutMemoryRuntime, ILetItOutContextRuntime
    {
        public bool HasContext { get; set; }
        internal List<LetItOutChatRequest> ContextRequests { get; } = [];
        internal List<LetItOutChatRequest> TrimRequests { get; } = [];
        internal TaskCompletionSource<LetItOutContextInfo>? DelayedContext { get; set; }
        public async Task<LetItOutContextInfo> MeasureContextAsync(LetItOutChatRequest request,CancellationToken token)
        {
            ContextRequests.Add(request);
            if (DelayedContext is { } late) return await late.Task;
            token.ThrowIfCancellationRequested(); return new(6000,10000,true,false);
        }
        public Task<LetItOutContextInfo> TrimContextAsync(LetItOutChatRequest request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); TrimRequests.Add(request);
            return Task.FromResult(new LetItOutContextInfo(5000,10000,false,true));
        }
        internal LetItOutConversationStore? MemoryStore { get; set; }
        public bool HasMemory => MemoryStore is not null;
        internal List<LetItOutChatRequest> PairSummaries { get; } = [];
        internal List<LetItOutMemoryCloseRequest> MemoryCloses { get; } = [];
        internal TaskCompletionSource? MemoryCloseGate { get; set; }
        public Task SummarizePairAsync(LetItOutChatRequest request, CancellationToken token)
        {
            if (!HasMemory) return Task.CompletedTask;
            token.ThrowIfCancellationRequested(); PairSummaries.Add(request);
            var pair = MemoryStore!.MemoryPairs(request.SessionId).Last();
            Assert.IsTrue(pair.Answer.Length > 0); Assert.IsTrue(pair.Number > 0);
            if (MemoryStore.TryClaimPair(request.SessionId, pair.Number)) MemoryStore.SetPairHeading(request.SessionId, pair, "Automatic topic heading");
            return Task.CompletedTask;
        }
        public async Task CloseMemoryAsync(LetItOutMemoryCloseRequest request, BackgroundOperationState? restored, CancellationToken token)
        {
            MemoryCloses.Add(request); MemoryStore!.ClaimMemoryRun(request.ConversationId, request.RunId);
            if (MemoryCloseGate is { } gate) await gate.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var run = MemoryStore.MemoryRun(request.ConversationId, request.RunId)!;
            var pairs = MemoryStore.MemoryPairs(request.ConversationId).Where(p => p.Number <= run.LastPair).ToArray();
            Assert.IsTrue(MemoryStore.PublishMemorySnapshot(request.ConversationId, run.LastPair, "Brief topic", "Expanded history", pairs));
        }
        internal List<LetItOutChatRequest> Requests { get; } = [];
        internal List<TaskCompletionSource<string>> Replies { get; } = [];
        internal List<LetItOutTitleRequest> TitleRequests { get; } = [];
        internal List<TaskCompletionSource<string>> TitleReplies { get; } = [];
        internal int Retirements { get; private set; }
        public async Task<string> GenerateAsync(LetItOutChatRequest request, IProgress<ModelStreamChunk> progress, CancellationToken token)
        {
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Replies.Add(reply); Requests.Add(request);
            progress.Report(new("I am listening. "));
            return await reply.Task.WaitAsync(token);
        }
        internal void Complete(int index, string answer) => Replies[index].TrySetResult(answer);
        public async Task<string> GenerateTitleAsync(LetItOutTitleRequest request, CancellationToken token)
        {
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            TitleRequests.Add(request); TitleReplies.Add(reply); return await reply.Task.WaitAsync(token);
        }
        public Task RetireAsync() { Retirements++; return Task.CompletedTask; }
    }

    private static Task Sta(Action action) => ScenarioNavigationTests.Sta(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { action(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static void Pump(Func<bool> condition, int timeout = 5000)
    {
        if (condition()) return;
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || clock.ElapsedMilliseconds > timeout) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(condition(), "The conversation UI did not reach the expected state.");
    }

    private static void Screenshot(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var directory = Path.Combine(Path.GetTempPath(), "lopata-let-it-out-display-ui"); Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
