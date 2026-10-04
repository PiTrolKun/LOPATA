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
public sealed class ImageOutputUiTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-output-ui-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public Task OutputButtonMenuSettingsAndDisplayedImageUseIndependentSizes() => OnUi(async () =>
    {
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "Prompt", 256, 256, [1], "unused", ImageGenerationSessionStore.Create(_root))
            { OutputLongestSide = 1280, OutputFolder = Path.Combine(_root, "saved") };
        await new ImageGenerationRuntime(new Worker()).RunAsync(request, [], null, CancellationToken.None);
        foreach (var language in new[] { "ru", "en" })
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var settings = new ImageGenerationSettings { Folder = request.OutputFolder }; var saved = 0;
            using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
            var control = new ImageGenerationControl(installer, new()); control.ConfigureOutput(settings, () => saved++);
            control.Configure(localizer.T, new StorageSettings(), 4, language); control.Restore(request.SessionDirectory);
            var image = Element<System.Windows.Controls.Image>(control, "Generation.CurrentImage");
            Assert.AreEqual(1280, ((BitmapSource)image.Source).PixelWidth);
            Assert.HasCount(6, image.ContextMenu!.Items); Assert.IsTrue(image.ContextMenu.Items.OfType<MenuItem>().All(i => i.IsEnabled));
            var output = Element<Button>(control, "Generation.OutputSize"); Assert.AreEqual("▦⤢", output.Content);
            var toolbar = (WrapPanel)output.Parent; Assert.AreEqual(output, toolbar.Children[^1]);
            Assert.AreEqual("Generation.Variants", AutomationProperties.GetAutomationId(toolbar.Children[^2]));
            Assert.IsTrue(output.ToolTip.ToString()!.Contains("256×256", StringComparison.Ordinal));
            Assert.HasCount(4, output.ContextMenu!.Items);
            ((MenuItem)output.ContextMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual(3840, settings.OutputLongestSide); Assert.AreEqual(1, saved);
            Assert.IsTrue(Element<Button>(control, "Generation.OutputSize").ToolTip.ToString()!.Contains("3840×3840", StringComparison.Ordinal));
            Assert.IsTrue(Element<Button>(control, "Generation.Resolution").ToolTip.ToString()!.Contains("256×256", StringComparison.Ordinal));
            Assert.AreEqual(1280, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single().Request.OutputLongestSide);
            var settingsControl = new ImageGenerationSettingsControl(); settingsControl.Configure(settings, localizer.T);
            var changed = 0; settingsControl.Changed += () => changed++;
            var combo = Element<ComboBox>(settingsControl, "Generation.OutputSizeSetting"); Assert.AreEqual(3, combo.SelectedIndex);
            combo.SelectedIndex = 1; Assert.AreEqual(1280, settings.OutputLongestSide); Assert.AreEqual(1, changed);
            if (language == "ru") Preview(control);
            control.DisposeRuntime();
        }
    });

    [TestMethod]
    public Task SubmittedPresetSnapshotCannotBeChangedByLaterSettingsEdits() => OnUi(async () =>
    {
        var localizer = new LocalizationService(); localizer.Load("ru");
        var settings = new ImageGenerationSettings { Folder = Path.Combine(_root, "saved"), OutputLongestSide = 1920 };
        using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
        var control = new ImageGenerationControl(installer, new()); control.ConfigureOutput(settings, () => { });
        control.Configure(localizer.T, new StorageSettings { Models = new() { Locations = [new() { Path = Path.Combine(_root, "models") }] } }, 4);
        var directory = ImageGenerationSessionStore.Create(_root); control.Restore(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        try
        {
            Element<TextBox>(control, "Generation.Prompt").Text = "New prompt";
            Element<Button>(control, "Generation.Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1920, ImageGenerationSessionStore.Load(directory).Turns.Single().Request.OutputLongestSide);
            Assert.IsFalse(Element<Button>(control, "Generation.OutputSize").IsEnabled);
            settings.OutputLongestSide = 3840;
            Assert.AreEqual(1920, ImageGenerationSessionStore.Load(directory).Turns.Single().Request.OutputLongestSide);
            Element<Button>(control, "Generation.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && !control.CanChangeModel; i++) await Task.Delay(20);
            Assert.IsTrue(control.CanChangeModel);
        }
        finally { ComponentLicenseGate.ConfirmAsync = oldGate; control.DisposeRuntime(); }
    });

    private sealed class Worker : IImageGenerationWorker
    {
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            using var bitmap = new SKBitmap(request.Width, request.Height); bitmap.Erase(SKColors.SlateBlue);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(output); data.SaveTo(file); return Task.CompletedTask;
        }
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
    private static void Preview(FrameworkElement control)
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_OUTPUT_PREVIEW"); if (string.IsNullOrWhiteSpace(path)) return;
        control.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; control.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        control.Resources["SecondaryButtonBackgroundBrush"] = control.Resources["PanelBrush"]; control.Resources["LineBrush"] = Brushes.SlateGray;
        control.Resources["AccentBrush"] = Brushes.RoyalBlue; control.Resources["UiBodyFontSize"] = 16d;
        control.Measure(new Size(1500, 900)); control.Arrange(new Rect(0, 0, 1500, 900)); control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1500, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
