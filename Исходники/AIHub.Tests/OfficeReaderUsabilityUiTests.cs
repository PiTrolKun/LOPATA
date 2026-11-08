using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderUsabilityUiTests
{
    [TestMethod, TestCategory("Native"), DataRow("ru", true), DataRow("en", false)]
    public Task MultipleDocumentsKeepTheirPagesAndUseThemedScrollbars(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_USABILITY_UI") != "1") Assert.Inconclusive("Explicit WPF usability test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "usability-" + language);
        var firstPath = OfficeReaderTextTests.MakePdf(root, "first.pdf");
        var secondPath = OfficeReaderTextTests.MakePdf(root, "second.pdf");
        var localizer = new LocalizationService(); localizer.Load(language);
        ReaderStateStore.Save(firstPath, new()); ReaderStateStore.Save(secondPath, new());
        var owner = new Window { Width = 100, Height = 100 }; owner.Show();
        var group = ReaderWindowGroup.For(owner, localizer.T, dark);
        var first = group.Open(); ReaderWindow? second = null;
        try
        {
            group.OpenFiles([firstPath, secondPath, firstPath], first);
            second = group.Open(secondPath);
            Assert.AreSame(first, group.Open(firstPath));
            Assert.AreEqual(2, owner.OwnedWindows.Count);
            await WaitFor(() => Layers(first).Any(l => l.PageText.Text.Contains("First page Alpha")) && Layers(second).Any(l => l.PageText.Runs.Count > 0));
            first.ApplyTheme(dark);
            var vertical = Desc<ScrollBar>(first).First(s => s.IsVisible && s.Orientation == System.Windows.Controls.Orientation.Vertical);
            Assert.AreEqual(((SolidColorBrush)first.Resources["ReaderPanel"]).Color, ((SolidColorBrush)vertical.Background).Color);
            var before = ((SolidColorBrush)vertical.Background).Color;
            first.ApplyTheme(!dark); await Dispatcher.Yield(DispatcherPriority.Render);
            Assert.AreNotEqual(before, ((SolidColorBrush)vertical.Background).Color);
            first.ApplyTheme(dark);
            Assert.IsInstanceOfType<Track>(vertical.Template.FindName("PART_Track", vertical));
            var firstText = Layers(first).First(l => l.PageText.Runs.Count > 0);
            Assert.AreEqual(localizer.T("Reader.Copy"), ((MenuItem)firstText.ContextMenu!.Items[0]).Header);
            Assert.AreEqual(2, firstText.ContextMenu.Items.Count);
            var selection = (ReaderTextSelection)typeof(ReaderWindow).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
            var statistics = Desc<TextBlock>(first).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Reader.Statistics");
            string Stats(int lines, int characters, int tokens) => string.Format(localizer.T("Reader.Statistics"), lines.ToString("N0"), characters.ToString("N0"), "≈" + tokens.ToString("N0"));
            await WaitFor(() => statistics.Text == Stats(3, 62, 20));
            Assert.AreEqual(8.0 * 96 / 72, statistics.FontSize, .001);
            Assert.AreEqual(statistics.FontSize, Find(first, "Next").FontSize);
            Assert.AreEqual(statistics.FontSize, Desc<System.Windows.Controls.TextBox>(first).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Reader.PageNumber").FontSize);
            selection.Begin(0, 0, false); selection.Extend(0, 5);
            await WaitFor(() => statistics.Text == Stats(1, 5, 2));
            StringAssert.StartsWith((string)statistics.ToolTip, localizer.T("Reader.StatisticsSelection"));
            Assert.AreEqual("First", await selection.ReadAsync(Document(first), CancellationToken.None));
            Capture(first, Path.Combine(root, "selected-" + language + ".png"));
            // Rapid changes must not restore an obsolete selected-fragment count.
            selection.Extend(1, 6); selection.Clear();
            await WaitFor(() => statistics.Text == Stats(3, 62, 20));
            Find(first, "Fullscreen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.Render);
            Assert.AreEqual(10.0 * 96 / 72, statistics.FontSize, .001);
            Assert.AreEqual(statistics.FontSize, Find(first, "Next").FontSize);
            Capture(first, Path.Combine(root, "footer-fullscreen-" + language + ".png"));
            Find(first, "Fullscreen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            first.Width = first.MinWidth; await Dispatcher.Yield(DispatcherPriority.Render);
            var statisticsRight = statistics.TranslatePoint(new Point(statistics.ActualWidth, 0), first).X;
            var nextLeft = Find(first, "Previous").TranslatePoint(new Point(), first).X;
            Assert.IsTrue(statisticsRight <= nextLeft && statistics.ActualWidth > 0, "Statistics must fit before page navigation at minimum width.");
            Capture(first, Path.Combine(root, "footer-narrow-" + language + ".png"));
            first.Width = 1240;
            Find(first, "Book").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Layers(first).Count(l => l.PageText.Runs.Count > 0) == 2);
            selection.SelectAll(2);
            await WaitFor(() => statistics.Text == Stats(3, 62, 20));
            StringAssert.Contains(await selection.ReadAsync(Document(first), CancellationToken.None), "Second page Beta");
            var workspace = Document(first).Workspace;
            first.Close(); await WaitFor(() => !Directory.Exists(workspace));
            Assert.IsTrue(second.IsVisible);
            StringAssert.Contains((await Document(second).ReadTextAsync(1, CancellationToken.None)).Text, "Second page Beta");
            Assert.IsTrue(Layers(second).Any(l => l.PageText.Runs.Count > 0));
        }
        finally { if (first.IsVisible) first.Close(); if (second?.IsVisible == true) second.Close(); owner.Close(); }
    });

    private static ReaderDocument Document(ReaderWindow window) => (ReaderDocument)typeof(ReaderWindow).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static IEnumerable<ReaderTextLayer> Layers(ReaderWindow window) => Desc<ReaderTextLayer>(window).Where(l => l.IsVisible);
    private static Button Find(Window window, string name) => Desc<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Reader." + name);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var descendant in Desc<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!condition()) { if (DateTime.UtcNow > until) Assert.Fail("Expected reader windows/pages were not ready."); await Task.Delay(80); }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }
}
