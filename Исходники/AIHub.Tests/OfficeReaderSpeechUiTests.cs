using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderSpeechUiTests
{
    [TestMethod, TestCategory("Native"), DataRow("ru", true), DataRow("en", false)]
    public Task PromptsSelectionPagesAndPlayerWorkInBothThemes(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_SPEECH_UI") != "1") Assert.Inconclusive("Explicit WPF speech test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "ReaderSpeech_20261108", language); Directory.CreateDirectory(root);
        var localizer = new LocalizationService(); localizer.Load(language);
        var owner = new ReaderWindow(null, localizer.T, dark); owner.Show();
        var voice = new VideoNarratorChoice("silero56", "ru", 100, new("xenia", Device: "cpu"));
        try
        {
            var one = new ReaderSpeechPrompt(owner, localizer.T, 1, null, voice);
            await Modal(one, () => {
                Assert.IsFalse(Desc<TextBox>(one).Any());
                Assert.IsTrue(Desc<TextBlock>(one).Any(t => t.Text.Contains("Silero") && t.Text.Contains("xenia")));
                Click(one, "Office.Reader.Speech.Start"); return Task.CompletedTask;
            });
            Assert.AreEqual(new ReaderSpeechRange(0, 1), one.Range);
            var pages = new ReaderSpeechPrompt(owner, localizer.T, 7, null, voice);
            await Modal(pages, () => {
                var first = Desc<TextBox>(pages).Single(t => Id(t).EndsWith(".FirstPage"));
                var count = Desc<TextBox>(pages).Single(t => Id(t).EndsWith(".PageCount"));
                Assert.AreEqual("1", first.Text); Assert.AreEqual("7", count.Text);
                first.Text = "6"; count.Text = "3"; Click(pages, "Office.Reader.Speech.Start");
                Assert.IsTrue(pages.IsVisible, "Out-of-range pages must not start synthesis.");
                count.Text = "2"; Capture(pages, Path.Combine(root, "pages.png"));
                Click(pages, "Office.Reader.Speech.Start"); return Task.CompletedTask;
            });
            Assert.AreEqual(new ReaderSpeechRange(5, 2), pages.Range);
            var selected = new ReaderSpeechRange(2, 3, 7, 50);
            var selection = new ReaderSpeechPrompt(owner, localizer.T, 7, selected, voice);
            await Modal(selection, () => {
                Assert.IsTrue(Desc<RadioButton>(selection).Single(r => Id(r).EndsWith(".Selection")).IsChecked);
                Assert.IsTrue(Desc<TextBox>(selection).All(t => !t.IsEnabled));
                Capture(selection, Path.Combine(root, "selection.png")); Click(selection, "Office.Reader.Speech.Start"); return Task.CompletedTask;
            });
            Assert.AreEqual(selected, selection.Range);
            var audio = Path.Combine(root, "silent.wav");
            using (var wave = new WaveFileWriter(audio, new(16000, 16, 1))) wave.Write(new byte[320000], 0, 320000);
            var player = new ReaderSpeechPlayerWindow(owner, localizer.T, new(audio, 10, voice), "source.pdf"); player.Show();
            try
            {
                await Wait(() => (string?)Desc<Button>(player).Single(b => Id(b).EndsWith(".Play")).Content == "Ⅱ");
                Assert.IsFalse(player.ShowInTaskbar); Assert.AreEqual(WindowStyle.None, player.WindowStyle);
                Click(player, "Office.Reader.Speech.Player.Play");
                Assert.AreEqual("▶", Desc<Button>(player).Single(b => Id(b).EndsWith(".Play")).Content);
                var slider = Desc<Slider>(player).Single(); slider.Value = 4;
                await Task.Delay(250); Assert.AreEqual(4, slider.Value, .3);
                Assert.IsTrue(Desc<TextBlock>(player).Any(t => t.Text.Contains("0:04 / 0:10")));
                var oldLeft = player.Left; player.Left += 35; Assert.AreEqual(oldLeft + 35, player.Left);
                Assert.IsTrue(Desc<Button>(player).Single(b => Id(b).EndsWith(".Save")).IsEnabled);
                Capture(player, Path.Combine(root, "player.png"));
            }
            finally { player.Close(); }
            Assert.IsTrue(File.Exists(audio), "Closing the floating player must not discard document audio.");
            File.Delete(audio);
        }
        finally { owner.Close(); }
    });

    private static string Id(DependencyObject value) => AutomationProperties.GetAutomationId(value);
    private static void Click(DependencyObject window, string id) => Desc<Button>(window).Single(b => Id(b) == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Modal(Window window, Func<Task> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.BeginInvoke(async () => { try { await check(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { if (window.IsVisible) window.Close(); } });
        window.ShowDialog(); await done.Task;
    }
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var item in Desc<T>(child)) yield return item; }
    }
    private static async Task Wait(Func<bool> check)
    { var end = DateTime.UtcNow.AddSeconds(15); while (!check()) { if (DateTime.UtcNow > end) throw new TimeoutException("Media did not open."); await Task.Delay(50); } await Dispatcher.Yield(DispatcherPriority.Render); }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
