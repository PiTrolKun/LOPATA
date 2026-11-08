using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderUiTests
{
    [TestMethod, TestCategory("Native"), DataRow("ru", true), DataRow("en", false)]
    public Task ReaderActuallyDisplaysPagesChangesModeAndRestoresPosition(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_UI") != "1") Assert.Inconclusive("Explicit WPF reader test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107");
        var source = Path.Combine(root, "user-suite", "AI_Office_TestSuite_RU_EN", "07_comics", language.ToUpperInvariant(), "color_comicinfo.cbz");
        var localizer = new LocalizationService(); localizer.Load(language);
        ReaderStateStore.Save(source, new());
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var window = new ReaderWindow(null, localizer.T, dark);
        try
        {
            window.Show(); await window.OpenAsync(source); await WaitFor(() => Desc<Image>(window).Any(i => i.Source is not null));
            Assert.AreEqual("1", Find<TextBox>(window, "PageNumber").Text);
            Assert.IsTrue(Find<Button>(window, "Speech").IsEnabled);
            Assert.IsTrue(Find<Button>(window, "AI").IsEnabled);
            Assert.IsTrue(Find<Button>(window, "Translation").IsEnabled);
            Assert.IsFalse(Find<Button>(window, "Editor").IsEnabled);
            Click(window, "ZoomIn"); Assert.AreEqual("110%", Find<TextBlock>(window, "Zoom").Text);
            Click(window, "Book"); Click(window, "Fit");
            await WaitFor(() => Desc<Image>(window).Count(i => i.Source is not null) == 2);
            Assert.AreEqual(2, Desc<ReaderPageView>(window).Count());
            Click(window, "Next"); await WaitFor(() => Desc<Image>(window).Count(i => i.Source is not null) == 2);
            Assert.AreEqual("3", Find<TextBox>(window, "PageNumber").Text);
            var color = ((SolidColorBrush)window.Background).Color;
            Click(window, "Theme"); Assert.AreNotEqual(color, ((SolidColorBrush)window.Background).Color);
            Click(window, "Theme");
            Click(window, "Fullscreen"); Assert.AreEqual(WindowStyle.None, window.WindowStyle);
            Click(window, "Fullscreen"); Assert.AreEqual(WindowStyle.SingleBorderWindow, window.WindowStyle);
            Capture(window, Path.Combine(root, "reader-book-" + language + ".png"));
            var saved = ReaderStateStore.Load(source); Assert.AreEqual(2, saved.Page); Assert.IsTrue(saved.Book);
            await window.OpenAsync(source);
            await WaitFor(() => Desc<Image>(window).Count(i => i.Source is not null) == 2);
            Assert.AreEqual("3", Find<TextBox>(window, "PageNumber").Text);
            Click(window, "Book"); await WaitFor(() => Desc<Image>(window).Any(i => i.Source is not null));
            Capture(window, Path.Combine(root, "reader-vertical-" + language + ".png"));
            var document = (ReaderDocument)typeof(ReaderWindow).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var workspace = document.Workspace; window.Close();
            await WaitFor(() => !Directory.Exists(workspace));
        }
        finally { if (window.IsVisible) window.Close(); ComponentLicenseGate.ConfirmAsync = gate; }
    });

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Desc<T>(root).Single(e => AutomationProperties.GetAutomationId(e) == "Office.Reader." + name);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var descendant in Desc<T>(child)) yield return descendant;
        }
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (!condition()) { if (DateTime.UtcNow > until) Assert.Fail("Reader did not reach the expected rendered state."); await Task.Delay(80); }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }
}
