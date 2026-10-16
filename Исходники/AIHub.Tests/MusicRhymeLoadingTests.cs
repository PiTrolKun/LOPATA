using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeLoadingTests
{
    [TestMethod]
    public void RunningStagesAnimateOnlyMissingRequestedResults()
    {
        var step = Step() with { GenerationRequested = true };
        Assert.IsNull(MusicRhymeLoadingState.For(step, 0, true, false).MessageKey);
        Assert.IsTrue(MusicRhymeLoadingState.For(step, 1, true, false).Working);
        Assert.IsTrue(MusicRhymeLoadingState.For(step, 2, true, false).Working);
        step = step with { ProgramReady = true };
        Assert.IsNull(MusicRhymeLoadingState.For(step, 1, true, false).MessageKey);
        Assert.IsTrue(MusicRhymeLoadingState.For(step, 2, true, false).Working);
        step = step with { ModelReady = true };
        Assert.IsNull(MusicRhymeLoadingState.For(step, 2, true, false).MessageKey);
    }

    [TestMethod]
    public void AutomaticProgramCheckDoesNotPretendTheModelIsGenerating()
    {
        var step = Step();
        Assert.IsTrue(MusicRhymeLoadingState.For(step, 1, true, false).Working);
        var model = MusicRhymeLoadingState.For(step, 2, true, false);
        Assert.IsFalse(model.Working);
        Assert.AreEqual("Music.Rhyme.Loading.NotRequested", model.MessageKey);
        var complete = step with { ProgramReady = true, Stage = "Reference" };
        Assert.IsNull(MusicRhymeLoadingState.For(complete, 1, false, false).MessageKey);
        Assert.IsFalse(MusicRhymeLoadingState.For(complete, 2, false, false).Working);
    }

    [TestMethod]
    public void PausedFailedAndInactiveStagesNeverKeepSpinning()
    {
        var step = Step() with { GenerationRequested = true };
        var paused = MusicRhymeLoadingState.For(step, 2, true, true);
        Assert.IsFalse(paused.Working); Assert.AreEqual("Music.Rhyme.Loading.Paused", paused.MessageKey);
        foreach (var terminal in new[] { step with { Stage = "Partial" }, step with { Error = "Failure" } })
        {
            var state = MusicRhymeLoadingState.For(terminal, 2, true, false);
            Assert.IsFalse(state.Working); Assert.AreEqual("Music.Rhyme.Loading.Interrupted", state.MessageKey);
        }
        Assert.IsFalse(MusicRhymeLoadingState.For(step, 2, false, false).Working);
        Assert.IsFalse(MusicRhymeLoadingState.For(null, 1, true, false).Working);
    }

    [TestMethod]
    public void UserEditedDraftsStayVisibleWhileTheCalculationContinues()
    {
        var step = Step() with { GenerationRequested = true, ProgramEdited = true, ModelEdited = true };
        Assert.IsNull(MusicRhymeLoadingState.For(step, 1, true, false).MessageKey);
        Assert.IsNull(MusicRhymeLoadingState.For(step, 2, true, false).MessageKey);
        var rejectedReply = Step() with { Stage = "Partial", Error = "Music.Rhyme.InvalidReply", Reply = "Received draft", Model = "Received draft" };
        Assert.IsNull(MusicRhymeLoadingState.For(rejectedReply, 2, false, false).MessageKey);
    }

    [TestMethod]
    public Task AnimationStopsWhenTheStageBecomesPausedOrComplete() => ScenarioNavigationTests.Sta(() =>
    {
        var panel = new MusicRhymeLoadingPanel(1);
        var progress = Desc<ProgressBar>(panel).Single();
        panel.Apply(new("Music.Rhyme.Loading.Program", true), key => key);
        Assert.AreEqual(Visibility.Visible, panel.Visibility);
        Assert.AreEqual(Visibility.Visible, progress.Visibility); Assert.IsTrue(progress.IsIndeterminate);
        panel.Apply(new("Music.Rhyme.Loading.Paused"), key => key);
        Assert.AreEqual(Visibility.Visible, panel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, progress.Visibility); Assert.IsFalse(progress.IsIndeterminate);
        panel.Apply(new((string?)null), key => key);
        Assert.AreEqual(Visibility.Collapsed, panel.Visibility); Assert.IsFalse(progress.IsIndeterminate);
    });

    [TestMethod]
    public Task WorkshopShowsOriginalImmediatelyAndUnfinishedResultsWithoutFakeActivity() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl(); view.Editor.Lyrics = "Кот поёт у окна.";
        var project = view.Projects.Current.Id;
        var window = new MusicRhymeWindow(view.Rhyme, project, key => key);
        try
        {
            var original = Desc<TextBox>(window).Single(item => Id(item) == "Music.Rhyme.Column.0");
            Assert.AreEqual(view.Editor.Lyrics, original.Text);
            var restart = Desc<Button>(window).Single(item => Id(item) == "Music.Rhyme.Restart");
            Assert.IsTrue(restart.IsEnabled);
            Assert.IsTrue(Desc<ProgressBar>(window).All(item => !item.IsIndeterminate));
            view.Rhyme.Accept(Step(project) with { Original = view.Editor.Lyrics, ProgramReady = true, Program = "Поёт у окна кот." });
            Assert.AreEqual("Поёт у окна кот.", Desc<TextBox>(window).Single(item => Id(item) == "Music.Rhyme.Column.1").Text);
            Assert.AreEqual(Visibility.Collapsed, Desc<MusicRhymeLoadingPanel>(window).Single(item => Id(item) == "Music.Rhyme.Loading.1").Visibility);
            Assert.IsTrue(Desc<ProgressBar>(window).All(item => !item.IsIndeterminate));
            window.ShowSettings(); Assert.IsFalse(restart.IsEnabled);
            Desc<Button>(window).Single(item => Id(item) == "Music.Rhyme.Settings.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(restart.IsEnabled);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task ActiveWorkshopLoadingRendersInBothLanguagesWithoutLaunchingAModel() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl(); view.Editor.Lyrics = "Кот поёт у окна.";
        var preferencesPath = Path.Combine(Path.GetTempPath(), "lopata-rhyme-loading-" + Guid.NewGuid().ToString("N") + ".json");
        using var owner = new MusicRhymeController(view, new MusicRhymePreferences(preferencesPath));
        owner.ProgramAvailable = () => true;
        var pending = new TaskCompletionSource<MusicRhymeStep>();
        MusicRhymeStep? started = null;
        owner.ProgramRunner = (step, _, _, _) => { started = step; return pending.Task; };
        var checking = owner.CheckAfterEditingAsync();
        try
        {
            Assert.IsNotNull(started);
            Assert.IsTrue(owner.IsRunning(view.Projects.Current.Id, started.Request.Id));
            foreach (var (language, width, dark) in new[] { ("ru", 1100d, true), ("en", 700d, false) })
            {
                var localizer = new LocalizationService(); localizer.Load(language);
                var resources = ScenarioNavigationTests.Create(language, 1.25, dark).Resources;
                resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke;
                resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
                var window = new MusicRhymeWindow(owner, view.Projects.Current.Id, localizer.T, resources) { Width = width, Height = 700 };
                try
                {
                    window.Show(); window.UpdateLayout();
                    var program = Desc<ProgressBar>(window).Single(item => Id(item) == "Music.Rhyme.Loading.Progress.1");
                    var model = Desc<ProgressBar>(window).Single(item => Id(item) == "Music.Rhyme.Loading.Progress.2");
                    Assert.IsTrue(program.IsIndeterminate); Assert.IsFalse(model.IsIndeterminate);
                    Assert.IsFalse(Desc<TextBox>(window).Single(item => Id(item) == "Music.Rhyme.Column.1").IsEnabled);
                    Assert.IsTrue(Desc<Button>(window).Single(item => Id(item) == "Music.Rhyme.Restart").IsEnabled);
                    var widths = Desc<TextBox>(window).Where(item => Id(item).StartsWith("Music.Rhyme.Column.", StringComparison.Ordinal))
                        .Select(item => item.ActualWidth).ToArray();
                    Assert.AreEqual(3, widths.Length);
                    Assert.IsTrue(widths.Max() - widths.Min() <= 2, "All three comparison columns must stay equal.");
                    Capture(window, language + "-program-loading");
                }
                finally { window.Close(); }
            }
            var ready = owner.Accept(started with { ProgramReady = true, Program = "Поёт у окна кот.", Stage = "Reference" });
            pending.TrySetResult(ready); AwaitOnDispatcher(checking);
            var readyWindow = new MusicRhymeWindow(owner, view.Projects.Current.Id, key => key);
            try
            {
                Assert.IsTrue(Desc<TextBlock>(readyWindow).Any(item => item.Text.StartsWith("Music.Rhyme.ProgramReady", StringComparison.Ordinal)));
                Assert.IsTrue(Desc<ProgressBar>(readyWindow).All(item => !item.IsIndeterminate));
            }
            finally { readyWindow.Close(); }
        }
        finally
        {
            if (!checking.IsCompleted) { pending.TrySetCanceled(); AwaitOnDispatcher(checking); }
        }
    });

    private static void AwaitOnDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start(); Dispatcher.PushFrame(frame); timeout.Stop();
        }
        Assert.IsTrue(task.IsCompleted, "The mocked program check did not finish."); task.GetAwaiter().GetResult();
    }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_RHYME_LOADING_UI") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }

    private static MusicRhymeStep Step(string? project = null)
    {
        const string text = "Кот поёт у окна.";
        return new() { Request = new(Guid.NewGuid().ToString("N"), project ?? Guid.NewGuid().ToString("N"), text, 0, text.Length,
            "core", null, "", "ru"), Original = text };
    }
    private static string Id(DependencyObject item) => AutomationProperties.GetAutomationId(item);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
}
