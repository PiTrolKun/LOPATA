using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricLibraryUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task LibraryChatToggleUsesThemeForBothStatesAndLiveThemeSwitch(string language, bool dark) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-lyrics-ui-" + Guid.NewGuid().ToString("N"));
        var l = new LocalizationService(); l.Load(language);
        var window = new MusicPoetryWindow(() => new(), new(), new(new UserProfileStore(), new IpLocationService()),
            l.T, language, new(root), new NoGeneration());
        Theme(window, dark); window.Width = 1100; window.Height = 700; window.Show(); await Render(window);
        try
        {
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)typeof(MusicPoetryWindow)
                .GetField("_libraryToggle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var examples = (Button)typeof(MusicPoetryWindow).GetField("_libraryExamples", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.IsInstanceOfType<System.Windows.Shapes.Path>(examples.Content);
            Assert.AreEqual(l.T("Music.Library.UsedExamples"), examples.ToolTip);
            Assert.AreEqual(l.T("Music.Library.UsedExamples"), AutomationProperties.GetName(examples));
            Assert.IsTrue(ToolTipService.GetShowOnDisabled(examples));
            Assert.IsFalse(examples.IsEnabled);
            foreach (var enabled in new[] { false, true })
            {
                toggle.IsChecked = enabled;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                toggle.ApplyTemplate(); await Render(window);
                var frame = (Border)toggle.Template.FindName("Frame", toggle);
                var tint = (Border)toggle.Template.FindName("SelectedTint", toggle);
                Assert.AreEqual(((SolidColorBrush)window.Resources["SecondaryButtonBackgroundBrush"]).Color,
                    ((SolidColorBrush)frame.Background).Color);
                Assert.AreEqual(enabled ? Visibility.Visible : Visibility.Collapsed, tint.Visibility);
                Assert.IsTrue(frame.CornerRadius.TopLeft > 0);
                if (Environment.GetEnvironmentVariable("LOPATA_LYRICS_EVIDENCE") is { Length: > 0 } evidence)
                {
                    Directory.CreateDirectory(evidence);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidence, $"chat-toggle-{language}-{enabled}.png")); encoder.Save(file);
                }
            }
            Theme(window, !dark); await Render(window);
            var switched = (Border)toggle.Template.FindName("Frame", toggle);
            Assert.AreEqual(((SolidColorBrush)window.Resources["SecondaryButtonBackgroundBrush"]).Color,
                ((SolidColorBrush)switched.Background).Color);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    [TestMethod]
    public Task CatalogInsertionUsesTheEditorsSingleUndoAndRejectsStaleSelection() => Sta(async () =>
    {
        using var editor = new MusicLyricsEditor(); var owner = new Window { Content = editor };
        Theme(owner, true); owner.Show(); await Render(owner);
        try
        {
            var box = Find<TextBox>(editor, "Music.Lyrics"); box.IsUndoEnabled = false;
            editor.Lyrics = "start old end"; box.IsUndoEnabled = true; box.Select(6, 3);
            var source = editor.CaptureSelection(); const string imported = "[Chorus]\nБерег ждёт домой";
            Assert.IsTrue(editor.ApplyEdit(source, new(source.Start, source.Length, imported, source.Start + imported.Length, 0)));
            Assert.AreEqual("start " + imported + " end", editor.Lyrics); Assert.IsTrue(box.CanUndo);
            box.Undo(); Assert.AreEqual(source.Text, editor.Lyrics);
            editor.Lyrics = "changed elsewhere";
            Assert.IsFalse(editor.ApplyEdit(source, new(source.Start, source.Length, imported, 0, 0)));
            Assert.AreEqual("changed elsewhere", editor.Lyrics);
        }
        finally { owner.Close(); }
    });
    [TestMethod]
    public Task ChatTogglePersistsOnlyAccessAndRhymeCheckboxIsDraftOnly() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-lyrics-ui-" + Guid.NewGuid().ToString("N"));
        var sessions = new MusicPoetrySessions(root); var l = new LocalizationService(); l.Load("ru");
        var window = new MusicPoetryWindow(() => new() { Lyrics = "original" }, new(),
            new(new UserProfileStore(), new IpLocationService()), l.T, "ru", sessions, new NoGeneration());
        try
        {
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)typeof(MusicPoetryWindow)
                .GetField("_libraryToggle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var session = (MusicPoetrySession)typeof(MusicPoetryWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.IsTrue(sessions.Load(session.Id).UsePersonalLyricsLibrary); Assert.AreEqual("original", session.Lyrics);
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.IsFalse(sessions.Load(session.Id).UsePersonalLyricsLibrary);
            using var view = new MusicWorkspaceControl(); var before = view.Rhyme.Options.UsePersonalLyricsLibrary;
            var panel = new MusicRhymeSettingsPanel(view.Rhyme, l.T, () => { });
            var check = Logical(panel).OfType<System.Windows.Controls.CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Music.Rhyme.Settings.UseExamples");
            check.IsChecked = !before; Assert.AreEqual(!before, panel.CaptureOptions().UsePersonalLyricsLibrary);
            Assert.AreEqual(before, view.Rhyme.Options.UsePersonalLyricsLibrary);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        await Task.CompletedTask;
    });
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task PersonalCatalogCanSaveAndSearchInBothThemesWithoutModel(string language, bool dark) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-lyrics-ui-" + Guid.NewGuid().ToString("N"));
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var l = new LocalizationService(); l.Load(language); var store = new LyricLibraryStore(root);
        var window = new LyricLibraryWindow(owner, l.T, store, draft: new() { Title = "Ночной берег / Night shore", Text = "[Verse]\nМоре хранит огни.\nThe harbor remembers me." });
        try
        {
            window.Show(); await Render(window);
            Click(Find<Button>(window, "Music.Library.Save")); await Render(window);
            Assert.AreEqual(1, store.Snapshot().Entries.Length);
            Find<TextBox>(window, "Music.Library.Query").Text = "harbor";
            Click(Find<Button>(window, "Music.Library.Search")); await Render(window);
            Assert.AreEqual(1, Find<ListBox>(window, "Music.Library.Results").Items.Count);
            Assert.AreEqual("Море хранит огни.", Find<TextBox>(window, "Music.Library.Field.Text").Text.Split('\n')[1]);
            Assert.IsTrue(Find<Button>(window, "Music.Library.Insert").ActualWidth > 0);
            if (Environment.GetEnvironmentVariable("LOPATA_LYRICS_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(evidence, "catalog-" + language + ".png")); encoder.Save(file);
            }
        }
        finally { window.Close(); owner.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    [TestMethod]
    public Task PreparationAutomaticallyClosesOnlyAfterSuccessAndContinuesOnce() => Sta(async () =>
    {
        var l = new LocalizationService(); l.Load("ru"); var calls = 0; var continuation = 0;
        var window = new LyricLibraryPreparationWindow(null, "fixture", l.T, CancellationToken.None) {
            Prepare = async (_, _, _, token) => { calls++; await Task.Delay(40, token); return true; }
        };
        Theme(window, true);
        if (window.ShowDialog() == true) continuation++;
        Assert.AreEqual(1, calls); Assert.AreEqual(1, continuation); Assert.IsFalse(window.IsVisible);
        await Task.CompletedTask;
    });
    [TestMethod, DataRow(false), DataRow(true)]
    public Task FailedOrCancelledPreparationDoesNotContinue(bool cancel) => Sta(async () =>
    {
        var l = new LocalizationService(); l.Load("en"); var entered = false; var continuation = 0;
        var window = new LyricLibraryPreparationWindow(null, "fixture", l.T, CancellationToken.None) {
            Prepare = async (_, _, _, token) => { entered = true; if (cancel) await Task.Delay(3000, token); else await Task.Delay(30, token); return false; }
        };
        Theme(window, false);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (!entered) return; timer.Stop(); window.Close(); };
        timer.Start(); if (window.ShowDialog() == true) continuation++;
        timer.Stop(); Assert.IsTrue(entered); Assert.AreEqual(0, continuation); Assert.IsFalse(window.IsVisible);
        await Task.CompletedTask;
    });
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static IEnumerable<DependencyObject> Logical(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var item in Logical(child)) yield return item; }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static async Task Render(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
    private static void Theme(FrameworkElement root, bool dark)
    {
        root.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        root.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DarkSlateGray;
        root.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(22, 31, 48)) : Brushes.White;
        root.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(15, 23, 38)) : Brushes.WhiteSmoke;
        root.Resources["InputBrush"] = root.Resources["PanelBrush"]; root.Resources["SecondaryButtonBackgroundBrush"] = root.Resources["PanelBrush"];
        root.Resources["LineBrush"] = Brushes.Gray; root.Resources["AccentBrush"] = Brushes.RoyalBlue; root.Resources["UiBodyFontSize"] = 14d;
    }
    private sealed class NoGeneration : IMusicPoetryRuntime
    {
        public Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance, Action<MusicPoetryContext> contextReady,
            CancellationToken token, bool assemble = false, string? reasoningMode = null, Action<MusicPoetryStreamUpdate>? reasoningProgress = null,
            string? interfaceLanguage = null, MusicPoetryReasoningPreference? reasoningPreference = null, double? temperature = null) =>
            throw new InvalidOperationException("Toggling library access must not generate or read any lyrics.");
        public void Dispose() { }
    }
}
