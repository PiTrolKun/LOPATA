using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipReferenceUiTests
{
    [TestMethod]
    [DataRow("ru", true, 1700d, 960d)]
    [DataRow("en", true, 1700d, 960d)]
    [DataRow("ru", false, 1700d, 960d)]
    [DataRow("en", false, 1700d, 960d)]
    [DataRow("ru", true, 860d, 560d)]
    [DataRow("en", true, 860d, 560d)]
    public Task ReferencesSplitThePromptOnlyWhilePresentAndKeepIndependentRoles(string language, bool dark, double width, double height) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new SongClipControl(localizer.T, store);
        var host = Host(control, dark, width, height);
        try {
            host.Show(); Flush(host);
            var split = Find<Grid>(control, "PromptAndReferences");
            var explanation = Find<FrameworkElement>(control, "ReferenceMeaningPanel");
            Assert.AreEqual(Visibility.Collapsed, explanation.Visibility);
            Assert.AreEqual(0, split.ColumnDefinitions[1].ActualWidth, 1);
            Assert.AreEqual("", Find<TextBox>(control, "Prompt").Text);
            Assert.IsTrue(SpellCheck.GetIsEnabled(Find<TextBox>(control, "Prompt")));
            Assert.IsTrue(string.Equals(language == "en" ? "en-US" : "ru-RU", Find<TextBox>(control, "Prompt").Language.IetfLanguageTag, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(control.Checkpoint()); Assert.AreEqual(0, store.List().Count);
            var models = Find<Button>(control, "Model");
            Assert.IsTrue(control.SelectVariant("motion"));
            Find<TextBox>(control, "Prompt").BringIntoView(); Flush(host);
            WriteEvidence(host, control, language, dark, width, height, "none");

            var first = files.File("performer.png"); WriteImage(first, 120);
            var second = files.File("room.png"); WriteImage(second, 200);
            await control.AddReferenceAsync(first, "performer", "The elf singer performs the song.", CancellationToken.None);
            Find<TextBox>(control, "ReferenceRole").BringIntoView(); Flush(host);
            Assert.IsTrue(Find<TextBox>(control, "ReferenceRole").ActualHeight >= 36, "Reference meaning must retain usable editing height.");
            Assert.IsTrue(SpellCheck.GetIsEnabled(Find<TextBox>(control, "ReferenceRole")));
            Assert.IsTrue(string.Equals(language == "en" ? "en-US" : "ru-RU", Find<TextBox>(control, "ReferenceRole").Language.IetfLanguageTag, StringComparison.OrdinalIgnoreCase));
            WriteEvidence(host, control, language, dark, width, height, "single");
            await control.AddReferenceAsync(second, "world", "The apartment is where the cat lives.", CancellationToken.None);
            Flush(host);
            Assert.AreEqual(Visibility.Visible, explanation.Visibility);
            Assert.AreEqual(split.ColumnDefinitions[0].ActualWidth, split.ColumnDefinitions[1].ActualWidth, 1);
            var references = Find<ComboBox>(control, "ReferenceSelection");
            WriteEvidence(host, control, language, dark, width, height, "multiple");
            Assert.AreEqual(2, references.Items.Count);
            Assert.AreEqual(1, references.SelectedIndex);
            Assert.AreEqual("The apartment is where the cat lives.", Find<TextBox>(control, "ReferenceRole").Text);
            references.SelectedIndex = 0;
            Assert.AreEqual("The elf singer performs the song.", Find<TextBox>(control, "ReferenceRole").Text);
            Find<TextBox>(control, "ReferenceRole").Text = "Both singers perform together in the foreground.";
            var kinds = Find<ComboBox>(control, "ReferenceKind");
            Assert.AreEqual("performer", ((ComboBoxItem)kinds.SelectedItem).Tag);
            Assert.IsTrue(control.Checkpoint());
            var saved = store.List().Single();
            Assert.AreEqual(2, saved.References!.Length);
            Assert.AreEqual("Both singers perform together in the foreground.", saved.References[0].Description);
            Assert.AreEqual("The apartment is where the cat lives.", saved.References[1].Description);
            Assert.AreEqual("", saved.Settings.Prompt, "The realistic example must never become user data.");
            var attempt = control.SelectStartAttempt();
            Assert.AreEqual(2, attempt.References!.Length);
            Assert.AreEqual(saved.References[0], attempt.References[0]);
            Find<TextBox>(control, "ReferenceRole").Text = "A later draft change.";
            Assert.AreEqual("Both singers perform together in the foreground.", attempt.References[0].Description);
            Assert.IsTrue(control.Checkpoint());
            references.SelectedIndex = 1;
            Find<Button>(control, "RemoveReference").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, store.List().Single().References!.Length);
            Find<Button>(control, "RemoveReference").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Flush(host);
            Assert.AreEqual(Visibility.Collapsed, explanation.Visibility);
            Assert.AreEqual(0, split.ColumnDefinitions[1].ActualWidth, 1);
            Assert.AreEqual(0, store.List().Single().References!.Length);
        }
        finally {
            host.Close(); Flush(host);
        }
        Assert.IsFalse(SpellCheck.GetIsEnabled(Find<TextBox>(control, "Prompt")));
        Assert.IsFalse(SpellCheck.GetIsEnabled(Find<TextBox>(control, "ReferenceRole")));
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ImportQuestionHasThreePlainTypesAndRequiresMeaningForTheSelectedFile(string language) => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var localizer = new LocalizationService(); localizer.Load(language);
        var image = files.File("performer.png"); WriteImage(image, 80);
        var question = new SongClipReferenceWindow(null, localizer.T, image);
        question.ShowActivated = false; question.ShowInTaskbar = false; question.Show(); Flush(question);
        var add = Find<Button>(question, "ReferenceImport.Add");
        Assert.IsFalse(add.IsEnabled);
        foreach (var kind in new[] { "performer", "world", "object" })
            Assert.AreEqual(localizer.T("SongClip." + (kind == "performer" ? "ReferencePerformer" : kind == "world" ? "ReferenceWorld" : "ReferenceObject")),
                Find<RadioButton>(question, "ReferenceImport." + kind).Content);
        Find<RadioButton>(question, "ReferenceImport.performer").IsChecked = true;
        Assert.IsFalse(add.IsEnabled);
        var description = Find<TextBox>(question, "ReferenceImport.Description");
        Assert.IsTrue(SpellCheck.GetIsEnabled(description));
        Assert.IsTrue(string.Equals(language == "en" ? "en-US" : "ru-RU", description.Language.IetfLanguageTag, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("", description.Text);
        description.Text = "   "; Assert.IsFalse(add.IsEnabled);
        description.Text = "This singer performs the song."; Assert.IsTrue(add.IsEnabled);
        Assert.AreEqual("performer", question.ChosenKind);
        Find<RadioButton>(question, "ReferenceImport.world").IsChecked = true;
        Assert.AreEqual("world", question.ChosenKind);
        var userText = string.Join(" ", ScenarioNavigationTests.LogicalDescendants(question).OfType<TextBlock>().Select(text => text.Text));
        foreach (var technical in new[] { "subject_definitions", "retention_analysis", "detailed_description", "<Picture" })
            Assert.DoesNotContain(technical, userText);
        question.Close(); Flush(question); Assert.IsFalse(SpellCheck.GetIsEnabled(description)); return Task.CompletedTask;
    });

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == "SongClip." + id);
    private static void WriteImage(string path, byte color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { color, 80, 120, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static Window Host(SongClipControl control, bool dark, double width, double height)
    {
        var window = new Window { Content = control, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
        foreach (var key in new[] { "UiBodyFontSize", "UiButtonFontSize", "UiSmallFontSize", "UiTinyFontSize" }) window.Resources[key] = 14d;
        window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.WhiteSmoke;
        window.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(15, 23, 38)) : Brushes.White;
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
        window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
        window.Resources["InputBrush"] = window.Resources["PanelBrush"];
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        return window;
    }
    private static void WriteEvidence(Window window, SongClipControl control, string language, bool dark, double width, double height, string stage)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SONG_REFERENCE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var name = $"reference-{language}-{(dark ? "dark" : "light")}-{width:0}x{height:0}-{stage}";
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(root, name + ".png"))) encoder.Save(output);
        var split = Find<Grid>(control, "PromptAndReferences"); var role = Find<TextBox>(control, "ReferenceRole");
        File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(new {
            language, dark, width, height, stage,
            promptWidth = split.ColumnDefinitions[0].ActualWidth, referenceWidth = split.ColumnDefinitions[1].ActualWidth,
            role.ActualHeight, role.ActualWidth, role.Visibility,
            references = Find<ComboBox>(control, "ReferenceSelection").Items.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Flush(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
