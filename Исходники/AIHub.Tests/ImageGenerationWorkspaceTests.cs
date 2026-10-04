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
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageGenerationWorkspaceTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-workspace-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ImageGenerationRequest Request(string model = "krea") => new(Guid.NewGuid().ToString("N"), model, "  Точный текст\n--steps 999 🐈  ",
        256, 256, [10, 11], Path.Combine(_root, "models"), ImageGenerationSessionStore.Create(_root))
        { SubmittedAt = new DateTimeOffset(2026, 10, 3, 14, 32, 8, TimeSpan.FromHours(7)), OutputFolder = Path.Combine(_root, "images") };

    [TestMethod]
    public async Task AutomaticDeliverySurvivesPauseAndCompletedReplayWithoutApprovingKrea()
    {
        var request = Request(); var worker = new Worker { FailAt = 1 }; var runtime = new ImageGenerationRuntime(worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.RunAsync(request, [], null, CancellationToken.None));
        var first = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single().Results.Single();
        Assert.IsTrue(first.Exported); Assert.IsFalse(first.Reviewed);
        Assert.AreEqual("Krea 2 Turbo_2026-10-03_14-32-08_0001.png", Path.GetFileName(first.ExportPath));
        var bytes = File.ReadAllBytes(first.ExportPath!); worker.FailAt = -1;
        var restored = JsonSerializer.Deserialize<ImageGenerationRequest>(JsonSerializer.Serialize(request))!;
        var turn = await runtime.RunAsync(restored, [], null, CancellationToken.None);
        Assert.HasCount(2, turn.Results); Assert.IsTrue(turn.Results.All(r => r.Exported && !r.Reviewed));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(first.ExportPath!));
        await runtime.RunAsync(restored, [], null, CancellationToken.None);
        Assert.HasCount(2, Directory.GetFiles(request.OutputFolder)); Assert.HasCount(3, worker.Calls);
    }

    [TestMethod]
    public async Task FailedDeliveryCanBeRetriedWithoutRegeneratingOrOverwritingAnotherFile()
    {
        var request = Request(); Directory.CreateDirectory(_root); var blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "keep");
        request = request with { OutputFolder = blocked, Seeds = [10] };
        var worker = new Worker(); var turn = await new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None);
        var failed = turn.Results.Single(); Assert.IsFalse(failed.Exported); Assert.IsNotNull(failed.ExportError);
        Assert.IsTrue(File.Exists(ImageGenerationSessionStore.ResultPath(request, 0))); Assert.AreEqual("keep", File.ReadAllText(blocked));
        var folder = Path.Combine(_root, "images"); Directory.CreateDirectory(folder);
        var collision = Path.Combine(folder, ImageGenerationExport.FileName(request, 1)); File.WriteAllText(collision, "someone else's file");
        var retry = ImageGenerationExport.Save(request, failed, folder);
        Assert.IsTrue(retry.Exported); Assert.IsFalse(retry.Reviewed); Assert.AreNotEqual(collision, retry.ExportPath);
        Assert.AreEqual("someone else's file", File.ReadAllText(collision)); Assert.HasCount(1, worker.Calls);
        CollectionAssert.AreEqual(File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0)), File.ReadAllBytes(retry.ExportPath!));
        // A second click with an old failure snapshot adopts the delivered file rather than creating a duplicate.
        ImageGenerationSessionStore.UpdateResult(request, 0, current => current with { Reviewed = true });
        var repeated = ImageGenerationExport.Save(request, failed, folder);
        Assert.AreEqual(retry.ExportPath, repeated.ExportPath); Assert.IsTrue(repeated.Reviewed); Assert.HasCount(2, Directory.GetFiles(folder));
    }

    [TestMethod]
    public async Task DeliveryAdoptsCommittedIntentAfterInterruptedCheckpoint()
    {
        var request = Request() with { Seeds = [10] }; var turn = await new ImageGenerationRuntime(new Worker()).RunAsync(request, [], null, CancellationToken.None);
        var intent = turn.Results.Single() with { Exported = false }; ImageGenerationSessionStore.PutResult(request, intent);
        var completed = ImageGenerationExport.Save(request, intent);
        Assert.IsTrue(completed.Exported); Assert.AreEqual(intent.ExportPath, completed.ExportPath); Assert.HasCount(1, Directory.GetFiles(request.OutputFolder));
    }

    [TestMethod]
    public Task AcceptedPromptAppearsBeforePreparationAndDuplicateSendIsIgnored() => OnUi(async () =>
    {
        var request = Request("z-image"); using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
        var localizer = new LocalizationService(); localizer.Load("ru");
        var control = new ImageGenerationControl(installer, new()); control.Configure(localizer.T, new StorageSettings { Models = new() { Locations = [new() { Path = request.ModelsRoot }] } }, 4);
        control.ConfigureOutput(new() { Folder = request.OutputFolder }, () => { }); control.Restore(request.SessionDirectory);
        var profileName = "  Автор из профиля  "; control.ConfigureMetadata(() => profileName);
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = async (_, token) => { gateEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        try
        {
            var input = Element<TextBox>(control, "Generation.Prompt"); input.Text = request.Prompt;
            Assert.IsTrue(SpellCheck.GetIsEnabled(input)); Assert.AreEqual("ru-RU", input.Language.IetfLanguageTag, StringComparer.OrdinalIgnoreCase);
            var send = Element<Button>(control, "Generation.Send");
            var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new KeySource(), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = UIElement.PreviewKeyDownEvent };
            input.RaiseEvent(enter); Assert.IsTrue(enter.Handled);
            await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var turns = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns; Assert.HasCount(1, turns); Assert.AreEqual(request.Prompt, turns[0].Request.Prompt);
            Assert.AreEqual("Автор из профиля", turns[0].Request.Metadata!.Author);
            profileName = "Другое имя";
            Assert.AreEqual("Автор из профиля", ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single().Request.Metadata!.Author);
            var sent = Element<TextBox>(control, "Generation.SentPrompt." + turns[0].Request.Id); Assert.IsTrue(sent.IsReadOnly); Assert.AreEqual(request.Prompt, sent.Text);
            Assert.IsFalse(SpellCheck.GetIsEnabled(sent));
            Assert.AreEqual("", Element<TextBox>(control, "Generation.Prompt").Text);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.HasCount(1, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns);
            Element<Button>(control, "Generation.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 50 && !control.CanChangeModel; i++) await Task.Delay(20);
            Assert.IsTrue(control.CanChangeModel); Assert.HasCount(1, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns);
            var draft = "Another promt\nбез автоматических исправлений";
            Element<TextBox>(control, "Generation.Prompt").Text = draft;
            localizer.Load("en"); control.Localize(localizer.T, "en");
            var localizedInput = Element<TextBox>(control, "Generation.Prompt");
            Assert.IsTrue(SpellCheck.GetIsEnabled(localizedInput)); Assert.AreEqual("en-US", localizedInput.Language.IetfLanguageTag, StringComparer.OrdinalIgnoreCase);
            Assert.AreEqual(draft, localizedInput.Text); Assert.IsNull(localizedInput.ContextMenu);
            Assert.IsTrue(Descendants(control).OfType<TextBlock>().Any(t => t.Text == localizer.T("Generation.Canceled")));
        }
        finally { ComponentLicenseGate.ConfirmAsync = oldGate; control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task HistoryThumbnailsMenusAndVisualClearKeepStoredResultsAndDraft() => OnUi(async () =>
    {
        var request = Request(); var first = await new ImageGenerationRuntime(new Worker()).RunAsync(request, [], null, CancellationToken.None);
        var secondRequest = request with { Id = Guid.NewGuid().ToString("N"), Prompt = "Второй запрос", FirstGenerationNumber = 3 };
        await new ImageGenerationRuntime(new Worker()).RunAsync(secondRequest, [], null, CancellationToken.None);
        foreach (var language in new[] { "ru", "en" })
        {
            using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
            var l = new LocalizationService(); l.Load(language); var control = new ImageGenerationControl(installer, new()); Theme(control);
            control.Configure(l.T, new StorageSettings(), 4, language); control.Restore(request.SessionDirectory);
            Assert.HasCount(2, Descendants(control).OfType<TextBox>().Where(t => t.IsReadOnly).ToArray());
            Assert.HasCount(3, Descendants(control).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Generation.Thumbnail.")).ToArray());
            var thumb = Element<Button>(control, "Generation.Thumbnail." + request.Id + ".0"); thumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var current = Element<System.Windows.Controls.Image>(control, "Generation.CurrentImage"); Assert.HasCount(6, current.ContextMenu!.Items);
            Assert.IsTrue(current.ContextMenu.Items.OfType<MenuItem>().All(i => i.IsEnabled));
            Assert.HasCount(1, Descendants(control).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b) == "Generation.ReviewPolicy").ToArray());
            Assert.IsFalse(Descendants(control).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("Generation.Review.")));
            Assert.IsFalse(ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.SelectMany(t => t.Results).Any(r => r.Reviewed));
            Element<TextBox>(control, "Generation.Prompt").Text = "Несохранённый ввод";
            Preview(control, language + "-workspace"); control.ClearWorkspace();
            Assert.HasCount(0, Descendants(control).OfType<System.Windows.Controls.Image>().ToArray());
            Assert.AreEqual("Несохранённый ввод", Element<TextBox>(control, "Generation.Prompt").Text);
            var input = Element<TextBox>(control, "Generation.Prompt");
            Assert.IsTrue(SpellCheck.GetIsEnabled(input)); Assert.AreEqual(language == "en" ? "en-US" : "ru-RU", input.Language.IetfLanguageTag, StringComparer.OrdinalIgnoreCase);
            Assert.HasCount(2, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns); Assert.IsTrue(first.Results.All(r => File.Exists(r.ExportPath)));
            Assert.HasCount(0, control.ModelMenu().Items.OfType<MenuItem>().Where(i => AutomationProperties.GetAutomationId(i).StartsWith("Generation.InstalledModel.")).ToArray());
            control.DisposeRuntime();
        }
    });

    [TestMethod]
    public void SizeChoicesAlwaysRespectEachModelLimitsAndDoNotUpscaleBehindTheUsersBack()
    {
        foreach (var model in ImageGenerationCatalog.Manifest.Models)
        foreach (var ratio in ImageGenerationDimensions.Ratios)
        foreach (var size in new[] { 512, 1024, 2048, 4096 })
        {
            var dims = ImageGenerationDimensions.Fit(ratio.Width, ratio.Height, size, model.MaximumSide);
            Assert.IsTrue(dims.Width % 64 == 0 && dims.Height % 64 == 0); Assert.IsTrue(dims.Width >= 256 && dims.Height >= 256);
            Assert.IsTrue(dims.Width <= model.MaximumSide && dims.Height <= model.MaximumSide);
            Assert.AreEqual(Math.Min(size, model.MaximumSide), Math.Max(dims.Width, dims.Height));
        }
    }

    private sealed class Worker : IImageGenerationWorker
    {
        public int FailAt { get; set; } = -1; public List<int> Calls { get; } = [];
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            Calls.Add(index); if (index == FailAt) throw new OperationCanceledException();
            using var bitmap = new SKBitmap(request.Width, request.Height); bitmap.Erase(index == 0 ? SKColors.SlateBlue : SKColors.Teal);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var stream = File.Create(output); data.SaveTo(stream);
            return Task.CompletedTask;
        }
    }
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
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
    private static void Theme(FrameworkElement element)
    {
        element.Resources["TextPrimaryBrush"] = System.Windows.Media.Brushes.WhiteSmoke;
        element.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        element.Resources["SecondaryButtonBackgroundBrush"] = element.Resources["PanelBrush"];
        element.Resources["LineBrush"] = System.Windows.Media.Brushes.SlateGray; element.Resources["AccentBrush"] = System.Windows.Media.Brushes.RoyalBlue;
        element.Resources["UiBodyFontSize"] = 16d;
    }
    private static void Preview(FrameworkElement control, string name)
    {
        control.Measure(new Size(1500, 900)); control.Arrange(new Rect(0, 0, 1500, 900)); control.UpdateLayout();
        var directory = Environment.GetEnvironmentVariable("LOPATA_WORKSPACE_PREVIEWS"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1500, 900, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 24, 39)), null, new Rect(0, 0, 1500, 900));
        bitmap.Render(background); bitmap.Render(control); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
}
