using System.Reflection;
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

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageReferenceTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "lopata-reference-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static ImageAnalysisBundleInstallationSnapshot Ready => new() { State = ImageAnalysisBundleInstallStates.Ready };
    private static ImageAnalysisBundleInstallationSnapshot Missing => new() { State = ImageAnalysisBundleInstallStates.DownloadRequired };

    [TestMethod]
    public Task SeveralReferencesAppendWithOneUndoEachWithoutGeneratingImages() => OnUi(async () =>
    {
        var analyzer = new Analyzer(); var checks = 0; var path = Picture();
        var control = Control(analyzer, () => { checks++; return Ready; }, _ => Task.FromResult(Ready), () => Task.FromResult<ImageReferenceSelection?>(new(path, ImageReferenceScope.Characters)));
        var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "  Мой замысел\n ";
        try
        {
            await Attach(control); var first = input.Text;
            Assert.AreEqual("  Мой замысел\n \n\nФрагмент", first);
            analyzer.Answer = "Другой фон"; await Attach(control);
            Assert.AreEqual(first + "\n\nДругой фон", input.Text);
            control.ClearWorkspace(); control.Localize(new LocalizationService().T, "en");
            Assert.AreSame(input, Element<TextBox>(control, "Generation.Prompt"));
            input.Undo(); Assert.AreEqual(first, input.Text); input.Undo(); Assert.AreEqual("  Мой замысел\n ", input.Text);
            input.Redo(); input.Redo(); Assert.AreEqual(first + "\n\nДругой фон", input.Text);
            Assert.AreEqual(1, checks); Assert.HasCount(2, analyzer.Requests);
            Assert.AreNotEqual(analyzer.Requests[0].Id, analyzer.Requests[1].Id);
            Assert.HasCount(0, ImageGenerationSessionStore.Load(analyzer.Requests[0].SessionDirectory).Turns);
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task PreparationIsOncePerChatAndSuccessfulDownloadRequiresAnotherClick() => OnUi(async () =>
    {
        var checks = 0; var prepares = 0; var selections = 0; var analyzer = new Analyzer();
        var control = Control(analyzer, () => { checks++; return Missing; }, _ => { prepares++; return Task.FromResult(Ready); }, () => { selections++; return Task.FromResult<ImageReferenceSelection?>(new(Picture(), ImageReferenceScope.Style)); });
        var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "draft";
        try
        {
            await Attach(control); Assert.AreEqual(1, checks); Assert.AreEqual(1, prepares); Assert.AreEqual(0, selections);
            Assert.AreEqual("draft", input.Text); Assert.HasCount(0, analyzer.Requests);
            control.ClearWorkspace(); control.Configure(new LocalizationService().T, new(), 4, "en");
            await Attach(control); Assert.AreEqual(1, selections); Assert.AreEqual(1, checks);
            await Attach(control); Assert.AreEqual(2, selections); Assert.AreEqual(1, checks);
            control.Restore(ImageGenerationSessionStore.Create(_root)); await Attach(control);
            Assert.AreEqual(2, checks); Assert.AreEqual(2, prepares); Assert.AreEqual(2, selections);
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task CancelledPreparationAndSelectionKeepDraftAndPermitRetryWithoutRecheck() => OnUi(async () =>
    {
        var checks = 0; var prepares = 0; var analyzer = new Analyzer();
        var control = Control(analyzer, () => { checks++; return Missing; }, _ => { prepares++; return Task.FromResult(prepares < 3 ? Missing : Ready); }, () => Task.FromResult<ImageReferenceSelection?>(null));
        var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "original";
        try
        {
            for (var i = 0; i < 4; i++) await Attach(control);
            Assert.AreEqual(1, checks); Assert.AreEqual(3, prepares); Assert.HasCount(0, analyzer.Requests);
            Assert.AreEqual("original", input.Text); Assert.IsTrue(input.IsEnabled);
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task BusyDuplicateCancellationErrorAndEmptyReplyPreserveDraft() => OnUi(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new Analyzer { Run = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return "unreachable"; } };
        var control = Control(analyzer); var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "original";
        try
        {
            var pending = Attach(control); await entered.Task; Assert.IsFalse(input.IsEnabled);
            Assert.IsFalse(Element<Button>(control, "Generation.Attach").IsEnabled); await Attach(control); Assert.HasCount(1, analyzer.Requests);
            Element<Button>(control, "Generation.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await pending;
            Assert.AreEqual("original", input.Text); Assert.IsTrue(input.IsEnabled);
            Assert.IsNull(ImageReferenceStore.Read(analyzer.Requests[0]));
            analyzer.Run = (_, _) => throw new InvalidOperationException("failed"); await Attach(control);
            analyzer.Run = (_, _) => Task.FromResult("<think>unfinished"); await Attach(control);
            Assert.AreEqual("original", input.Text); Assert.IsTrue(input.IsEnabled);
            Assert.IsNull(ImageReferenceStore.Read(analyzer.Requests.Last()));
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task CompletedReceiptRestoresOriginalAndFragmentWithoutInferenceOrDuplicateDelivery() => OnUi(async () =>
    {
        var request = await ImageReferenceStore.CreateAsync(ImageGenerationSessionStore.Create(_root), "draft", "en", new(Picture(), ImageReferenceScope.Background), CancellationToken.None);
        ImageReferenceStore.Save(request, "A stone garden"); File.Delete(request.ImagePath);
        var analyzer = new Analyzer(); var control = Control(analyzer);
        var state = new BackgroundOperationState { Id = request.Id, Kind = ImageReferenceAnalyzer.BackgroundKind, Title = "Reference", Project = request.SessionDirectory, Input = JsonSerializer.SerializeToElement(request) };
        try
        {
            await control.ResumeReferenceAsync(state, CancellationToken.None);
            var input = Element<TextBox>(control, "Generation.Prompt"); Assert.AreEqual("draft\n\nA stone garden", input.Text);
            Assert.HasCount(0, analyzer.Requests);
            var notice = new BackgroundOperationNotice(request.Id, state.Kind, state.Title, request.SessionDirectory);
            Assert.AreEqual(request, ImageReferenceStore.ReadRequest(notice.Project!, notice.Id));
            control.RestoreReferenceResult(notice); input.Undo(); Assert.AreEqual("draft", input.Text);
            control.RestoreReferenceResult(notice); Assert.AreEqual("draft", input.Text); // Explicit undo is respected.
            var fresh = Control(analyzer); fresh.RestoreReferenceResult(notice);
            Assert.AreEqual("draft\n\nA stone garden", Element<TextBox>(fresh, "Generation.Prompt").Text); fresh.DisposeRuntime();
            Assert.ThrowsExactly<InvalidDataException>(() => ImageReferenceStore.ReadRequest(_root, "../bad"));
            Assert.ThrowsExactly<InvalidDataException>(() => ImageReferenceStore.Read(request with { Draft = "different" }));
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task PauseAndResumeUseSharedControllerAndRetireOwnedModelBeforeCompletion() => OnUi(async () =>
    {
        var previous = ApplicationBackgroundOperations.Current; var previousToken = ApplicationBackgroundOperations.ExitToken;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "background.json")));
        ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var retired = 0;
        var analyzer = new Analyzer { Run = async (_, token) => { if (++calls == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } return "resumed"; } };
        ApplicationBackgroundOperations.RegisterModel(analyzer, _ => { retired++; return Task.CompletedTask; });
        var control = Control(analyzer); var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "draft";
        try
        {
            var pending = Attach(control); await entered.Task; await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.AreEqual("draft", input.Text); Assert.IsTrue(retired > 1);
            await controller.ResumeAsync(CancellationToken.None); await pending;
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase); Assert.AreEqual(2, calls);
            Assert.AreEqual(analyzer.Requests[0].Id, controller.State.Id); Assert.AreEqual("draft\n\nresumed", input.Text);
            Assert.AreEqual("resumed", ImageReferenceStore.Read(analyzer.Requests[0])); input.Undo(); Assert.AreEqual("draft", input.Text);
            Assert.IsTrue(retired >= 4);
        }
        finally { ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousToken; control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task RuntimeReceivesOnlyOneImageAndScopedRoleInCorrectLanguage() => OnUi(async () =>
    {
        var runtime = new BetaRuntime(); using var analyzer = new ImageReferenceAnalyzer(() => runtime);
        foreach (var scope in Enum.GetValues<ImageReferenceScope>())
        foreach (var language in new[] { "ru", "en" })
        {
            var request = await ImageReferenceStore.CreateAsync(ImageGenerationSessionStore.Create(_root), "PRIVATE DRAFT MUST NOT ENTER ANALYSIS", language, new(Picture(), scope), CancellationToken.None);
            Assert.AreEqual("visible fragment", await analyzer.GenerateAsync(request, CancellationToken.None));
            var call = runtime.Calls.Last(); Assert.AreEqual("analyze", call.Command); Assert.AreEqual(request.ImagePath, call.Image);
            Assert.HasCount(1, call.Messages); Assert.IsTrue(call.Messages[0].IncludesImage); Assert.AreEqual("user", call.Messages[0].Role);
            StringAssert.Contains(call.Messages[0].Content, language == "en" ? "English" : "Russian");
            Assert.IsFalse(call.Messages[0].Content.Contains(request.Draft, StringComparison.Ordinal));
        }
        Assert.HasCount(8, runtime.Calls); Assert.AreEqual(8, runtime.Prepares);
        Assert.AreEqual(8, runtime.Calls.Select(c => c.Messages).Distinct().Count());
    });

    [TestMethod]
    public Task DurableCopyDoesNotModifyOriginalAndRejectsChangedInputAndNonBetaRuntime() => OnUi(async () =>
    {
        var source = Picture(); var bytes = File.ReadAllBytes(source);
        var request = await ImageReferenceStore.CreateAsync(ImageGenerationSessionStore.Create(_root), "draft", "ru", new(source, ImageReferenceScope.Whole), CancellationToken.None);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source)); Assert.AreNotEqual(source, request.ImagePath);
        File.Delete(source); Assert.IsTrue(File.Exists(request.ImagePath));
        var runtime = new BetaRuntime(); using var analyzer = new ImageReferenceAnalyzer(() => runtime);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => analyzer.GenerateAsync(request with { Sha256 = "bad" }, CancellationToken.None)); Assert.AreEqual(0, runtime.Prepares);
        runtime.BundleId = ImageAnalysisBundleCatalog.LightId;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => analyzer.GenerateAsync(request, CancellationToken.None)); Assert.AreEqual(0, runtime.Prepares);
    });

    [TestMethod]
    public Task PreparationWindowReusesMotherControlAndClosesOnlyWhenReady() => OnUi(async () =>
    {
        var localizer = new LocalizationService(); localizer.Load("ru"); var calls = 0;
        var window = new ImageReferencePreparationWindow(Missing, localizer.T, (_, _, _) => Task.FromResult(++calls == 1 ? Missing : Ready));
        var mother = (ImageAnalysisBundleConfirmationControl)window.Content;
        Assert.AreEqual(Visibility.Collapsed, ((Button)mother.FindName("BackToBundlesButton")).Visibility);
        Assert.AreEqual(Visibility.Collapsed, ((Button)mother.FindName("RemoveVisionButton")).Visibility);
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000; window.WindowStartupLocation = WindowStartupLocation.Manual;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.ExecuteAsync(ImageAnalysisBundleActions.Download); Assert.IsTrue(window.IsVisible); Assert.IsFalse(window.Snapshot.CanStart);
                await window.ExecuteAsync(ImageAnalysisBundleActions.Verify); Assert.IsTrue(window.Snapshot.CanStart); done.SetResult();
            }
            catch (Exception error) { done.SetException(error); window.Close(); }
        };
        Assert.AreEqual(true, window.ShowDialog()); await done.Task; Assert.AreEqual(2, calls);
    });

    [TestMethod]
    public Task PreparationErrorOrCancellationCannotMarkBetaReadyAndCanBeRetried() => OnUi(async () =>
    {
        var localizer = new LocalizationService(); localizer.Load("en"); var failures = 0;
        var window = new ImageReferencePreparationWindow(Missing, localizer.T,
            (_, _, _) => throw (++failures == 1 ? new OperationCanceledException() : new IOException("broken")), () => Ready);
        var problems = new List<string>(); window.ReportProblem = problems.Add;
        await window.ExecuteAsync(ImageAnalysisBundleActions.Download); Assert.IsFalse(window.Snapshot.CanStart);
        await window.ExecuteAsync(ImageAnalysisBundleActions.Verify); Assert.IsFalse(window.Snapshot.CanStart);
        Assert.AreEqual(2, failures); Assert.HasCount(2, problems); window.Close();
    });

    [TestMethod]
    public Task ScopeSelectionAndThemeResourcesAreUsableInBothLanguages() => OnUi(() =>
    {
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var window = new ImageReferenceSelectionWindow(Picture(), localizer.T);
            window.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
            window.Resources["PanelBrush"] = new SolidColorBrush(dark ? Colors.DarkSlateGray : Colors.WhiteSmoke);
            window.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            window.Resources["LineBrush"] = new SolidColorBrush(Colors.Gray);
            var combo = Element<ComboBox>(window, "Generation.Reference.Scope"); Assert.AreEqual(4, combo.Items.Count);
            foreach (ComboBoxItem item in combo.Items) Assert.IsFalse(((string)item.Content).StartsWith("Generation.", StringComparison.Ordinal));
            Assert.IsFalse(window.Title.StartsWith("Generation.", StringComparison.Ordinal));
            window.Measure(new Size(620, 650)); window.Arrange(new Rect(0, 0, 620, 650)); window.UpdateLayout();
            Assert.AreEqual(dark ? Colors.Black : Colors.White, ((SolidColorBrush)window.Background).Color); window.Close();
        }
        return Task.CompletedTask;
    });

    private ImageGenerationControl Control(Analyzer analyzer, Func<ImageAnalysisBundleInstallationSnapshot>? check = null,
        Func<ImageAnalysisBundleInstallationSnapshot, Task<ImageAnalysisBundleInstallationSnapshot>>? prepare = null,
        Func<Task<ImageReferenceSelection?>>? select = null)
    {
        var localizer = new LocalizationService(); localizer.Load("ru"); var control = new ImageGenerationControl();
        control.ConfigureReferences(analyzer, check ?? (() => Ready), prepare ?? (_ => Task.FromResult(Ready)),
            select ?? (() => Task.FromResult<ImageReferenceSelection?>(new(Picture(), ImageReferenceScope.Whole))));
        control.Configure(localizer.T, new(), 4); control.Restore(ImageGenerationSessionStore.Create(_root)); return control;
    }
    private string Picture()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output); return path;
    }
    private sealed class Analyzer : IImageReferenceAnalyzer
    {
        public string Answer { get; set; } = "Фрагмент";
        public List<ImageReferenceRequest> Requests { get; } = [];
        public Func<ImageReferenceRequest, CancellationToken, Task<string>>? Run { get; set; }
        public Task<string> GenerateAsync(ImageReferenceRequest request, CancellationToken token) { Requests.Add(request); return Run?.Invoke(request, token) ?? Task.FromResult(Answer); }
    }
    private sealed class BetaRuntime : IOmniTextRuntime
    {
        public string BundleId { get; set; } = ImageAnalysisBundleCatalog.MediumId;
        public string RuntimeVersion => "test"; public string DeviceMapJson => "{}"; public bool IsReady => true;
        public ImageAnalysisHeavyResourcePlan? CurrentPlan => null; public int Prepares { get; private set; }
        public List<(string Command, string Image, IReadOnlyList<ImageAnalysisHiddenMessage> Messages)> Calls { get; } = [];
        public Task<OmniWarmupResult> PrepareAsync(Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken token, bool reuseCurrentPlan = false) { Prepares++; return Task.FromResult<OmniWarmupResult>(null!); }
        public Task<OmniTextGenerationResult> GenerateAsync(string command, string imagePath, IReadOnlyList<ImageAnalysisHiddenMessage> conversation, IProgress<ModelStreamChunk>? progress, CancellationToken token, Action<string>? responseReceived = null, Action<string>? diagnosticReceived = null)
        { Calls.Add((command, imagePath, conversation)); return Task.FromResult(new OmniTextGenerationResult("<think>reasoning</think>visible fragment", 1, 1, 1, 4096, "stop", 1, 1, 1, 1, 1, "beta", "test")); }
        public Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(CancellationToken token) => Task.FromResult<ImageAnalysisHeavyResourceStatus>(null!);
        public void Stop() { } public void Dispose() { }
    }
    private static Task Attach(ImageGenerationControl control) => (Task)typeof(ImageGenerationControl).GetMethod("AttachReferenceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, null)!;
    private static T Element<T>(DependencyObject root, string id) where T : DependencyObject => Descendants(root).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant; }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.SetResult(); } catch (Exception error) { done.SetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
