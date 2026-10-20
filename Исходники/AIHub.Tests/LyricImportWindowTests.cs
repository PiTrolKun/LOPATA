using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicLibrary;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricImportWindowTests
{
    [TestMethod, DataRow("ru", true, true), DataRow("en", false, true), DataRow("ru", true, false)]
    public Task ExternalImportReviewsStagedRecordsAndOnlyConfirmationWritesTheCatalog(string language, bool dark, bool accept) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-import-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "external.jsonl");
        File.WriteAllLines(source, [JsonSerializer.Serialize(new { title = "Первая / First", lyrics = "First original lyrics" }),
            JsonSerializer.Serialize(new { title = "Вторая / Second", lyrics = "Second original lyrics", artist = "File artist", style = "From file", language = "fr" })]);
        var bytes = File.ReadAllBytes(source); var store = new LyricLibraryStore(Path.Combine(root, "library"));
        var l = new LocalizationService(); l.Load(language);
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var window = new LyricLibraryWindow(owner, l.T, store); window.Show();
        Exception? failure = null; var opened = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => {
            var review = window.OwnedWindows.OfType<LyricImportWindow>().FirstOrDefault(); if (review is null) return;
            timer.Stop(); opened = true;
            try
            {
                review.UpdateLayout();
                Assert.AreEqual(0, store.Snapshot().Entries.Length);
                Find<TextBox>(review, "Music.Library.Field.Artist").Text = "Added artist";
                Find<TextBox>(review, "Music.Library.Field.Genre").Text = "indie rock";
                Find<TextBox>(review, "Music.Library.Field.Style").Text = "Calm spoken delivery";
                Find<TextBox>(review, "Music.Library.Field.Language").Text = "русский";
                if (!accept) { Click(Find<Button>(review, "Music.Library.Import.Cancel")); return; }
                Click(Find<Button>(review, "Music.Library.Import.FillEmpty"));
                var list = Find<ListBox>(review, "Music.Library.Import.Entries"); list.SelectedIndex = 1;
                Assert.AreEqual("File artist", Find<TextBox>(review, "Music.Library.Field.Artist").Text);
                Assert.AreEqual("From file", Find<TextBox>(review, "Music.Library.Field.Style").Text);
                Assert.AreEqual("fr", Find<TextBox>(review, "Music.Library.Field.Language").Text);
                list.SelectedIndex = 0;
                Find<TextBox>(review, "Music.Library.Field.Language").Text = "invalid-language-value";
                Click(Find<Button>(review, "Music.Library.Import.Confirm"));
                Assert.IsTrue(review.IsVisible); Assert.AreEqual(0, store.Snapshot().Entries.Length);
                Assert.IsFalse(string.IsNullOrWhiteSpace(Find<TextBlock>(review, "Music.Library.Import.Error").Text));
                Find<TextBox>(review, "Music.Library.Field.Language").Text = "ru";
                review.UpdateLayout();
                if (Environment.GetEnvironmentVariable("LOPATA_LYRICS_EVIDENCE") is { Length: > 0 } evidence)
                {
                    Directory.CreateDirectory(evidence);
                    var bitmap = new RenderTargetBitmap((int)review.ActualWidth, (int)review.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(review); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidence, "import-" + language + ".png")); png.Save(file);
                }
                Click(Find<Button>(review, "Music.Library.Import.Confirm"));
            }
            catch (Exception error) { failure = error; review.Close(); }
        };
        timer.Start();
        try
        {
            var task = (Task)typeof(LyricLibraryWindow).GetMethod("ImportFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new[] { source } })!;
            await task.WaitAsync(TimeSpan.FromSeconds(20));
            if (failure is not null) throw failure;
            Assert.IsTrue(opened);
            var entries = store.Snapshot().Entries; Assert.AreEqual(accept ? 2 : 0, entries.Length);
            if (accept)
            {
                var first = entries.Single(e => e.Title == "Первая / First");
                Assert.AreEqual("Added artist", first.Artist); Assert.AreEqual("Calm spoken delivery", first.Style); Assert.AreEqual("ru", first.Language);
                Assert.AreEqual("First original lyrics", first.Text);
                Assert.AreEqual("File artist", entries.Single(e => e.Title == "Вторая / Second").Artist);
            }
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        }
        finally { timer.Stop(); window.Close(); owner.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static void Theme(FrameworkElement root, bool dark)
    {
        root.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        root.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DarkSlateGray;
        root.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(22,31,48)) : Brushes.White;
        root.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(15,23,38)) : Brushes.WhiteSmoke;
        root.Resources["SecondaryButtonBackgroundBrush"] = root.Resources["PanelBrush"];
        root.Resources["LineBrush"] = Brushes.Gray; root.Resources["AccentBrush"] = Brushes.RoyalBlue; root.Resources["UiBodyFontSize"] = 14d;
    }
}
