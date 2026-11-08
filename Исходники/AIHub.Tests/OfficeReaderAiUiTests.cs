using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderAiUiTests
{
    [TestMethod, TestCategory("Native"), DataRow("ru", true), DataRow("en", false)]
    public Task ModelSelectionAndVoiceSettingsAreIndependentAndThemed(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_AI_UI") != "1") Assert.Inconclusive("Explicit WPF model-picker test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "ReaderAi_20261108", language); Directory.CreateDirectory(root);
        var prefs = new ReaderAiSettingsStore(Path.Combine(root, "preferences.json"));
        if (File.Exists(Path.Combine(root, "preferences.json"))) File.Delete(Path.Combine(root, "preferences.json"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var owner = new ReaderWindow(null, localizer.T, dark); owner.Show();
        var storage = new StorageSettings { Models = new() { Locations = [new() { Path = root }] } };
        var library = new ManagedModelLibraryStore(Path.Combine(root, "library"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var future = new ManagedModelArtifactCard { ModelArtifactId = "reader-future", DisplayName = "Future text model", Architecture = "llama", Format = "GGUF",
            InstallDirectory = root, ModelsRoot = root, Files = [new() { RelativePath = "future.gguf", SizeBytes = 2, Purpose = "main_model" }] };
        File.WriteAllBytes(Path.Combine(root, "future.gguf"), [1, 2]);
        Task<IReadOnlyList<ManagedModelArtifactCard>> Load() => Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([future]);
        try
        {
            var ai = Desc<Button>(owner).Single(b => Id(b) == "Office.Reader.AI");
            Assert.IsTrue(ai.IsEnabled); Assert.IsTrue(ai.Margin.Left >= 20);
            Assert.IsFalse(Desc<Button>(owner).Any(b => Id(b) == "Office.Reader.FutureTool"));
            Assert.IsFalse(Desc<Button>(owner).Single(b => Id(b) == "Office.Reader.Translation").IsEnabled, "Translation needs an open document.");
            var window = new ReaderAiWindow(owner, localizer.T, prefs, library, storage, Load);
            await Modal(window, async () => {
                await Wait(() => Desc<RadioButton>(window).Any());
                var textModels = Desc<RadioButton>(window).Where(r => Id(r).StartsWith("Office.Reader.AI.Text.")).ToArray();
                StringAssert.EndsWith(Id(textModels[0]), ManagedModelCatalog.CoreArtifactId);
                Assert.IsTrue(textModels[0].IsChecked);
                Assert.AreEqual(owner.Foreground.ToString(), textModels[0].Foreground.ToString());
                StringAssert.EndsWith(Id(textModels[1]), "reader-future");
                Assert.IsTrue(Desc<RadioButton>(window).Single(r => Id(r) == "Office.Reader.AI.Speech.qwen3-17b").IsChecked);
                Assert.IsTrue(Desc<TextBlock>(window).Any(t => t.Text.Contains("Ryan")));
                Assert.AreEqual(dark ? "#FF0D1523" : "#FFEDF0F6", window.Background.ToString());
                Capture(window, Path.Combine(root, "models.png"));
                window.Width = window.MinWidth; await Dispatcher.Yield(DispatcherPriority.Render);
                Capture(window, Path.Combine(root, "models-narrow.png")); window.Width = 1150;
                var preparationCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var preparationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                preparationTimer.Tick += (_, _) => {
                    if (window.OwnedWindows.OfType<VideoPreparationWindow>().SingleOrDefault() is not { } preparation) return;
                    preparationTimer.Stop();
                    try {
                        Assert.IsTrue(Desc<Button>(preparation).Any(b => Id(b) == "VideoEditor.Preparation.Download"));
                        Assert.AreEqual(window.Background.ToString(), preparation.Background.ToString());
                        Capture(preparation, Path.Combine(root, "preparation.png"));
                        preparationCheck.TrySetResult();
                    } catch (Exception error) { preparationCheck.TrySetException(error); }
                    finally { preparation.Close(); }
                };
                preparationTimer.Start();
                Desc<Button>(window).Single(b => Id(b) == "Office.Reader.AI.Prepare.silero56").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await preparationCheck.Task;
                await Wait(() => Desc<RadioButton>(window).Any(r => Id(r) == "Office.Reader.AI.Text.reader-future"));
                Desc<RadioButton>(window).Single(r => Id(r) == "Office.Reader.AI.Text.reader-future").IsChecked = true;
                var configure = Desc<Button>(window).Single(b => Id(b) == "Office.Reader.AI.Configure.silero56");
                var voiceCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                timer.Tick += (_, _) => {
                    if (window.OwnedWindows.OfType<VideoVoiceWindow>().SingleOrDefault() is not { } voice) return;
                    timer.Stop();
                    try {
                        var speaker = Desc<System.Windows.Controls.ComboBox>(voice).Single(c => (string?)c.ToolTip == localizer.T("VideoEditor.Voices.Speaker"));
                        Assert.AreEqual("xenia", speaker.SelectedItem);
                        Assert.AreEqual(window.Background.ToString(), voice.Background.ToString());
                        Assert.IsTrue(Desc<CheckBox>(voice).All(c => c.Visibility == Visibility.Collapsed));
                        Capture(voice, Path.Combine(root, "voice.png"));
                        voiceCheck.TrySetResult();
                    } catch (Exception error) { voiceCheck.TrySetException(error); }
                    finally { voice.Close(); }
                };
                timer.Start(); configure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await voiceCheck.Task;
                Desc<Button>(window).Single(b => Id(b) == "Office.Reader.AI.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.AreEqual("reader-future", prefs.Load().TextModel);
            Assert.AreEqual("qwen3-17b", prefs.Load().Speech.Selected);
            var saved = File.ReadAllText(Path.Combine(root, "preferences.json"));
            var cancel = new ReaderAiWindow(owner, localizer.T, prefs, library, storage, Load);
            await Modal(cancel, async () => {
                await Wait(() => Desc<RadioButton>(cancel).Any());
                Desc<RadioButton>(cancel).First().IsChecked = true; cancel.Close();
            });
            Assert.AreEqual(saved, File.ReadAllText(Path.Combine(root, "preferences.json")));
        }
        finally { owner.Close(); }
    });
    private static string Id(DependencyObject value) => AutomationProperties.GetAutomationId(value);
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
    { var end = DateTime.UtcNow.AddSeconds(20); while (!check()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(40); } await Dispatcher.Yield(DispatcherPriority.Render); }
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
            dispatcher.BeginInvoke(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
