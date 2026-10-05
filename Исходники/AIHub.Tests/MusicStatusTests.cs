using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicStatusTests
{
    [TestMethod]
    public void ConfirmedPauseFreezesClockAndResumeRetainsElapsedTime()
    {
        var now = TimeSpan.Zero; var status = new MusicGenerationStatus(() => now);
        var first = status.Begin(); now = TimeSpan.FromSeconds(90);
        status.Report(first, MusicGenerationStage.Paused);
        now = TimeSpan.FromSeconds(190); Assert.AreEqual(TimeSpan.FromSeconds(90), status.Snapshot.Elapsed);
        var resumed = status.Begin(status.Snapshot.Elapsed);
        now += TimeSpan.FromSeconds(5);
        Assert.AreEqual(TimeSpan.FromSeconds(95), status.Snapshot.Elapsed);
        Assert.IsFalse(status.Report(first, MusicGenerationStage.Completed));
        status.Report(resumed, MusicGenerationStage.Completed);
        Assert.AreEqual(100d, status.Snapshot.Percent);
    }

    [TestMethod]
    public void ClockStopsOnFailureAndOldEventsCannotChangeNewOperation()
    {
        var now = TimeSpan.Zero; var status = new MusicGenerationStatus(() => now);
        var first = status.Begin(); now = TimeSpan.FromSeconds(90);
        Assert.IsTrue(status.Report(first, MusicGenerationStage.Sound, 72, true));
        Assert.IsTrue(status.Report(first, MusicGenerationStage.Error));
        now = TimeSpan.FromSeconds(180);
        Assert.AreEqual(TimeSpan.FromSeconds(90), status.Snapshot.Elapsed);
        Assert.AreEqual(72d, status.Snapshot.Percent); Assert.IsTrue(status.Snapshot.Estimated);
        Assert.IsFalse(status.Report(first, MusicGenerationStage.Completed));
        var second = status.Begin(); Assert.AreEqual(TimeSpan.Zero, status.Snapshot.Elapsed); Assert.IsNull(status.Snapshot.Percent);
        Assert.IsFalse(status.Report(first, MusicGenerationStage.Sound, 90));
        Assert.AreEqual(second, status.Snapshot.Operation);
        now += TimeSpan.FromSeconds(5); status.Report(second, MusicGenerationStage.Cancelled);
        now += TimeSpan.FromSeconds(5); Assert.AreEqual(TimeSpan.FromSeconds(5), status.Snapshot.Elapsed);
    }

    [TestMethod]
    public void TimeUsesMinutesAndShortenedSecondsRatherThanDecimalMinutes()
    {
        foreach (var (seconds, expected) in new[] { (0, "0 с"), (8, "8 с"), (59, "59 с"),
            (60, "1.0"), (61, "1.01"), (65, "1.05"), (70, "1.1"), (90, "1.3"), (119, "1.59"), (120, "2.0"), (3600, "60.0") })
            Assert.AreEqual(expected, MusicGenerationStatus.CompactTime(TimeSpan.FromSeconds(seconds), "с"));
    }

    [TestMethod]
    public Task StagesShowRealUnknownAndEstimatedProgressInBothLanguagesAndThemes() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            var now = TimeSpan.Zero; var telemetry = new MusicGenerationStatus(() => now);
            using var status = new MusicStatusControl(telemetry); l.Load(language); status.Localize(l.T);
            var window = Host(status, dark, 520, 110);
            Modal(window, () =>
            {
                var figure = Desc<MusicViolinist>(status).Single(); var bar = Desc<ProgressBar>(status).Single();
                Assert.AreEqual(dark ? Brushes.White : Brushes.Black, Desc<TextBlock>(status).First().Foreground);
                Snapshot(window, "status-" + language + "-" + dark + "-Idle");
                Assert.AreEqual(Colors.Black, MusicViolinist.StageColor(figure.Stage)); Assert.IsFalse(figure.Animate);
                var operation = telemetry.Begin(); Assert.IsNull(telemetry.Snapshot.Percent);
                Assert.AreEqual(SystemParameters.ClientAreaAnimation, bar.IsIndeterminate);
                var states = new[] { MusicGenerationStage.Loading, MusicGenerationStage.Planning,
                    MusicGenerationStage.Sequence, MusicGenerationStage.Sound, MusicGenerationStage.Encoding };
                foreach (var stage in states)
                {
                    now += TimeSpan.FromSeconds(18); telemetry.Report(operation, stage, 42, true);
                    Assert.IsFalse(bar.IsIndeterminate); Assert.AreEqual(42d, bar.Value);
                    Assert.AreEqual(stage, figure.Stage);
                    Assert.IsTrue(AutomationProperties.GetName(bar).Contains(l.T("Music.Status.Estimated").Replace("{0}", "42%")));
                    Snapshot(window, "status-" + language + "-" + dark + "-" + stage);
                }
                Assert.AreEqual("1.3", Desc<TextBlock>(status).Single(t => AutomationProperties.GetAutomationId(t) == "Music.Status.Time").Text);
                telemetry.Report(operation, MusicGenerationStage.Error);
                Assert.AreEqual(42d, bar.Value); Assert.AreEqual(Brushes.Firebrick, bar.Foreground); Assert.IsFalse(figure.Animate);
                Assert.AreEqual(Color.FromRgb(210, 40, 40), MusicViolinist.StageColor(figure.Stage));
                Snapshot(window, "status-" + language + "-" + dark + "-Error");
                var retry = telemetry.Begin(); telemetry.Report(retry, MusicGenerationStage.Completed);
                Assert.AreEqual(100d, bar.Value); Assert.IsFalse(figure.Animate); Assert.AreEqual(Brushes.ForestGreen, bar.Foreground);
            });
        }
    });

    [TestMethod]
    public Task LogCanBeSelectedWithoutNewEventsPullingItDownAndIsBounded() => ScenarioNavigationTests.Sta(() =>
    {
        using var status = new MusicStatusControl(); var l = new LocalizationService(); l.Load("ru"); status.Localize(l.T);
        var window = Host(status, true, 650, 180);
        Modal(window, () =>
        {
            var log = Desc<System.Windows.Controls.TextBox>(status).Single();
            for (var i = 0; i < 80; i++) status.AppendLog("Строка " + i);
            window.UpdateLayout(); log.ScrollToHome(); Pump(); log.Select(12, 6);
            var selected = log.SelectedText; var offset = log.VerticalOffset;
            status.AppendLog("Новое событие"); Pump();
            Assert.AreEqual(offset, log.VerticalOffset, 1); Assert.AreEqual(selected, log.SelectedText);
            Assert.IsTrue(log.IsReadOnly); Assert.AreEqual(Brushes.Black, log.Background);
            log.ScrollToEnd(); Pump(); status.AppendLog("Последнее событие"); Pump();
            Assert.IsTrue(log.VerticalOffset >= log.ExtentHeight - log.ViewportHeight - 2);
            status.AppendLog(string.Join('\n', Enumerable.Range(0, 550).Select(i => "Пакет " + i))); Pump();
            Assert.AreEqual(500, log.Text.Split('\n').Length); StringAssert.Contains(log.Text, "Пакет 549");
        });
    });

    [TestMethod]
    public Task HiddenPanelKeepsOperationClockAndWorkerEventsAreMarshalled() => ScenarioNavigationTests.Sta(() =>
    {
        var now = TimeSpan.Zero; var telemetry = new MusicGenerationStatus(() => now);
        using var status = new MusicStatusControl(telemetry); var l = new LocalizationService(); l.Load("ru"); status.Localize(l.T);
        var window = Host(status, true, 650, 110);
        Modal(window, () =>
        {
            var operation = telemetry.Begin(); status.Visibility = Visibility.Hidden; now = TimeSpan.FromSeconds(90);
            Task.Run(() => { telemetry.Report(operation, MusicGenerationStage.Sequence, 55); status.AppendLog("Worker event"); }).GetAwaiter().GetResult();
            Pump(); status.Visibility = Visibility.Visible; Pump();
            Assert.AreEqual(TimeSpan.FromSeconds(90), telemetry.Snapshot.Elapsed);
            Assert.AreEqual(55d, Desc<ProgressBar>(status).Single().Value);
            StringAssert.Contains(Desc<System.Windows.Controls.TextBox>(status).Single().Text, "Worker event");
            status.Dispose(); telemetry.Report(operation, MusicGenerationStage.Completed); Pump();
        });
    });

    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static Window Host(FrameworkElement content, bool dark, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height + 40, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["UiBodyFontSize"] = 14d;
        window.Background = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.WhiteSmoke; return window;
    }
    private static void Modal(Window window, Action action)
    {
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        { try { action(); } catch (Exception error) { failure = error; } finally { window.Close(); } }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Snapshot(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
        { var bounds = new Rect(visual.RenderSize); context.DrawRectangle(window.Background, null, bounds); context.DrawRectangle(new VisualBrush(visual), null, bounds); }
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
