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
public sealed class ImagePromptAssistantTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-prompt-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public Task ReplacementsAreSingleUndoStepsAndSurviveRerenderAndManualEdits() => OnUi(async () =>
    {
        var assistant = new Assistant { Answer = "Подробный кот в саду" }; var control = Control(assistant);
        var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "  Кот\nв саду  ";
        await Assist(control); Assert.AreEqual("  Кот\nв саду  ", assistant.Requests[0].Prompt);
        Assert.AreEqual(assistant.Answer, input.Text); Assert.AreSame(input, Element<TextBox>(control, "Generation.Prompt"));
        Assert.HasCount(0, ImageGenerationSessionStore.Load(assistant.Requests[0].SessionDirectory).Turns);
        input.Undo(); Assert.AreEqual("  Кот\nв саду  ", input.Text);
        input.Redo(); Assert.AreEqual(assistant.Answer, input.Text);
        assistant.Answer = "Ещё более подробный кот"; await Assist(control);
        var localizer = new LocalizationService(); localizer.Load("en"); control.Localize(localizer.T, "en"); control.ClearWorkspace();
        Assert.AreSame(input, Element<TextBox>(control, "Generation.Prompt"));
        input.Undo(); Assert.AreEqual("Подробный кот в саду", input.Text);
        input.Undo(); Assert.AreEqual("  Кот\nв саду  ", input.Text);
        input.Redo(); input.Redo(); input.CaretIndex = input.Text.Length; input.SelectedText = "!";
        assistant.Answer = "Последняя версия"; await Assist(control);
        input.Undo(); Assert.AreEqual("Ещё более подробный кот!", input.Text);
        control.DisposeRuntime();
    });

    [TestMethod]
    public Task EmptyInputCreatesRandomPromptAndUndoRestoresEmptyOrWhitespace() => OnUi(async () =>
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var assistant = new Assistant(); var control = Control(assistant, language);
            var input = Element<TextBox>(control, "Generation.Prompt");
            foreach (var original in new[] { "", " \n " })
            {
                input.Text = original; await Assist(control);
                Assert.AreEqual(original, assistant.Requests.Last().Prompt);
                Assert.AreEqual(language, assistant.Requests.Last().LanguageCode);
                input.Undo(); Assert.AreEqual(original, input.Text);
            }
            Assert.AreNotEqual(assistant.Requests[0].Id, assistant.Requests[1].Id);
            Assert.IsTrue(Element<Button>(control, "Generation.Enhance").IsEnabled);
            Assert.IsTrue(SpellCheck.GetIsEnabled(input)); control.DisposeRuntime();
        }
    });

    [TestMethod]
    public Task ErrorCancellationAndDuplicateClicksKeepOriginalDraft() => OnUi(async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new Assistant { Run = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return "unreachable"; } };
        var control = Control(assistant); var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "Мой текст";
        var pending = Assist(control); await entered.Task;
        Assert.IsFalse(input.IsEnabled); Assert.IsFalse(Element<Button>(control, "Generation.Enhance").IsEnabled);
        await Assist(control); Assert.HasCount(1, assistant.Requests);
        Element<Button>(control, "Generation.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await pending;
        Assert.AreEqual("Мой текст", input.Text); Assert.IsTrue(input.IsEnabled);
        assistant.Run = (_, _) => throw new InvalidOperationException("test failure"); await Assist(control);
        Assert.AreEqual("Мой текст", input.Text); Assert.IsTrue(input.IsEnabled);
        control.ConfigurePromptAssistant(assistant, () => null); await Assist(control);
        Assert.HasCount(2, assistant.Requests); Assert.AreEqual("Мой текст", input.Text); control.DisposeRuntime();
    });

    [TestMethod]
    public Task CompletedCheckpointReplaysWithoutCallingTheCoreAgain() => OnUi(async () =>
    {
        var assistant = new Assistant(); var control = Control(assistant);
        var request = new ImagePromptAssistRequest(Guid.NewGuid().ToString("N"), "Исходная идея", "ru", "core.gguf", ImageGenerationSessionStore.Create(_root));
        ImagePromptAssistStore.Save(request, "Готовая идея");
        var state = new BackgroundOperationState { Id = request.Id, Kind = ImagePromptAssistant.BackgroundKind, Title = "Prompt", Project = request.SessionDirectory, Input = JsonSerializer.SerializeToElement(request) };
        await control.ResumePromptAsync(state, CancellationToken.None);
        var input = Element<TextBox>(control, "Generation.Prompt"); Assert.AreEqual("Готовая идея", input.Text); Assert.HasCount(0, assistant.Requests);
        var notice = new BackgroundOperationNotice(request.Id, state.Kind, state.Title, request.SessionDirectory);
        Assert.AreEqual(request, ImagePromptAssistStore.ReadRequest(notice.Project!, notice.Id));
        // The notice still identifies this result after another operation replaces the controller state.
        control.RestorePromptResult(notice); input.Undo(); Assert.AreEqual("Исходная идея", input.Text);
        Assert.AreEqual("Готовая идея", ImagePromptAssistStore.Read(request));
        Assert.ThrowsExactly<InvalidDataException>(() => ImagePromptAssistStore.ReadRequest(request.SessionDirectory, "../bad"));
        control.DisposeRuntime();
    });

    [TestMethod]
    public Task PauseAndResumeUseTheApplicationController() => OnUi(async () =>
    {
        var previous = ApplicationBackgroundOperations.Current; var previousToken = ApplicationBackgroundOperations.ExitToken;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "background.json")));
        ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var assistant = new Assistant { Run = async (_, token) => { if (++calls == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } return "Возобновлённый промпт"; } };
        var control = Control(assistant); var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = "Исходник";
        try
        {
            var pending = Assist(control); await entered.Task; await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.AreEqual("Исходник", input.Text);
            await controller.ResumeAsync(CancellationToken.None); await pending;
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
            Assert.AreEqual(assistant.Requests[0].Id, controller.State.Id);
            Assert.AreEqual("Возобновлённый промпт", input.Text); input.Undo(); Assert.AreEqual("Исходник", input.Text);
            Assert.AreEqual(2, calls);
        }
        finally { ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousToken; control.DisposeRuntime(); }
    });

    [TestMethod]
    public void RequestsHaveNoHistoryAndOnlyCompletedPromptIsAccepted()
    {
        var request = new ImagePromptAssistRequest(Guid.NewGuid().ToString("N"), "Сад", "ru", "core", _root);
        StringAssert.Contains(ImagePromptAssistant.UserPrompt(request), "Сад");
        var random = ImagePromptAssistant.UserPrompt(request with { Prompt = " ", LanguageCode = "en" });
        StringAssert.Contains(random, "English"); StringAssert.Contains(random, request.Id);
        Assert.AreEqual("final prompt", ImagePromptAssistant.CleanResponse("<think>private reasoning</think>final prompt"));
        Assert.ThrowsExactly<InvalidDataException>(() => ImagePromptAssistant.CleanResponse("<think>unfinished"));
        Assert.ThrowsExactly<InvalidDataException>(() => ImagePromptAssistant.CleanResponse(" "));
    }

    [TestMethod]
    public Task NativeMenuUsesTheCurrentThemeIncludingSuggestionText() => OnUi(async () =>
    {
        var input = new TextBox { Text = "helo", Width = 300, Height = 100 };
        LiterarySpellChecking.Enable(input, "en");
        var window = new Window { Content = input, Width = 400, Height = 150, ShowInTaskbar = false,
            ShowActivated = false, Left = -10000, Top = -10000 };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Show();
        try
        {
            foreach (var dark in new[] { true, false })
            {
                var background = new SolidColorBrush(dark ? Color.FromRgb(23, 32, 51) : Colors.White);
                var foreground = new SolidColorBrush(dark ? Colors.WhiteSmoke : Color.FromRgb(31, 31, 31));
                window.Resources["PanelBrush"] = background; window.Resources["TextPrimaryBrush"] = foreground;
                window.Resources["StepBadgeBrush"] = new SolidColorBrush(dark ? Color.FromRgb(30, 58, 95) : Color.FromRgb(234, 241, 255));
                LiterarySpellChecking.ApplyMenuTheme(window.Resources);
                // Use the actual native spelling menu/item subclasses, not a custom replacement.
                var menuType = typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditorContextMenu+EditorContextMenu", true)!;
                var itemType = typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditorContextMenu+EditorMenuItem", true)!;
                var menu = (ContextMenu)Activator.CreateInstance(menuType, true)!;
                var item = (MenuItem)Activator.CreateInstance(itemType, true)!;
                var suggestion = new TextBlock { Text = "hello", FontWeight = FontWeights.Bold }; item.Header = suggestion;
                menu.Items.Add(item); menu.PlacementTarget = input; menu.IsOpen = true;
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle); menu.UpdateLayout();
                    Assert.AreEqual(foreground.Color, ((SolidColorBrush)suggestion.Foreground).Color);
                    var borders = VisualChildren(menu).OfType<Border>().Where(b => b.Background is SolidColorBrush).ToArray();
                    Assert.IsTrue(borders.Any(b => ((SolidColorBrush)b.Background).Color == background.Color),
                        string.Join(", ", borders.Select(b => b.Name + ":" + b.Background)));
                    var previews = Environment.GetEnvironmentVariable("LOPATA_WORKSPACE_PREVIEWS");
                    if (previews is not null)
                    {
                        Directory.CreateDirectory(previews);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(previews, dark ? "spelling-dark.png" : "spelling-light.png")); png.Save(output);
                    }
                }
                finally { menu.IsOpen = false; }
            }
            Assert.IsTrue(SpellCheck.GetIsEnabled(input)); Assert.IsNull(input.ContextMenu);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in VisualChildren(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }

    private ImageGenerationControl Control(Assistant assistant, string language = "ru")
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new ImageGenerationControl(); control.ConfigurePromptAssistant(assistant, () => "core.gguf");
        control.Configure(localizer.T, new(), 4, language); control.Restore(ImageGenerationSessionStore.Create(_root));
        control.Measure(new Size(1500, 900)); control.Arrange(new Rect(0, 0, 1500, 900)); control.UpdateLayout();
        return control;
    }
    private sealed class Assistant : IImagePromptAssistant
    {
        public string Answer { get; set; } = "Случайный подробный промпт";
        public List<ImagePromptAssistRequest> Requests { get; } = [];
        public Func<ImagePromptAssistRequest, CancellationToken, Task<string>>? Run { get; set; }
        public Task<string> GenerateAsync(ImagePromptAssistRequest request, CancellationToken token)
        { Requests.Add(request); return Run?.Invoke(request, token) ?? Task.FromResult(Answer); }
    }
    private static Task Assist(ImageGenerationControl control) => (Task)typeof(ImageGenerationControl)
        .GetMethod("AssistPromptAsync", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(control, null)!;
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
