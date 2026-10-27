using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoConceptUiTests
{
    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, VideoConceptTests.FakeAssistant assistant, string language = "ru")
    {
        var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Form = "story"; session.Project.CurrentStep = "idea"; session.Project.IdeaText = "Исходный замысел";
        session.Changed(); session.Save(files.File("concept.lopataAIVid"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new VideoPreparationControl(localizer.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new DebugModelInfo { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()),
            new VideoConceptReceiptStore(files.File("receipts")));
        return control;
    }
    private static T Find<T>(DependencyObject control, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(control)
        .OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    [TestMethod]
    [DataRow("ru")] [DataRow("en")]
    public Task OptionsAreInSeparateOwnedDialogAndCancelKeepsProjectChoices(string language) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var project = new VideoPreparationProject { Form = "story", IdeaText = "Не менять", AssistantModelId = "executor" };
        var choices = VideoConceptModels.Ids.Select(id => new VideoConceptChoice(id, VideoConceptModels.Name(id),
            new DebugModelInfo { Path = files.File(id + ".gguf"), IsRunnable = true }, true, "AiGpuEstimate")).ToArray();
        var localizer = new LocalizationService(); localizer.Load(language);
        var dialog = new VideoConceptOptionsWindow(null, localizer.T, choices, project.AssistantModelId, 1);
        Assert.AreEqual(3, Find<ComboBox>(dialog, "VideoPrep.AiModel").Items.Count);
        Assert.AreEqual(3, Find<ComboBox>(dialog, "VideoPrep.AiFreedom").Items.Count);
        Find<ComboBox>(dialog, "VideoPrep.AiModel").SelectedValue = "core";
        Find<ComboBox>(dialog, "VideoPrep.AiFreedom").SelectedIndex = 2;
        Assert.AreEqual("core", dialog.SelectedModelId); Assert.AreEqual(3, dialog.Freedom);
        Assert.AreEqual("executor", project.AssistantModelId); Assert.AreEqual("Не менять", project.IdeaText);
        _ = dialog.Dispatcher.InvokeAsync(() => Find<Button>(dialog, "VideoPrep.AiDialogCancel")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Loaded);
        Assert.AreEqual(false, dialog.ShowDialog());
        Assert.IsFalse(dialog.IsVisible); Assert.AreEqual("executor", project.AssistantModelId);
        await Task.CompletedTask;
    });

    [TestMethod]
    [DataRow("ru", 1100d, true)] [DataRow("en", 620d, false)]
    public Task OneCanvasReplacementArrowsAndExplicitModelChoice(string language, double width, bool dark) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var requests = new List<VideoConceptRequest>(); assistant.Run = (r, _) => { requests.Add(r); return Task.FromResult(new VideoConceptAnswer("Вариант ИИ", "Уточнено", "", VideoConceptTests.Model)); };
        var control = Create(files, assistant, language); var host = ScenarioNavigationTests.Create(language, 1, dark); host.Content = control;
        Assert.IsFalse(Find<Button>(control, "VideoPrep.VersionBack").IsEnabled);
        await control.ImproveConceptAsync();
        Assert.AreEqual("executor", requests.Single().ModelId);
        Assert.AreEqual("Вариант ИИ", Find<System.Windows.Controls.TextBox>(control, "VideoPrep.Idea").Text);
        Assert.IsTrue(Find<Button>(control, "VideoPrep.VersionBack").IsEnabled);
        Assert.IsFalse(Find<Button>(control, "VideoPrep.VersionForward").IsEnabled);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<ComboBox>().Any());
        Assert.IsTrue(control.MoveConcept(-1)); Assert.AreEqual("Исходный замысел", control.Session.Project.IdeaText);
        Assert.IsTrue(Find<Button>(control, "VideoPrep.VersionForward").IsEnabled);
        Assert.IsTrue(control.MoveConcept(1));
        control.Session.Project.AssistantModelId = "core";
        control.Session.Project.ImprovementFreedom = 3; control.Session.Changed();
        await control.ImproveConceptAsync();
        Assert.AreEqual("core", requests.Last().ModelId); Assert.AreEqual(3, requests.Last().Freedom);
        host.Measure(new Size(width, 800)); host.Arrange(new Rect(0, 0, width, 800)); host.UpdateLayout();
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>().Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
        var saved = VideoPreparationArchive.Load(control.Session.ArchivePath!, files.File("opened"));
        Assert.AreEqual("core", saved.AssistantModelId); Assert.AreEqual(3, saved.ImprovementFreedom); Assert.HasCount(3, saved.IdeaHistory);
    });

    [TestMethod]
    public Task EditsDuringGenerationStayInFieldAndLateAnswerIsAvailableForward() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        assistant.Run = async (_, _) => { entered.SetResult(); await release.Task; return new("Поздний ответ", "", "", VideoConceptTests.Model); };
        var control = Create(files, assistant); var task = control.ImproveConceptAsync(); await entered.Task;
        Find<System.Windows.Controls.TextBox>(control, "VideoPrep.Idea").Text = "Не затирать свежую правку";
        Assert.IsTrue(control.Checkpoint()); Assert.IsFalse(control.Next());
        await control.ImproveConceptAsync(); Assert.AreEqual(1, assistant.Calls);
        release.SetResult(); await task;
        Assert.AreEqual("Не затирать свежую правку", control.Session.Project.IdeaText);
        Assert.IsTrue(control.MoveConcept(1)); Assert.AreEqual("Поздний ответ", control.Session.Project.IdeaText);
    });

    [TestMethod]
    public Task CancelAndInvalidReplyDoNotReplaceOriginalOrCreateAiVersions() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        assistant.Run = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("unreachable"); };
        var control = Create(files, assistant); var task = control.ImproveConceptAsync(); await entered.Task;
        Find<Button>(control, "VideoPrep.AiCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await task;
        Assert.AreEqual("Исходный замысел", control.Session.Project.IdeaText); Assert.HasCount(1, control.Session.Project.IdeaHistory);
        assistant.Run = (_, _) => throw new InvalidDataException("VideoPrep.AiInvalidReply");
        await control.ImproveConceptAsync();
        Assert.AreEqual("Исходный замысел", control.Session.Project.IdeaText); Assert.HasCount(1, control.Session.Project.IdeaHistory);
        Assert.IsTrue(Find<Button>(control, "VideoPrep.AiToggle").IsEnabled);
    });

    [TestMethod]
    public Task SavingWhilePausedKeepsTheOperationStatusVisible() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json")));
        ApplicationBackgroundOperations.Current = controller;
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assistant = new VideoConceptTests.FakeAssistant { Run = async (_, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("unreachable"); } };
            var control = Create(files, assistant);
            var work = control.ImproveConceptAsync(); await entered.Task;
            await controller.PauseAsync(); control.RefreshConceptOperationState();
            Find<System.Windows.Controls.TextBox>(control, "VideoPrep.Idea").Text = "Правка на паузе";
            Assert.IsTrue(control.Checkpoint());
            StringAssert.Contains(Find<TextBlock>(control, "VideoPrep.Status").Text, "приостановлено");
            Find<Button>(control, "VideoPrep.AiCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await work;
            Assert.AreEqual("Правка на паузе", control.Session.Project.IdeaText);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    private static async Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = dispatcher.InvokeAsync(action).Task.Unwrap();
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
                else if (t.IsCanceled) done.TrySetCanceled(); else done.TrySetResult();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }, TaskScheduler.Default);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
