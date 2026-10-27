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
public sealed class VideoWorldUiTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static VideoConceptAnswer LongAnswer()
    {
        var answer = VideoWorldTests.Answer(); var json = System.Text.Json.Nodes.JsonNode.Parse(answer.Text)!;
        json["cards"]![0]!["description"] = string.Join(" ", Enumerable.Repeat("Long visual description with soft colours and detailed textures.", 70));
        return answer with { Text = json.ToJsonString() };
    }
    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, VideoConceptTests.FakeAssistant assistant, string language = "ru")
    {
        var p = VideoWorldTests.Root(); var source = files.File("initial.lopataAIVid"); VideoPreparationArchive.Save(p, source);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(source);
        var l = new LocalizationService(); l.Load(language); var control = new VideoPreparationControl(l.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>([new("core", "Qwen", new DebugModelInfo
        { Path = "test.gguf", Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")]), new(files.File("receipts")));
        return control;
    }
    [TestMethod]
    public Task ManualWorldEnglishBypassSpellingAndNavigationPreserveUpstream() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant(); var control = Create(files, assistant);
        var p = control.Session.Project; var original = p.IdeaText; Assert.IsFalse(control.Next());
        Find<TextBox>(control, "VideoPrep.WorldName").Text = "Workshop";
        var description = Find<TextBox>(control, "VideoPrep.WorldDescription"); Assert.IsTrue(SpellCheck.GetIsEnabled(description));
        description.Text = "Warm watercolour animation with soft window light. The filmmaker says «Снято!» in Russian.";
        Assert.IsTrue(Find<Button>(control, "VideoPrep.WorldUndo").IsEnabled);
        Find<Button>(control, "VideoPrep.WorldUndo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("", Find<TextBox>(control, "VideoPrep.WorldDescription").Text);
        Find<Button>(control, "VideoPrep.WorldRedo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        description = Find<TextBox>(control, "VideoPrep.WorldDescription"); Assert.IsTrue(description.Text.Contains("watercolour"));
        Find<TextBox>(control, "VideoPrep.WorldFixed").Text = "Round glasses remain unchanged.";
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("world_translation", p.CurrentStep); Assert.AreEqual(0, assistant.Calls);
        var english = Find<TextBox>(control, "VideoPrep.WorldEnglish"); Assert.IsTrue(SpellCheck.GetIsEnabled(english));
        english.Text += " Manual wording."; Assert.IsTrue(control.Next()); Assert.AreEqual("references", p.CurrentStep);
        Assert.IsTrue(control.BackStepIfNeeded()); StringAssert.EndsWith(Find<TextBox>(control, "VideoPrep.WorldEnglish").Text, "Manual wording.");
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("Workshop", Find<TextBox>(control, "VideoPrep.WorldName").Text);
        Assert.AreEqual(original, p.IdeaText);
    });
    [TestMethod]
    public Task PopupHasHorizontalSlotsAndEarlyChoiceCancelsRunningSecondLevel() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var calls = new List<int>();
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (r, token) =>
        { calls.Add(r.Freedom); if (r.Freedom == 2) { enteredSecond.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            await Task.Delay(30, token); return LongAnswer(); } };
        var control = Create(files, assistant); var owner = new Window { Content = control, Width = 1100, Height = 900 };
        owner.Show();
        var checkedPopup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                VideoWorldOptionsWindow? popup = null;
                for (var i = 0; i < 100 && popup is null; i++) { popup = owner.OwnedWindows.OfType<VideoWorldOptionsWindow>().FirstOrDefault(); await Task.Delay(10); }
                Assert.IsNotNull(popup); popup.Width = 620; popup.UpdateLayout();
                var slots = Enumerable.Range(1, 3).Select(n => Find<Border>(popup, "VideoPrep.WorldOption." + n)).ToArray();
                Assert.AreEqual(slots[0].TranslatePoint(new(), popup).Y, slots[2].TranslatePoint(new(), popup).Y, .5);
                Assert.IsTrue(slots[2].TranslatePoint(new(), popup).X > slots[1].TranslatePoint(new(), popup).X);
                Assert.AreEqual(slots[0].ActualWidth, slots[2].ActualWidth, .5);
                Assert.IsTrue(slots[0].ActualWidth < 400, "A horizontal viewport must constrain card width even when text is long.");
                Find<Button>(popup, "VideoPrep.WorldStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(8));
                popup.UpdateLayout();
                Assert.IsTrue(slots[0].ActualWidth < 400, "A ready reply must not expand its column.");
                Find<Button>(popup, "VideoPrep.WorldChoose.1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                checkedPopup.SetResult();
            }
            catch (Exception e) { foreach (Window w in owner.OwnedWindows) w.Close(); checkedPopup.SetException(e); }
        });
        Find<Button>(control, "VideoPrep.WorldPropose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await checkedPopup.Task.WaitAsync(TimeSpan.FromSeconds(12));
        for (var i = 0; i < 100 && control.Session.Project.World!.Options.Any(o => o.Status == "running"); i++) await Task.Delay(10);
        CollectionAssert.AreEqual(new[] { 1, 2 }, calls.ToArray());
        Assert.HasCount(2, control.Session.Project.World!.Cards); Assert.AreEqual("canceled", control.Session.Project.World.Options[1].Status);
        Assert.AreEqual("ready", control.Session.Project.World.Options[0].Status); owner.Close();
    });
    [TestMethod]
    public Task PopupCloseCancelsWorkWithoutChangingAcceptedWorld() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return VideoWorldTests.Answer(); } };
        var control = Create(files, assistant, "en"); var owner = new Window { Content = control, Width = 1000, Height = 800 }; owner.Show();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                VideoWorldOptionsWindow? popup = null;
                for (var i = 0; i < 100 && popup is null; i++) { popup = owner.OwnedWindows.OfType<VideoWorldOptionsWindow>().FirstOrDefault(); await Task.Delay(10); }
                Assert.IsNotNull(popup); Find<Button>(popup, "VideoPrep.WorldStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); popup.Close(); finished.SetResult();
            }
            catch (Exception e) { foreach (Window w in owner.OwnedWindows) w.Close(); finished.SetException(e); }
        });
        Find<Button>(control, "VideoPrep.WorldPropose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await finished.Task;
        await Task.Delay(100); Assert.HasCount(1, control.Session.Project.World!.Cards); Assert.AreEqual("", control.Session.Project.World.Cards[0].Text.IdeaText); owner.Close();
    });
    // Native Windows spell checking retains STA callbacks. Keep one dispatcher alive for the test process,
    // rather than destroying its thread while those callbacks are still owned by Windows.
    private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action) => UiDispatcher.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(35));
}
