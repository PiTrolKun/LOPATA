using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoShotsLayoutTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static VideoPreparationControl Control(VideoPreparationTests.TestFiles files, VideoPreparationProject p)
    {
        var path = files.File("layout.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path);
        var localize = new LocalizationService(); localize.Load("ru"); return new(localize.T, session);
    }
    [TestMethod]
    public Task BothListsScrollIndependentlyAndFollowWindowHeight() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoShotsTests.Root(); var d = VideoShots.Ensure(p); d.Attempted = true;
        d.Scenes = Enumerable.Range(1, 12).Select(i => new VideoScenePlan { Name = "Сцена " + i, Shots = Enumerable.Range(1, 4)
            .Select(j => new VideoShotPlan { Name = "План " + j, Start = "Начало действия", End = "Конец действия" }).ToList() }).ToList();
        d.SelectedSceneId = d.Scenes[0].Id; d.SelectedShotId = d.Scenes[0].Shots[0].Id;
        var control = Control(files, p); var window = new Window { Content = control, Width = 1200, Height = 900 }; window.Show();
        try
        {
            await Layout(); var steps = Find<ScrollViewer>(control, "VideoPrep.StepsScroll"); var list = Find<ScrollViewer>(control, "VideoPrep.Shots.ListScroll");
            var page = Find<ScrollViewer>(control, "VideoPrep.PageScroll");
            Assert.IsTrue(list.ScrollableHeight > 0); Assert.IsTrue(list.ViewportHeight < page.ViewportHeight);
            var initial = list.ViewportHeight; window.Height = 600; await Layout();
            Assert.IsTrue(steps.ScrollableHeight > 0); Assert.IsTrue(list.ViewportHeight < initial);
            var pageOffset = page.VerticalOffset;
            steps.ScrollToEnd(); list.ScrollToEnd(); await Layout();
            Assert.IsTrue(steps.VerticalOffset > 0); Assert.IsTrue(list.VerticalOffset > 0); Assert.AreEqual(pageOffset, page.VerticalOffset, .5);
            var lastId = control.Session.Project.Shots!.Scenes[^1].Id;
            Find<Button>(control, "VideoPrep.Shots.Scene." + lastId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Layout();
            Assert.IsTrue(Find<ScrollViewer>(control, "VideoPrep.Shots.ListScroll").VerticalOffset > 0, "Selecting a late scene must keep the list position.");
            Assert.IsTrue(Find<Button>(control, "VideoPrep.ShotsAdd").IsVisible);
        }
        finally { window.Close(); }
    });
    [TestMethod]
    public Task ReferenceNamesFollowDarkAndLightThemeResources() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoShotsTests.Root(); var d = VideoShots.Ensure(p); d.Attempted = true;
        var image = await VideoReferenceTests.Import(p, VideoReferenceTests.Image(files), files.File("managed"));
        VideoReferences.Bind(p, image.Image.Id, "", "general", "Reference notes");
        var scene = new VideoScenePlan { Name = "Мастерская", Shots = [new() { Name = "Ключ", Start = "Начало", End = "Конец", ReferenceIds = [image.Image.Id] }] };
        d.Scenes.Add(scene); d.SelectedSceneId = scene.Id; d.SelectedShotId = scene.Shots[0].Id;
        var control = Control(files, p); var window = new Window { Content = control, Width = 1200, Height = 900 }; window.Show();
        try
        {
            var names = Find<TextBlock>(control, "VideoPrep.Shots.ReferenceNames");
            foreach (var colour in new[] { Colors.WhiteSmoke, Colors.Black })
            { control.Resources["TextPrimaryBrush"] = new SolidColorBrush(colour); await Layout();
                Assert.AreEqual(colour, ((SolidColorBrush)names.Foreground).Color); }
            StringAssert.Contains(names.Text, "original.png");
        }
        finally { window.Close(); }
    });
    [TestMethod]
    public Task BusyFooterDescribesScenePreparationAndProgressUsesTheme() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, VideoShotsTests.Root());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (r, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return VideoShotsTests.Answer(r); } };
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>([new("core", "Qwen", new DebugModelInfo
        { Path = "test.gguf", Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")]), new(files.File("receipts")));
        control.Resources["TextPrimaryBrush"] = new SolidColorBrush(Colors.WhiteSmoke);
        var run = control.StartShotsAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Layout();
        var status = Find<TextBlock>(control, "VideoPrep.Status").Text;
        Assert.IsFalse(status.Contains("улучшает замысел")); StringAssert.Contains(status, "сцен");
        Assert.AreEqual(Colors.WhiteSmoke, ((SolidColorBrush)Find<TextBlock>(control, "VideoPrep.Shots.Completed").Foreground).Color);
        Find<Button>(control, "VideoPrep.ShotsCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await run;
    });
    [TestMethod]
    public void ModelCannotReplaceReadableSceneTitleWithItsServiceId()
    {
        var p = VideoShotsTests.Root(); var scene = new VideoScenePlan { Name = "Раскрытие мастерской", Intent = "Показать пространство" };
        var r = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, "layout.lopataAIVid", Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(scene),
            p.Form, "core", "test.gguf", 1, VideoShotsProtocol.Version, EngineSetSelection.ActiveId, VideoShotsProtocol.Scene, ContextText: VideoShots.Source(p), ResponseLanguage: "ru");
        var answer = JsonNode.Parse(VideoShotsTests.Answer(r).Text)!; answer["name"] = scene.Id;
        Assert.AreEqual(scene.Name, VideoShotsProtocol.ReadScene(answer.ToJsonString(), r).Name);
    }
    private static async Task Layout() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(35));
}
