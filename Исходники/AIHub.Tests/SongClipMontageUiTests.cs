using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipMontageUiTests
{
    [TestMethod]
    public Task LegacyDefaultKeepsOptionalModeSettingsAbsentAndItsSnapshotUnchanged() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project();
        var control = fixture.Control(project);
        var mode = Find<ComboBox>(control, "ClipMode");
        Assert.AreEqual(0, mode.SelectedIndex);
        Assert.AreEqual(Visibility.Collapsed, Parent(Find<ComboBox>(control, "MontageClipSeconds")).Visibility);
        Assert.IsTrue(control.Checkpoint());
        var saved = fixture.Store.Load(project.Id);
        Assert.IsNull(saved.Settings.Motion.Mode); Assert.IsNull(saved.Settings.Montage);
        var json = JsonSerializer.Serialize(saved.Settings);
        Assert.DoesNotContain("\"Mode\"", json); Assert.DoesNotContain("\"Montage\"", json);

        var legacy = new SongClipAttempt(Guid.NewGuid().ToString("N"), project.Id, project.Song,
            project.Reference, project.Name, project.Lyrics, project.Timings, saved.Settings with { Seed = 123 }, "", 8)
        { Model = project.Model };
        saved.PendingAttemptId = legacy.Id; fixture.Store.Save(saved); fixture.Store.EnsureAttemptSnapshot(legacy);
        var snapshot = fixture.Store.Resolve(project.Id, "attempts/" + legacy.Id + "/snapshot.json");
        var before = File.ReadAllBytes(snapshot);
        var reopened = fixture.Control(saved);
        Assert.AreEqual(legacy.Id, reopened.SelectStartAttempt().Id);
        Find<ComboBox>(reopened, "ClipMode").SelectedIndex = 1;
        Assert.AreNotEqual(legacy.Id, reopened.SelectStartAttempt().Id,
            "A different rendering contract must not silently resume the connected attempt.");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(snapshot));
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task SwitchingModesKeepsIndependentSettingsWithinTheSameProject(string language) => OnUi(() =>
    {
        using var fixture = new Fixture(language);
        var project = fixture.Project(); var control = fixture.Control(project);
        var mode = Find<ComboBox>(control, "ClipMode");
        var size = Find<ComboBox>(control, "MotionSize"); var steps = Find<ComboBox>(control, "MotionSteps");
        var duration = Find<ComboBox>(control, "MontageClipSeconds");
        size.SelectedIndex = 1; steps.SelectedItem = 30;
        var connected = fixture.Store.Load(project.Id).Settings.Motion;
        mode.SelectedIndex = 1;
        Assert.AreEqual(0, size.SelectedIndex); Assert.AreEqual(20, steps.SelectedItem); Assert.AreEqual(5, duration.SelectedItem);
        Assert.AreEqual(Visibility.Visible, Parent(duration).Visibility);
        size.SelectedIndex = 2; steps.SelectedItem = 12; duration.SelectedItem = 8;
        var montage = fixture.Store.Load(project.Id);
        Assert.AreEqual(project.Id, montage.Id); Assert.AreEqual(project.Song, montage.Song);
        Assert.AreEqual(project.Lyrics, montage.Lyrics); Assert.AreEqual(project.Timings, montage.Timings);
        Assert.AreEqual(connected with { Mode = "montage" }, montage.Settings.Motion);
        Assert.AreEqual(new SongClipMontageSettings { Width = 480, Height = 864, Steps = 12, ClipSeconds = 8 }, montage.Settings.Montage);
        mode.SelectedIndex = 0;
        Assert.AreEqual(1, size.SelectedIndex); Assert.AreEqual(30, steps.SelectedItem);
        Assert.AreEqual(Visibility.Collapsed, Parent(duration).Visibility);
        Assert.IsTrue(control.Checkpoint());
        var returned = fixture.Store.Load(project.Id);
        Assert.AreEqual(connected, returned.Settings.Motion);
        Assert.AreEqual(montage.Settings.Montage, returned.Settings.Montage);
        var reopened = fixture.Control(returned);
        Find<ComboBox>(reopened, "ClipMode").SelectedIndex = 1;
        Assert.AreEqual(2, Find<ComboBox>(reopened, "MotionSize").SelectedIndex);
        Assert.AreEqual(12, Find<ComboBox>(reopened, "MotionSteps").SelectedItem);
        Assert.AreEqual(8, Find<ComboBox>(reopened, "MontageClipSeconds").SelectedItem);
        Assert.IsInstanceOfType<Button>(Find<Button>(control,"Model"));
        Assert.AreEqual("h3_music_context",SongClipVariantCatalog.Find("montage").Model);

    });

    [TestMethod]
    public Task BusyModeCannotChangeAndHistoryUsesTheSavedResultMode() => OnUi(() =>
    {
        using var fixture = new Fixture("en"); var project = fixture.Project(); var control = fixture.Control(project);
        var mode = Find<ComboBox>(control, "ClipMode");
        typeof(SongClipControl).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, true);
        typeof(SongClipControl).GetMethod("SetInputs", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [false]);
        Assert.IsFalse(mode.IsEnabled); Assert.IsFalse(Find<ComboBox>(control, "MontageClipSeconds").IsEnabled);
        mode.SelectedIndex = 1;
        Assert.IsNull(fixture.Store.Load(project.Id).Settings.Motion.Mode);
        var result = new SongClipResult("result", "results/clip.mp4", "h3_music_context", 20, DateTimeOffset.UtcNow);
        var label = typeof(SongClipControl).GetMethod("ResultLabel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var connected = (string)label.Invoke(control, [result])!;
        var montage = (string)label.Invoke(control, [result with { Mode = "montage" }])!;
        Assert.Contains(fixture.Localize.T("SongClip.ConnectedMode"), connected);
        Assert.Contains(fixture.Localize.T("SongClip.MontageMode"), montage);
        Assert.DoesNotContain(fixture.Localize.T("SongClip.MontageMode"), connected);
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task PausedAttemptKeepsItsVisibleModeAndParametersLockedUntilItIsStopped(string language) => OnUi(async () =>
    {
        using var fixture = new Fixture(language); var project = fixture.Project();
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), project.Id, project.Song, "", project.Name,
            project.Lyrics, project.Timings, project.Settings with { Seed = 123 }, "", 8) { Model = project.Model };
        project.PendingAttemptId = attempt.Id; fixture.Store.Save(project); fixture.Store.EnsureAttemptSnapshot(attempt);
        var snapshot = fixture.Store.Resolve(project.Id, "attempts/" + attempt.Id + "/snapshot.json");
        var before = File.ReadAllBytes(snapshot);
        var controller = new BackgroundOperationController(new BackgroundOperationStore(fixture.File("background.json")));
        ApplicationBackgroundOperations.Current = controller;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();
        var state = new BackgroundOperationState { Kind = H3MusicRunner.Kind, Title = "H3", Project = project.Id,
            Input = JsonSerializer.SerializeToElement(attempt) };
        var work = controller.RunAsync(state, async token =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 1;
        }, () => Task.CompletedTask, lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var control = fixture.Control(project); control.Configure(fixture.Localize.T, Path.GetTempPath(), 8);
            var refresh = typeof(SongClipControl).GetMethod("RefreshGenerationActions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            refresh.Invoke(control, null);
            Assert.IsFalse(Find<ComboBox>(control, "ClipMode").IsEnabled);
            await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            refresh.Invoke(control, null);
            typeof(SongClipControl).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
            var status = (TextBlock)typeof(SongClipControl).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
            Assert.AreEqual(fixture.Localize.T("SongClip.Paused"), status.Text);
            foreach (var id in new[] { "ClipMode", "MotionSize", "MotionSteps", "MontageClipSeconds" })
                Assert.IsFalse(Find<ComboBox>(control, id).IsEnabled, "A parked immutable attempt still owns " + id);
            Assert.IsFalse(Find<Button>(control,"Model").IsEnabled);
            Assert.IsTrue(Find<Button>(control, "Generate").IsEnabled);
            Assert.AreEqual(fixture.Localize.T("SongClip.Resume"), AutomationProperties.GetName(Find<Button>(control, "Generate")));
            var mode = Find<ComboBox>(control, "ClipMode");
            var peer = new ComboBoxAutomationPeer(mode);
            Assert.ThrowsExactly<ElementNotEnabledException>(() => ((IExpandCollapseProvider)peer).Expand());
            Assert.AreEqual(0, mode.SelectedIndex);
            Assert.IsTrue(control.Checkpoint());
            Assert.IsNull(fixture.Store.Load(project.Id).Settings.Motion.Mode);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(snapshot));
        }
        finally
        {
            lifetime.Cancel();
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task MontageProgressUsesReadableRhythmAndAssemblyStages(string language) => OnUi(() =>
    {
        using var fixture = new Fixture(language); var control = fixture.Control(fixture.Project());
        var format = typeof(SongClipControl).GetMethod("FormatEngineProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (stage, key) in new[] { ("montage-analysis", "MontageAnalysis"), ("montage-assembly", "MontageAssembly") })
            Assert.AreEqual(fixture.Localize.T("SongClip." + key), format.Invoke(control, [JsonSerializer.Serialize(new { kind = "stage", stage })]));
        var ready = JsonSerializer.Serialize(new { kind = "analysis", slots = new[] { new { index = 1 }, new { index = 2 } } });
        Assert.AreEqual(string.Format(System.Globalization.CultureInfo.CurrentCulture, fixture.Localize.T("SongClip.MontageAnalysisReady"), 2), format.Invoke(control, [ready]));
    });

    [TestMethod]
    [DataRow("ru", true, 1700, 960)]
    [DataRow("ru", false, 860, 560)]
    [DataRow("en", false, 1700, 960)]
    [DataRow("en", true, 860, 560)]
    public Task BothModeChoicesRemainVisibleLocalizedAndThemed(string language, bool dark, int width, int height) => OnUi(() =>
    {
        using var fixture = new Fixture(language); var control = fixture.Control(fixture.Project());
        var host = Host(control, dark, width, height);
        try
        {
            host.Show(); Flush(host);
            var mode = Find<ComboBox>(control, "ClipMode");
            Assert.AreEqual(fixture.Localize.T("SongClip.ClipMode"), AutomationProperties.GetName(mode));
            foreach (var index in new[] { 0, 1 })
            {
                mode.SelectedIndex = index; mode.BringIntoView(); Flush(host);
                Assert.IsTrue(mode.IsVisible); Assert.IsTrue(mode.ActualWidth >= 100); Assert.IsTrue(mode.ActualHeight > 0);
                var text = ((ComboBoxItem)mode.SelectedItem).Content?.ToString()!;
                Assert.AreEqual(fixture.Localize.T(index == 0 ? "SongClip.ConnectedMode" : "SongClip.MontageMode"), text);
                Assert.DoesNotContain("SongClip.", text);
                if (index == 1)
                {
                    var duration = Find<ComboBox>(control, "MontageClipSeconds"); duration.BringIntoView(); Flush(host);
                    Assert.IsTrue(duration.IsVisible); Assert.IsTrue(duration.ActualWidth >= 100);
                }
                WriteEvidence(host, language, dark, width, height, index);
            }
        }
        finally { host.Close(); Flush(host); }
    });

    private static FrameworkElement Parent(FrameworkElement element) => (FrameworkElement)LogicalTreeHelper.GetParent(element);
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == "SongClip." + id);

    private sealed class Fixture : IDisposable
    {
        private readonly VideoPreparationTests.TestFiles _files = new();
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        internal SongClipStore Store { get; }
        internal LocalizationService Localize { get; } = new();
        internal Fixture(string language)
        {
            Store = new(_files.File("projects")); Localize.Load(language); ApplicationBackgroundOperations.Current = null;
        }
        internal SongClipProject Project()
        {
            var project = new SongClipProject { Model = "h3_music_context", Name = "Song", Song = "attachments/song.wav",
                Lyrics = "A cat watches the moon.", Timings = "[00:01.00]A cat watches the moon.", Settings = new() { Prompt = "" } };
            Store.Save(project); return project;
        }
        internal SongClipControl Control(SongClipProject project)
        {
            var control = new SongClipControl(Localize.T, Store); control.OpenProject(project.Id); return control;
        }
        internal string File(string relative) => _files.File(relative);
        public void Dispose() { ApplicationBackgroundOperations.Current = _previous; _files.Dispose(); }
    }

    private static Window Host(SongClipControl control, bool dark, double width, double height)
    {
        var host = new Window { Content = control, Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false };
        foreach (var key in new[] { "UiBodyFontSize", "UiButtonFontSize", "UiSmallFontSize", "UiTinyFontSize" }) host.Resources[key] = 14d;
        host.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.WhiteSmoke;
        host.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(15, 23, 38)) : Brushes.White;
        host.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        host.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
        host.Resources["SecondaryButtonBackgroundBrush"] = host.Resources["PanelBrush"];
        host.Resources["InputBrush"] = host.Resources["PanelBrush"];
        host.Resources["LineBrush"] = Brushes.Gray; host.Resources["AccentBrush"] = Brushes.RoyalBlue;
        host.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        host.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        return host;
    }

    private static void WriteEvidence(Window host, string language, bool dark, int width, int height, int mode)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var image = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(host); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(root, $"montage-{language}-{(dark ? "dark" : "light")}-{width}x{height}-{mode}.png")); encoder.Save(file);
    }

    private static void Flush(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
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
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
