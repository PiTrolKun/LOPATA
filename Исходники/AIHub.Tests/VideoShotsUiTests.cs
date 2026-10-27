using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoShotsUiTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, IVideoConceptAssistant assistant)
    {
        var p = VideoShotsTests.Root(); VideoPreparationArchive.Save(p, files.File("initial.lopataAIVid"));
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(files.File("initial.lopataAIVid"));
        var l = new LocalizationService(); l.Load("ru"); var control = new VideoPreparationControl(l.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>([new("core", "Qwen", new DebugModelInfo
        { Path = "test.gguf", Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")]), new(files.File("receipts"))); return control;
    }
    private sealed class StreamingAssistant : IVideoConceptAssistant, IVideoShotsProgress
    {
        public int Calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VideoConceptAnswer> GenerateAsync(VideoConceptRequest r, CancellationToken token) => GenerateShotsAsync(r, null, token);
        public async Task<VideoConceptAnswer> GenerateShotsAsync(VideoConceptRequest r, Action<string>? reasoning, CancellationToken token)
        {
            Calls++; if (Calls == 1) { reasoning?.Invoke("Проверяю порядок событий и непрерывность действия."); Entered.SetResult(); await Release.Task.WaitAsync(token); }
            return VideoShotsTests.Answer(r);
        }
    }
    [TestMethod]
    public Task FirstEntryAutoBuildShowsSpinnerAndActualReasoningThenReentryKeepsEdits() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new StreamingAssistant(); var control = Create(files, assistant);
        var owner = new Window { Content = control, Width = 1250, Height = 950 }; owner.Show();
        try
        {
            await assistant.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6)); await Task.Delay(35);
            Assert.IsNotNull(Find<System.Windows.Shapes.Ellipse>(control, "VideoPrep.Shots.Spinner"));
            StringAssert.Contains(Find<TextBox>(control, "VideoPrep.Shots.Reasoning").Text, "непрерывность");
            Assert.IsFalse(Find<Button>(control, "VideoPrep.Next").IsEnabled); Assert.IsFalse(control.BackStepIfNeeded());
            assistant.Release.SetResult();
            for (var i = 0; i < 100 && !VideoShots.Ready(control.Session.Project); i++) await Task.Delay(20);
            await Task.Delay(30); var p = control.Session.Project; Assert.IsTrue(VideoShots.Ready(p)); Assert.AreEqual(3, assistant.Calls);
            var field = Find<TextBox>(control, "VideoPrep.ShotsStart"); Assert.IsTrue(SpellCheck.GetIsEnabled(field)); field.Text = "Моя ручная правка";
            Assert.IsTrue(control.Save()); Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("references", p.CurrentStep);
            Assert.IsTrue(control.Next()); await Task.Delay(70); Assert.AreEqual(3, assistant.Calls);
            Assert.AreEqual("Моя ручная правка", Find<TextBox>(control, "VideoPrep.ShotsStart").Text);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Any(e => AutomationProperties.GetAutomationId(e) == "VideoPrep.ShotsSavedOptions"));
        }
        finally { owner.Close(); }
    });
    [TestMethod]
    public Task SceneSelectionReordersScenesAndTranslationKeepsSeparateManualHistory() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => Task.FromResult(VideoShotsTests.Answer(r)) };
        var control = Create(files, assistant); await control.StartShotsAsync(); var p = control.Session.Project; var d = p.Shots!;
        var first = d.Scenes[0].Id;
        Find<Button>(control, "VideoPrep.Shots.Scene." + first).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("", d.SelectedShotId); Find<Button>(control, "VideoPrep.ShotsDown").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(first, d.Scenes[1].Id);
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("shots_translation", p.CurrentStep);
        var originalVersion = VideoShots.SelectedTranslation(d, first)!;
        var name = Find<TextBox>(control, "VideoPrep.ShotsSceneName"); Assert.IsTrue(SpellCheck.GetIsEnabled(name));
        name.Text = "My English edit"; Assert.IsTrue(control.Save());
        Assert.AreEqual("My English edit", VideoShots.SelectedTranslation(d, first)!.Scene.Name); Assert.AreNotEqual(originalVersion.Id, VideoShots.SelectedTranslation(d, first)!.Id);
        Assert.AreNotEqual("My English edit", d.Scenes[1].Name); Assert.IsTrue(control.Next()); Assert.AreEqual("storyboard", p.CurrentStep);
        Assert.IsTrue(control.BackStepIfNeeded()); Find<Button>(control, "VideoPrep.ShotsEnglishPrevious").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(originalVersion.Id, VideoShots.SelectedTranslation(d, first)!.Id);
    });
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));
}
