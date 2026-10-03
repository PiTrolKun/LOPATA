using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageGenerationGalleryTests
{
    [TestMethod]
    public Task EmbeddedExamplesKeepOriginalPixelsAndLocalizedPrompts() => OnUi(() =>
    {
        Assert.HasCount(4, ImageGenerationExamples.All);
        foreach (var group in ImageGenerationExamples.All.GroupBy(e => e.ModelId)) Assert.HasCount(2, group);
        foreach (var example in ImageGenerationExamples.All)
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("/AIHub;component/Assets/GenerationExamples/" + example.Resource, UriKind.Relative))!.Stream;
            Assert.AreEqual(example.Sha256.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            var image = ImageGenerationExamples.Image(example); Assert.AreEqual(2048, image.PixelWidth); Assert.AreEqual(2048, image.PixelHeight);
            Assert.IsTrue(image.IsFrozen);
            foreach (var language in new[] { "ru", "en" })
            {
                var l = new LocalizationService(); l.Load(language);
                var window = new ImageGenerationExampleWindow(example, l.T);
                var shown = Descendants(window).OfType<System.Windows.Controls.Image>().Single();
                Assert.IsNull(shown.Effect); Assert.IsNull(shown.Clip);
                Assert.AreEqual(2048, ((BitmapSource)shown.Source).PixelWidth);
                var prompt = Descendants(window).OfType<TextBox>().Single(); Assert.AreEqual(example.Prompt(language), prompt.Text);
                Assert.IsTrue(prompt.IsReadOnly);
                Assert.IsTrue(Descendants(window).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "Generation.Example.CopyPrompt"));
                Assert.AreEqual(language, l.T("Generation.Examples.Language"));
                window.Close();
            }
        }
        Assert.StartsWith("Desktop wallpaper", ImageGenerationExamples.All.Single(e => e.Id == "z-image-fantasy").PromptEn);
        Assert.StartsWith("dark fantasy", ImageGenerationExamples.All.Single(e => e.Id == "krea-fantasy").PromptEn);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task SelectionHasLocalPreviewMasksAndRendersInBothLanguages() => OnUi(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-gallery-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var language in new[] { "ru", "en" })
            {
                var l = new LocalizationService(); l.Load(language);
                using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(root, "library")));
                var control = new ImageGenerationControl(installer, new());
                Theme(control); control.Configure(l.T, new StorageSettings(), 4);
                var buttons = Descendants(control).OfType<Button>().ToArray();
                Assert.HasCount(2, buttons.Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Generation.Model.")).ToArray());
                Assert.HasCount(4, buttons.Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Generation.Example.")).ToArray());
                var previews = Descendants(control).OfType<Viewbox>().ToArray(); Assert.HasCount(4, previews);
                foreach (var example in ImageGenerationExamples.All)
                {
                    var button = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Example." + example.Id);
                    var images = Descendants(button).OfType<System.Windows.Controls.Image>().ToArray();
                    Assert.AreEqual(1 + example.BlurRegions.Length, images.Length);
                    Assert.IsNull(images[0].Effect);
                    foreach (var overlay in images.Skip(1))
                    {
                        Assert.IsInstanceOfType<BlurEffect>(overlay.Effect);
                        var clip = (RectangleGeometry)overlay.Clip; Assert.IsTrue(clip.Rect.Width < 512 && clip.Rect.Height < 512);
                    }
                }
                SavePreview(control, language + "-selection");
                control.Localize(l.T); Assert.IsFalse(Directory.Exists(Path.Combine(root, "models")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task MissingComponentCheckDoesNotDownloadOrAskForLicense() => OnUi(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-preparation-" + Guid.NewGuid().ToString("N"));
        var oldGate = ComponentLicenseGate.ConfirmAsync; var confirmations = 0;
        ComponentLicenseGate.ConfirmAsync = (_, _) => { confirmations++; throw new InvalidOperationException("Unexpected license dialog during file check."); };
        try
        {
            var l = new LocalizationService(); l.Load("ru");
            using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(root, "library")));
            var control = new ImageGenerationControl(installer, new()); Theme(control);
            control.Configure(l.T, new StorageSettings { Models = new() { Locations = [new() { Path = Path.Combine(root, "models") }] } }, 4);
            Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Model.krea").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 50 && Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Verify").IsEnabled == false; i++) await Task.Delay(20);
            Assert.AreEqual(0, confirmations); Assert.IsFalse(Directory.Exists(Path.Combine(root, "models")));
            Assert.IsFalse(Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.OpenChat").IsEnabled);
            Assert.IsTrue(Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Download").IsEnabled);
            Assert.IsTrue(Descendants(control).OfType<TextBlock>().Any(t => t.Text == l.T("Generation.Preparation.NeedsDownload")));
            SavePreview(control, "ru-preparation-missing");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => installer.CheckAsync(Path.Combine(root, "models"), "krea", null, cancellation.Token));
            Assert.IsTrue(control.GoBack()); Assert.IsFalse(control.GoBack());
        }
        finally { ComponentLicenseGate.ConfirmAsync = oldGate; if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private static void Theme(FrameworkElement element)
    {
        element.Resources["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(235, 239, 246));
        element.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        element.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        element.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        element.Resources["LineBrush"] = new SolidColorBrush(Color.FromRgb(49, 60, 77));
        element.Resources["AccentBrush"] = System.Windows.Media.Brushes.RoyalBlue;
        element.Resources["UiBodyFontSize"] = 16d;
    }
    private static void SavePreview(FrameworkElement control, string name)
    {
        control.Measure(new Size(1500, 1000)); control.Arrange(new Rect(0, 0, 1500, 1000)); control.UpdateLayout();
        var result = new RenderTargetBitmap(1500, 1000, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual(); using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 24, 39)), null, new Rect(0, 0, 1500, 1000));
        result.Render(backdrop); result.Render(control);
        var directory = Environment.GetEnvironmentVariable("LOPATA_GALLERY_PREVIEWS");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        Assert.AreEqual(1500, result.PixelWidth);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        foreach (var descendant in Descendants(child)) yield return descendant;
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
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
