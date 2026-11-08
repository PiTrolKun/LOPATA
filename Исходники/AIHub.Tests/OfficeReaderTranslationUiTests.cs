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
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderTranslationUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task DefaultsSwapScopeAndOriginalToggleWorkInBothThemes(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_TRANSLATION_UI") != "1") Assert.Inconclusive("Opt in to WPF translation UI tests.");
        var folder = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "ReaderTranslation_20261108", language); Directory.CreateDirectory(folder);
        var localizer = new LocalizationService(); localizer.Load(language);
        var owner = new ReaderWindow(null, localizer.T, dark); owner.Show();
        try
        {
            var prompt = new ReaderTranslationPrompt(owner, localizer.T, 1, null, "Test model");
            await Modal(prompt, () => {
                Assert.AreEqual("ru", prompt.Source); Assert.AreEqual("en", prompt.Target);
                var source = Desc<System.Windows.Controls.ComboBox>(prompt).Single(c => Id(c).EndsWith(".Source"));
                source.IsDropDownOpen = true; prompt.UpdateLayout();
                Assert.IsTrue(source.IsDropDownOpen);
                var item = (ComboBoxItem)source.Items[2];
                Assert.AreEqual(((SolidColorBrush)owner.Foreground).Color, ((SolidColorBrush)item.Foreground).Color);
                source.SelectedIndex = 2; Assert.AreEqual("de", prompt.Source);
                source.SelectedIndex = 0; source.IsDropDownOpen = false;
                Click(prompt, "Office.Reader.Translation.Swap"); Assert.AreEqual("en", prompt.Source); Assert.AreEqual("ru", prompt.Target);
                Assert.IsFalse(Desc<TextBox>(prompt).Any()); Capture(prompt, Path.Combine(folder, "prompt.png"));
                Click(prompt, "Office.Reader.Translation.Start");
            });
            Assert.AreEqual(new ReaderSpeechRange(0, 1), prompt.Range);
            var selected = new ReaderSpeechRange(1, 2, 5, 20);
            var pages = new ReaderTranslationPrompt(owner, localizer.T, 5, selected, "Test model");
            await Modal(pages, () => {
                Assert.IsTrue(Desc<RadioButton>(pages).Single(r => Id(r).EndsWith(".Selection")).IsChecked);
                Assert.IsTrue(Desc<TextBox>(pages).All(t => !t.IsEnabled));
                Click(pages, "Office.Reader.Translation.Start");
            });
            Assert.AreEqual(selected, pages.Range);
            var range = new ReaderTranslationPrompt(owner, localizer.T, 5, null, "Test model");
            await Modal(range, () => {
                Desc<TextBox>(range).Single(t => Id(t).EndsWith(".FirstPage")).Text = "4";
                var count = Desc<TextBox>(range).Single(t => Id(t).EndsWith(".PageCount")); count.Text = "3";
                Click(range, "Office.Reader.Translation.Start"); Assert.IsTrue(range.IsVisible);
                count.Text = "2"; Click(range, "Office.Reader.Translation.Start");
            });
            Assert.AreEqual(new ReaderSpeechRange(3, 2), range.Range);
            var file = OfficeReaderTextTests.MakePdf(folder, "source.pdf");
            var original = new ReaderDocument(file, folder, new FakePages(Path.Combine(folder, "page.png")), file);
            var translated = new ReaderDocument(file, folder, new FakePages(Path.Combine(folder, "page.png")), file);
            var cancellation = new CancellationTokenSource(); Set(owner, "_load", cancellation);
            Set(owner, "_translationOriginal", original); Set(owner, "_translatedDocument", translated);
            Set(owner, "_translationResult", new ReaderTranslationResult(file, "Test model", "ru", "en"));
            Call(owner, "PresentDocument", original, 1);
            var toggle = Desc<Button>(owner).Single(b => Id(b) == "Office.Reader.Translation.Toggle"); toggle.Visibility = Visibility.Visible;
            await Dispatcher.Yield(DispatcherPriority.Render);
            var translate = Desc<Button>(owner).Single(b => Id(b) == "Office.Reader.Translation");
            Assert.IsTrue(toggle.TranslatePoint(new(0, 0), owner).Y > translate.TranslatePoint(new(0, 0), owner).Y);
            Click(owner, "Office.Reader.Translation.Toggle"); Assert.AreSame(translated, Get(owner, "_document"));
            Assert.AreEqual(1, Get(owner, "_page"));
            Click(owner, "Office.Reader.Translation.Toggle"); Assert.AreSame(original, Get(owner, "_document"));
            var panel = new ReaderTranslationWindow(owner, localizer.T, new(file, "Test model", "ru", "en"), file, () => { }); panel.Show();
            Assert.AreEqual(WindowStyle.None, panel.WindowStyle); Assert.IsFalse(panel.ShowInTaskbar);
            var left = panel.Left; panel.Left += 30; Assert.AreEqual(left + 30, panel.Left);
            Capture(panel, Path.Combine(folder, "result.png")); panel.Close();
        }
        finally { owner.Close(); await Task.Delay(100); }
    });
    private static object? Get(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, field);
    private static void Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static string Id(DependencyObject value) => AutomationProperties.GetAutomationId(value);
    private static void Click(DependencyObject window, string id) => Desc<Button>(window).Single(b => Id(b) == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Modal(Window window, Action check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.BeginInvoke(() => { try { check(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { if (window.IsVisible) window.Close(); } });
        window.ShowDialog(); await done.Task;
    }
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var item in Desc<T>(child)) yield return item; }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    private static Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
    private sealed class FakePages(string path) : IReaderPages
    {
        public IReadOnlyList<ReaderPage> Pages { get; } = [new(600, 800), new(600, 800)];
        public Task<string> RenderAsync(int page, int width, CancellationToken token) => Task.FromResult(path);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
