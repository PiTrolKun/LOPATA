using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.SpeechAssets;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class SpeechAssetsUiTests
{
    [TestMethod, TestCategory("Native")]
    public Task InstalledNarratorSurvivesHiddenIdleAndReusesPidForGeneration() => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_RESIDENT_INSTALLED") != "1") Assert.Inconclusive("Explicit installed-model retention probe required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "resident-installed"); Directory.CreateDirectory(root);
        var choice = new AIHub.Services.VideoEditor.VideoNarratorChoice("qwen3-17b", "ru", 100, new("Ryan"));
        var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var project = new SpeechAssetSession { Voice = choice, SourceName = "retention-probe.md", Rows = SpeechAssetMarkdown.Parse("- Проверяем повторное использование голоса.") }; store.FixProject(project);
        var card = new ManagedModelLibraryStore().Load(AIHub.Services.VideoEditor.VideoNarratorCatalog.ArtifactId(choice.Id)); Assert.IsNotNull(card);
        var resident = new SpeechAssetModelSession(directory: Path.Combine(root, "warm"));
        var owner = new Window { Width = 1000, Height = 650 };
        async Task<bool> Prepare(AIHub.Services.VideoEditor.VideoModelNeed need, CancellationToken token)
        {
            using var preparation = new AIHub.Services.VideoEditor.VideoModelPreparation(new ManagedModelLibraryStore());
            return await preparation.PrepareAsync(card.ModelsRoot, need, false, null, token);
        }
        var control = new SpeechAssetsControl(owner, key => key, store, resident, Prepare); owner.Content = control; owner.Show();
        var hidden = ApplicationBackgroundOperations.PreserveHiddenWork;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            control.EnterScenario(); control.OpenSavedSession(project.Id); await control.WarmSelectedModelAsync().WaitAsync(timeout.Token);
            Assert.IsTrue(resident.IsWarm(choice), "Opening a saved project must perform real warmup.");
            var before = OwnedProcessRegistry.Shared.GetSnapshot().Single(p => p.Component == "VideoEditor.Narrator." + choice.Id);
            ApplicationBackgroundOperations.PreserveHiddenWork = true; owner.Hide(); await Task.Delay(3000, timeout.Token);
            var idle = OwnedProcessRegistry.Shared.GetSnapshot().Single(p => p.Pid == before.Pid);
            Assert.IsTrue(resident.IsWarm(choice)); owner.Show();
            var row = project.Rows[0];
            await new SpeechAssetWork(store, resident: resident).RunAsync(new(project.Id, choice, [new(row.Id, Guid.NewGuid().ToString("N"), row.Text)]), "Retention probe", null, timeout.Token);
            Assert.IsTrue(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Pid == before.Pid));
            Assert.HasCount(1, store.Load(project.Id).Rows[0].Takes);
            await control.LeaveScenarioAsync();
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Pid == before.Pid));
            File.WriteAllText(Path.Combine(root, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { before.Pid,
                BeforeMemory = before.MemoryBytes, HiddenIdleMemory = idle.MemoryBytes, SamePidAfterGeneration = true, ExitedWithScenario = true }));
        }
        finally { await control.LeaveScenarioAsync(); owner.Close(); ApplicationBackgroundOperations.PreserveHiddenWork = hidden; }
    }, TimeSpan.FromMinutes(6));
    [TestMethod, TestCategory("Native")]
    public Task ProjectOpeningWarmsModelAndWindowHideKeepsItUntilScenarioExit() => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_UI") != "1") Assert.Inconclusive("Explicit UI test required.");
        var root = Path.Combine(Path.GetTempPath(), "speech-resident-ui-" + Guid.NewGuid().ToString("N"));
        var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var choice = new AIHub.Services.VideoEditor.VideoNarratorChoice("silero56", "ru", 100, new("xenia"));
        var project = new SpeechAssetSession { Voice = choice, Rows = SpeechAssetMarkdown.Parse("- Пример") }; store.FixProject(project);
        var calls = 0; var disposed = 0;
        var registry = new AIHub.Services.VideoEditor.VideoNarratorRegistry();
        registry.Register("silero56", () => new SpeechAssetResidentTests.Probe(root, _ => { calls++; return Task.CompletedTask; }, () => disposed++));
        var resident = new SpeechAssetModelSession(registry, Path.Combine(root, "warm"));
        var owner = new Window { Width = 1000, Height = 650 };
        var control = new SpeechAssetsControl(owner, key => key, store, resident, (_, _) => Task.FromResult(true));
        owner.Content = control; owner.Show();
        var hidden = ApplicationBackgroundOperations.PreserveHiddenWork;
        try
        {
            control.EnterScenario(); control.OpenSavedSession(project.Id); await control.WarmSelectedModelAsync();
            Assert.IsTrue(resident.IsWarm(choice)); Assert.AreEqual(1, calls);
            ApplicationBackgroundOperations.PreserveHiddenWork = true; owner.Hide(); await Task.Delay(150);
            Assert.IsTrue(resident.IsWarm(choice)); Assert.AreEqual(0, disposed);
            owner.Show(); await control.WarmSelectedModelAsync(); Assert.AreEqual(1, calls);
            ApplicationBackgroundOperations.PreserveHiddenWork = false;
            control.Visibility = Visibility.Collapsed;
            await control.LeaveScenarioAsync(); Assert.AreEqual(1, disposed); Assert.IsFalse(resident.IsWarm(choice));
            control.Visibility = Visibility.Visible; await control.WarmSelectedModelAsync();
            Assert.IsTrue(resident.IsWarm(choice)); Assert.AreEqual(2, calls);
            await control.LeaveScenarioAsync(); Assert.AreEqual(2, disposed);
        }
        finally { await control.LeaveScenarioAsync(); owner.Close(); ApplicationBackgroundOperations.PreserveHiddenWork = hidden; Directory.Delete(root, true); }
    });
    [TestMethod, TestCategory("Native"), DataRow("ru", true), DataRow("en", false)]
    public Task EditableRowsConfirmationCountersAndRestore(string language, bool dark) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_UI") != "1") Assert.Inconclusive("Explicit UI test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", language); Directory.CreateDirectory(root);
        var store = new SpeechAssetStore(Path.Combine(root, "sessions", Guid.NewGuid().ToString("N")));
        var session = new SpeechAssetSession { SourceName = "notification-list.md", Rows = SpeechAssetMarkdown.Parse("# Реплики / Lines\n- Файл сохранён.\n- Загрузка завершена.\n- Проверьте настройки.") };
        foreach (var row in session.Rows.Take(2))
            for (var index = 0; index < 2; index++)
            {
                var id = Guid.NewGuid().ToString("N"); var relative = "audio/" + id + ".wav"; var file = store.FileFor(session.Id, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var wave = new WaveFileWriter(file, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
                row.Takes.Add(new() { Id = id, Text = row.Text, Voice = session.Voice, File = relative, Seconds = .1,
                    Sha256 = SpeechAssetStore.Hash(file), Selected = index == 0 });
            }
        session.Rows[0].Confirm();
        store.Save(session); File.WriteAllText(Path.Combine(store.Root, "last.txt"), session.Id);
        var localizer = new LocalizationService(); localizer.Load(language);
        // Use only the main host's public palette; ReaderWindow has additional brushes that can hide missing resources.
        var owner = new Window { Width = 1400, Height = 850 };
        foreach (var (key, value) in new[] {
            ("WindowBackgroundBrush", dark ? "#111827" : "#F3F3F3"), ("PanelBrush", dark ? "#172033" : "#FFFFFF"),
            ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#AAB4C4" : "#5D6470"),
            ("LineBrush", dark ? "#2D374B" : "#DADDE3"), ("AccentBrush", "#2563EB"),
            ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8") })
            owner.Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        owner.Resources["UiBodyFontSize"] = 14d;
        owner.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        var control = new SpeechAssetsControl(owner, localizer.T, store); owner.Content = control; owner.Show();
        try
        {
            await Task.Delay(250); owner.UpdateLayout();
            var text = Desc<TextBox>(control).Single(t => t.Text == "Файл сохранён.");
            text.Text = "Файл сохранён!";
            await Task.Delay(500); Assert.AreEqual("Файл сохранён!", store.Load(session.Id).Rows[0].Text);
            var restore = Desc<Button>(control).First(b => Equals(b.Content, localizer.T("SpeechAssets.RestoreText")));
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual("Файл сохранён.", text.Text);
            Desc<Button>(control).First(b => Equals(b.Content, localizer.T("SpeechAssets.Confirm"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(Desc<TextBlock>(control).Any(t => t.Text == "V"));
            Assert.IsTrue(Desc<TextBlock>(control).Any(t => t.Text == string.Format(localizer.T("SpeechAssets.Counts"), 3, 2, 1, 2)));
            await Task.Delay(150); owner.UpdateLayout();
            foreach (var item in Desc<System.Windows.Controls.Control>(control).Where(c => c is Button or TextBox or System.Windows.Controls.CheckBox or System.Windows.Controls.RadioButton))
            {
                var foreground = (item.Foreground as SolidColorBrush)?.Color;
                var background = (item is not System.Windows.Controls.Primitives.ToggleButton && item.Background is SolidColorBrush { Color.A: > 0 } brush ? brush.Color : (System.Windows.Media.Color?)null)
                    ?? ((SolidColorBrush)owner.Resources["PanelBrush"]).Color;
                Assert.IsNotNull(foreground, item.GetType().Name);
                Assert.IsTrue(Contrast(foreground.Value, background) >= 4.5, $"Low contrast: {item.GetType().Name} {foreground} / {background}");
            }
            var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(owner); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, "utility.png")); encoder.Save(output);
            await Task.Delay(500); // Real generation flushes pending text/confirmation edits before locking them.
            var busy = typeof(SpeechAssetsControl).GetMethod("Busy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            busy.Invoke(control, [true]); await Task.Delay(150); owner.UpdateLayout();
            Assert.IsTrue(Desc<TextBox>(control).All(t => t.IsReadOnly));
            Assert.IsTrue(Desc<System.Windows.Controls.ListBox>(control).All(l => l.IsEnabled), "Scrolling remains available during synthesis.");
            foreach (var button in Desc<Button>(control).Where(b => !b.IsEnabled))
            {
                var frame = Desc<Border>(button).First();
                Assert.AreEqual(((SolidColorBrush)owner.Resources["SecondaryButtonBackgroundBrush"]).Color,
                    ((SolidColorBrush)frame.Background).Color, "Actual disabled template must not substitute system white.");
                Assert.IsTrue(Contrast(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)frame.Background).Color) >= 4.5);
            }
            var busyBitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            busyBitmap.Render(owner); var busyPng = new PngBitmapEncoder(); busyPng.Frames.Add(BitmapFrame.Create(busyBitmap));
            using (var busyFile = File.Create(Path.Combine(root, "generating.png"))) busyPng.Save(busyFile);
            // Exercise the hidden key through the owning window, including while row editing is locked.
            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(owner), 0, System.Windows.Input.Key.F1) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            owner.RaiseEvent(key); Assert.IsTrue(key.Handled);
            await Task.Delay(400);
            Assert.AreEqual(2, store.Load(session.Id).Rows.Count(r => r.Confirmed));
            Assert.IsFalse(store.Load(session.Id).Rows[2].Confirmed);
            busy.Invoke(control, [false]); await Task.Delay(100);
            var pickerChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                Window? picker = null;
                try
                {
                    picker = owner.OwnedWindows.Cast<Window>().Single(); picker.UpdateLayout();
                    Assert.AreEqual("VideoEditor.Voices.Window", System.Windows.Automation.AutomationProperties.GetAutomationId(picker));
                    Assert.IsTrue(Desc<System.Windows.Controls.ListBox>(picker).Any(list => list.Items.Count > 3));
                    var shot = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(picker); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot));
                    using var file = File.Create(Path.Combine(root, "models.png")); png.Save(file);
                    pickerChecked.TrySetResult();
                }
                catch (Exception error) { pickerChecked.TrySetException(error); }
                finally { picker?.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            Desc<Button>(control).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "SpeechAssets.Model")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pickerChecked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var automatic = store.CreateDraft(); automatic.Rows = SpeechAssetMarkdown.Parse("- Automatic project");
            var automaticJob = new SpeechAssetJob(automatic.Id, automatic.Voice,
                [new(automatic.Rows[0].Id, Guid.NewGuid().ToString("N"), automatic.Rows[0].Text)], store.Root, RunId: Guid.NewGuid().ToString("N"));
            store.BeginRun(automatic, automaticJob);
            var projectsChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                Window? manager = null;
                try
                {
                    manager = owner.OwnedWindows.Cast<Window>().Single(); manager.UpdateLayout();
                    Assert.AreEqual("SpeechAssets.Projects.Window", System.Windows.Automation.AutomationProperties.GetAutomationId(manager));
                    Button Action(string key) => Desc<Button>(manager).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "SpeechAssets.Projects." + key);
                    void Click(string key) => Action(key).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var tables = Desc<System.Windows.Controls.DataGrid>(manager).ToArray();
                    var manual = tables.Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).EndsWith(".Manual"));
                    var auto = tables.Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t).EndsWith(".Automatic"));
                    Assert.AreEqual(1, auto.Items.Count); Assert.AreEqual(0, manual.Items.Count);
                    Click("Fix"); Assert.AreEqual(1, manual.Items.Count);
                    var projectId = control.SessionId;
                    Click("New"); Assert.AreNotEqual(projectId, control.SessionId); Assert.HasCount(0, control.CurrentProject.Rows);
                    manual.SelectedIndex = 0; Click("Open"); Assert.AreEqual(projectId, control.SessionId);
                    Assert.AreEqual(2, control.CurrentProject.Rows.Count(r => r.Confirmed));
                    Desc<TextBox>(manager).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "SpeechAssets.Projects.Name").Text = "Checked voice pack";
                    Click("Rename"); Assert.AreEqual("Checked voice pack", store.Load(projectId).Name);
                    manager.UpdateLayout();
                    var shot = new RenderTargetBitmap((int)manager.ActualWidth, (int)manager.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    shot.Render(manager); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot));
                    using var file = File.Create(Path.Combine(root, "projects.png")); png.Save(file);
                    projectsChecked.TrySetResult();
                }
                catch (Exception error) { projectsChecked.TrySetException(error); }
                finally { manager?.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            Desc<Button>(control).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "SpeechAssets.Projects")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await projectsChecked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { owner.Close(); }
    });
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var descendant in Desc<T>(child)) yield return descendant;
        }
    }
    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        static double L(System.Windows.Media.Color color)
        {
            static double C(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var first = L(a); var second = L(b); return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });
    private static Task Sta(Func<Task> action, TimeSpan? timeout = null)
    {
        // WPF retains native media/render callbacks. Keep this STA alive across cases instead of tearing it down under them.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UiDispatcher.Value.BeginInvoke(async () =>
        {
            try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(2));
    }
}
