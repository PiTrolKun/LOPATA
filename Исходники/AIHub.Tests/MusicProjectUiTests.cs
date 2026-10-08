using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicProjectUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task HistoryRestoresOnlyItsSelectedGroupAndManualProjectsHaveSeparateTable(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects")); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load(language); view.Localize(l.T); view.ConfigureOutput(files.Root, _ => { });
        var window = new Window { Content = view, Width = 1400, Height = 900, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        Theme(window, dark); Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            try {
                var controller = view.Projects; Assert.IsFalse(controller.Current.Persistent);
                view.Editor.Lyrics = "Черновик"; Assert.HasCount(0, projects.List());
                var expert = new MusicExpertSettings(); expert.Values["seed"] = 12;
                view.Generation.SetExpertSettings(expert, false); view.Generation.SetOptions(new("Первый", 1, 30) { Artist = "Автор", Comment = "Заметка" });
                var wishes = new MusicPreferences { NoChoir = true }; wishes.Select("genres", ["rock"]); view.Wishes.Apply(wishes);
                var first = jobs.Create(files.Root, files.Root, "Первый", 1, 30, "rock", view.Editor.Lyrics);
                first = controller.Record(first, controller.Capture()); controller.Outcome(first, MusicProjectOutcome.Failed, "Тестовый сбой");
                view.Editor.Lyrics = "Второй текст"; expert.Values["seed"] = 99; view.Generation.SetExpertSettings(expert, false);
                view.Generation.SetOptions(new("Второй", 3, null)); var second = jobs.Create(files.Root, files.Root, "Второй", 3, 360, "", view.Editor.Lyrics);
                second = controller.Record(second, controller.Capture()); controller.Outcome(second, MusicProjectOutcome.Cancelled);
                controller.Navigate(-1); Assert.AreEqual("Черновик", view.Editor.Lyrics); Assert.AreEqual("Второй", view.Generation.Options.Title);
                Assert.AreEqual(99d, view.Generation.ExpertSettings.Values["seed"]); controller.Navigate(1);
                controller.CycleMode(); controller.Navigate(-1); Assert.AreEqual("Второй текст", view.Editor.Lyrics);
                Assert.AreEqual("Первый", view.Generation.Options.Title); Assert.AreEqual("Автор", view.Generation.Options.Artist);
                Assert.AreEqual("Заметка", view.Generation.Options.Comment); Assert.AreEqual(30, view.Generation.Options.DurationSeconds);
                Assert.AreEqual(12d, view.Generation.ExpertSettings.Values["seed"]); Assert.IsTrue(view.Wishes.State.NoChoir);
                controller.CycleMode(); Assert.AreEqual(MusicHistoryMode.Off, controller.Mode); Assert.IsFalse(controller.CanNavigate(1));
                var third = jobs.Create(files.Root, files.Root, "", 1, 30, "", "Повтор старого текста");
                third = controller.Record(third, MusicProjectSnapshot.FromJob(third)); Assert.HasCount(3, controller.Current.Steps);
                Assert.AreEqual("Второй текст", controller.Current.Steps[1].Snapshot.Lyrics); Assert.AreEqual("Тестовый сбой", controller.Current.Steps[0].Message);
                controller.SetBusy(true); Assert.Throws<InvalidOperationException>(controller.New); Assert.Throws<InvalidOperationException>(controller.Fix);
                Assert.IsFalse(controller.CanNavigate(-1)); controller.SetBusy(false);
                controller.Rename("Ручной проект"); controller.Fix(); var savedId = controller.Current.Id;
                controller.New(); Assert.AreEqual("", view.Editor.Lyrics); Assert.IsFalse(controller.Current.Persistent);
                controller.Open(savedId); Assert.AreEqual("Второй текст", view.Editor.Lyrics); Assert.AreEqual("Первый", view.Generation.Options.Title);
                Assert.IsTrue(controller.Current.Manual); Assert.HasCount(3, controller.Current.Steps);
                var buttons = ScenarioNavigationTests.LogicalDescendants(view).OfType<Button>().ToArray();
                Assert.AreEqual(l.T("Music.Projects.Title"), buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Projects.Manage").ToolTip);
                window.UpdateLayout(); Capture(view, "workspace");
                var manager = new MusicProjectsWindow(controller, l.T) { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
                Theme(manager, dark); manager.Show(); manager.UpdateLayout();
                var tables = ScenarioNavigationTests.LogicalDescendants(manager).OfType<DataGrid>().ToArray();
                Assert.AreEqual(1, tables.Single(t => AutomationProperties.GetAutomationId(t) == "Music.Projects.Manual").Items.Count);
                Assert.AreEqual(0, tables.Single(t => AutomationProperties.GetAutomationId(t) == "Music.Projects.Automatic").Items.Count);
                foreach (var table in tables) Assert.IsTrue(table.Columns[0].ActualWidth >= 180);
                Capture(manager, "manager"); manager.Close();
                foreach (var control in ScenarioNavigationTests.LogicalDescendants(view).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.History."))) {
                    var point = control.TranslatePoint(new Point(), view.Editor);
                    Assert.IsTrue(point.X >= 0 && point.X + control.ActualWidth <= view.Editor.ActualWidth + 1);
                }
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
            void Capture(FrameworkElement control, string suffix) {
                var folder = Environment.GetEnvironmentVariable("LOPATA_MUSIC_PROJECT_UI_EVIDENCE"); if (string.IsNullOrEmpty(folder)) return;
                Directory.CreateDirectory(folder); var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
                    var rect = new Rect(control.RenderSize); canvas.DrawRectangle(dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke, null, rect);
                    canvas.DrawRectangle(new VisualBrush(control), null, rect);
                }
                var bitmap = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(folder, language + "-" + suffix + ".png")); encoder.Save(image);
            }
        }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });
    [TestMethod]
    public Task RecoveryRepairsExactInterruptedLinkWithoutAddingHistoryAndLeavesLegacyAlone() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T);
        var job = jobs.Create(files.Root, files.Root, "", 1, 30, "", "Восстановление");
        var saved = projects.AddRequest(projects.CreateDraft(), job.Id, MusicProjectSnapshot.FromJob(job));
        var restored = view.Projects.RestoreJob(job); Assert.AreEqual(saved.Id, restored.ProjectId);
        Assert.HasCount(1, projects.Load(saved.Id).Steps); Assert.AreEqual("Восстановление", view.Editor.Lyrics);
        view.Projects.RestoreJob(jobs.Load(job.Id)); Assert.HasCount(1, projects.Load(saved.Id).Steps);
        var legacy = jobs.Create(files.Root, files.Root, "", 1, 30, "", "Старый WAV");
        Assert.IsNull(view.Projects.RestoreJob(legacy).ProjectId); Assert.HasCount(1, projects.List());
    });
    private static void Theme(Window window, bool dark)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["UiBodyFontSize"] = 16d; window.Background = (Brush)window.Resources["PanelBrush"];
    }
    [TestMethod]
    public Task BackgroundPausePersistsAttemptStatusAndKeepsProjectNavigationLocked() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var store = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var background = new BackgroundOperationController(new(Path.Combine(files.Root, "background.json")));
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = background;
        using var lifetime = new CancellationTokenSource(); Task<int>? running = null;
        try {
            using var view = new MusicWorkspaceControl(store, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
            var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T);
            var job = jobs.Create(files.Root, files.Root, "", 1, 30, "", "Текст"); job = view.Projects.Record(job, MusicProjectSnapshot.FromJob(job));
            var project = view.Projects.Current.Id;
            running = background.RunAsync(new() { Kind = MusicGenerationRunner.BackgroundKind, Title = "Тест", Project = job.Id,
                Input = System.Text.Json.JsonSerializer.SerializeToElement(new { JobId = job.Id }) },
                async token => { await Task.Delay(Timeout.Infinite, token); return 1; }, () => Task.CompletedTask, lifetime.Token);
            Pump(() => view.Projects.Busy && store.Load(project).Steps[0].Outcome == MusicProjectOutcome.Running);
            var pause = background.PauseAsync(); Pump(() => pause.IsCompleted && store.Load(project).Steps[0].Outcome == MusicProjectOutcome.Paused);
            pause.GetAwaiter().GetResult(); Assert.IsTrue(view.Projects.Busy);
            Assert.Throws<InvalidOperationException>(() => view.Projects.Open(project)); Assert.Throws<InvalidOperationException>(view.Projects.New);
            Assert.HasCount(1, store.Load(project).Steps);
            var resume = background.ResumeAsync(default); Pump(() => resume.IsCompleted && store.Load(project).Steps[0].Outcome == MusicProjectOutcome.Running);
            resume.GetAwaiter().GetResult(); Assert.HasCount(1, store.Load(project).Steps);
            lifetime.Cancel(); Pump(() => running.IsCompleted); Assert.Throws<OperationCanceledException>(() => running.GetAwaiter().GetResult());
            background.DiscardPending(); Pump(() => !view.Projects.Busy);
        }
        finally { lifetime.Cancel(); ApplicationBackgroundOperations.Current = previous; }
        static void Pump(Func<bool> done) {
            var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(8);
            var clock = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
            clock.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
            clock.Start(); try { Dispatcher.PushFrame(frame); } finally { clock.Stop(); }
            Assert.IsTrue(done(), "Background event did not reach the project UI.");
        }
    });
}
