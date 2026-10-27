using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoTranslationUiTests
{
    private static T Find<T>(DependencyObject control, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(control)
        .OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, VideoConceptTests.FakeAssistant assistant,
        string language, string modelId = "")
    {
        var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Form = "story"; session.Project.CurrentStep = "idea"; session.Project.IdeaText = "Город в тумане. Лопата и LOPATA.";
        session.Project.AssistantModelId = modelId; session.Changed(); session.Save(files.File("project.lopataAIVid"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new VideoPreparationControl(localizer.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new DebugModelInfo { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()), new(files.File("receipts")));
        return control;
    }

    [TestMethod]
    [DataRow("ru", "", "executor", 1100d, true)]
    [DataRow("en", "core", "core", 620d, false)]
    [DataRow("ru", "profile-katarau", "profile-katarau", 1100d, true)]
    public Task NextStartsTranslationShowsLoadingAndInheritsModel(string language, string chosen, string expected, double width, bool dark) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoConceptRequest? request = null;
        assistant.Run = async (r, _) => { request = r; entered.SetResult(); await release.Task; return new("A town in fog. Лопата and LOPATA.", "", "", VideoConceptTests.Model); };
        var control = Create(files, assistant, language, chosen);
        Assert.IsTrue(SpellCheck.GetIsEnabled(Find<TextBox>(control, "VideoPrep.Idea")));
        Assert.AreEqual(language == "en" ? "en-us" : "ru-ru", Find<TextBox>(control, "VideoPrep.Idea").Language.IetfLanguageTag.ToLowerInvariant());
        var work = control.NextAsync(); await entered.Task;
        Assert.AreEqual("translation", control.Session.Project.CurrentStep); Assert.AreEqual(expected, request!.ModelId);
        Assert.AreEqual(VideoTranslationProtocol.Purpose, request.Purpose);
        Assert.IsTrue(Find<ProgressBar>(control, "VideoPrep.TranslationLoading").IsIndeterminate);
        Assert.IsFalse(Find<Button>(control, "VideoPrep.Next").IsEnabled);
        var english = Find<TextBox>(control, "VideoPrep.English"); Assert.IsTrue(SpellCheck.GetIsEnabled(english));
        Assert.AreEqual("en-us", english.Language.IetfLanguageTag.ToLowerInvariant());
        release.SetResult(); Assert.IsTrue(await work); Assert.AreEqual(1, assistant.Calls);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<ProgressBar>().Any());
        Find<TextBox>(control, "VideoPrep.English").Text = "Manual English edit with Лопата.";
        Assert.IsTrue(control.Checkpoint()); Assert.IsTrue(await control.NextAsync());
        Assert.AreEqual("development", control.Session.Project.CurrentStep); Assert.AreEqual(1, assistant.Calls);
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("translation", control.Session.Project.CurrentStep);
        Assert.AreEqual("Manual English edit with Лопата.", Find<TextBox>(control, "VideoPrep.English").Text);
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.IsTrue(await control.NextAsync()); Assert.AreEqual(1, assistant.Calls);
        var host = ScenarioNavigationTests.Create(language, 1, dark); host.Content = control;
        host.Measure(new Size(width, 800)); host.Arrange(new Rect(0, 0, width, 800)); host.UpdateLayout();
        Assert.IsTrue(Find<TextBox>(control, "VideoPrep.English").ActualWidth > 100);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>().Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
        var loaded = VideoPreparationArchive.Load(control.Session.ArchivePath!, files.File("opened"));
        Assert.AreEqual("Manual English edit with Лопата.", loaded.EnglishText); Assert.HasCount(2, loaded.TranslationHistory);
    });

    [TestMethod]
    public Task CancelTranslationKeepsSourceAndManualEditsAndAllowsRetry() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        assistant.Run = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("unreachable"); };
        var control = Create(files, assistant, "ru", "core"); var source = control.Session.Project.IdeaText;
        var work = control.NextAsync(); await entered.Task;
        Find<TextBox>(control, "VideoPrep.English").Text = "Keep my draft";
        Find<Button>(control, "VideoPrep.TranslationCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await work;
        Assert.AreEqual(source, control.Session.Project.IdeaText); Assert.AreEqual("Keep my draft", control.Session.Project.EnglishText);
        Assert.IsTrue(Find<Button>(control, "VideoPrep.Translate").IsEnabled);
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("development", control.Session.Project.CurrentStep);
    });

    [TestMethod]
    public Task InheritedTranslatorFollowsConceptChoiceAndExplicitTranslatorRemainsSelected() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var requests = new List<VideoConceptRequest>();
        var assistant = new VideoConceptTests.FakeAssistant
        {
            Run = (r, _) =>
        { requests.Add(r); return Task.FromResult(new VideoConceptAnswer("Translated " + requests.Count, "", "", VideoConceptTests.Model)); }
        };
        var control = Create(files, assistant, "ru");
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("executor", requests.Single().ModelId);
        Assert.IsTrue(control.BackStepIfNeeded());
        Find<TextBox>(control, "VideoPrep.Idea").Text = "Новый замысел"; control.Session.Project.AssistantModelId = "core";
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("core", requests.Last().ModelId);
        Assert.AreEqual("Translated 1", control.Session.Project.EnglishText); Assert.IsFalse(control.Next());
        Assert.IsTrue(control.MoveTranslation(1)); Assert.AreEqual("Translated 2", control.Session.Project.EnglishText);
        Assert.IsTrue(control.BackStepIfNeeded());
        control.Session.Project.TranslationModelId = "profile-katarau"; control.Session.Project.TranslationModelExplicit = true;
        Find<TextBox>(control, "VideoPrep.Idea").Text = "Третий замысел";
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("profile-katarau", requests.Last().ModelId);
    });

    [TestMethod]
    public Task AutomaticEnglishDetectionPreservesQuotedRussianAndBypassesModelWithoutToggle() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var control = Create(files, assistant, "ru");
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).Any(c => AutomationProperties.GetAutomationId(c) == "VideoPrep.AlreadyEnglish"));
        control.Session.Project.IdeaText = "The traveller walks through an abandoned city. He says «Кто здесь?» (по-русски).";
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual(0, assistant.Calls);
        Assert.AreEqual(control.Session.Project.IdeaText, control.Session.Project.EnglishText);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).Any(c => AutomationProperties.GetAutomationId(c) == "VideoPrep.UseEnglishOriginal"));
        var l = new LocalizationService(); l.Load("ru");
        var choices = VideoConceptModels.Ids.Select(id => new VideoConceptChoice(id, VideoConceptModels.Name(id),
            new DebugModelInfo { Path = files.File(id), IsRunnable = true }, true, "AiGpuEstimate")).ToArray();
        var dialog = new VideoConceptOptionsWindow(null, l.T, choices, "core", 1, translation: true);
        Assert.AreEqual("core", dialog.SelectedModelId);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(dialog).OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "VideoPrep.AiFreedom"));
        _ = dialog.Dispatcher.InvokeAsync(() => Find<Button>(dialog, "VideoPrep.AiDialogCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), DispatcherPriority.Loaded);
        Assert.AreEqual(false, dialog.ShowDialog());
    });

    private static async Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = dispatcher.InvokeAsync(action).Task.Unwrap();
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions); else if (t.IsCanceled) done.TrySetCanceled(); else done.TrySetResult();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }, TaskScheduler.Default);
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
