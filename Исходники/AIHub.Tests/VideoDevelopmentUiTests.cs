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
public sealed class VideoDevelopmentUiTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, VideoConceptTests.FakeAssistant assistant,
        string form = "story", string language = "ru")
    {
        var session = new VideoPreparationSession(files.File("recovery")); var p = session.Project;
        p.Form = form; p.IdeaText = "The cat prepares tea. Then a filmmaker waves and says «Снято!».";
        VideoTranslationHistory.UseOriginal(p); p.CurrentStep = "development"; p.LastAssistantModelId = "core";
        VideoDevelopment.Ensure(p); session.Changed(); session.Save(files.File("project.lopataAIVid"));
        var l = new LocalizationService(); l.Load(language); var control = new VideoPreparationControl(l.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new DebugModelInfo { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()),
            new(files.File("receipts")));
        return control;
    }

    [TestMethod]
    [DataRow("story", "ru", 1100d, true)] [DataRow("imagery", "en", 620d, false)]
    public Task EmptyPlanAiTranslationManualEditAndNavigationPreserveTheConcept(string form, string language, double width, bool dark) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var requests = new List<VideoConceptRequest>();
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) =>
        { requests.Add(r); return Task.FromResult(VideoDevelopmentTests.Answer(VideoTranslationProtocol.IsTranslation(r)
            ? "First, the cat prepares tea. Then the filmmaker waves and says «Снято!»." : "Сначала кот готовит чай. Затем появляется оператор. В финале он говорит «Снято!».", VideoTranslationProtocol.IsTranslation(r))); } };
        var control = Create(files, assistant, form, language); var root = control.Session.Project; var source = root.IdeaText; var working = root.EnglishText;
        Assert.IsFalse(control.Next());
        Assert.IsTrue(SpellCheck.GetIsEnabled(Find<TextBox>(control, "VideoPrep.Development")));
        Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).OfType<ExampleTextInput>()
            .Single(e => AutomationProperties.GetAutomationId(e.Input) == "VideoPrep.Development").ExampleVisible);
        await control.ImproveConceptAsync();
        Assert.AreEqual(VideoDevelopment.Purpose, requests.Single().Purpose); Assert.AreEqual("", requests[0].Text);
        Assert.AreEqual("core", requests[0].ModelId); Assert.AreEqual(working, requests[0].ContextText);
        Assert.IsTrue(control.MoveConcept(-1)); Assert.AreEqual("", root.Development!.IdeaText);
        Assert.IsTrue(control.MoveConcept(1)); Assert.IsTrue(await control.NextAsync());
        Assert.AreEqual("development_translation", root.CurrentStep); Assert.AreEqual(2, requests.Count);
        Assert.AreEqual(VideoDevelopment.TranslationPurpose, requests.Last().Purpose);
        var english = Find<TextBox>(control, "VideoPrep.DevelopmentEnglish"); Assert.AreEqual("en-us", english.Language.IetfLanguageTag.ToLowerInvariant());
        english.Text += " Manual edit."; Assert.IsTrue(control.Checkpoint());
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual("world", root.CurrentStep);
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("development_translation", root.CurrentStep);
        StringAssert.EndsWith(Find<TextBox>(control, "VideoPrep.DevelopmentEnglish").Text, "Manual edit.");
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("development", root.CurrentStep);
        var host = ScenarioNavigationTests.Create(language, 1, dark); host.Content = control;
        host.Measure(new Size(width, 800)); host.Arrange(new Rect(0, 0, width, 800)); host.UpdateLayout();
        Assert.IsTrue(Find<TextBox>(control, "VideoPrep.Development").ActualWidth > 100);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>().Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
        var loaded = VideoPreparationArchive.Load(control.Session.ArchivePath!, files.File("opened"));
        Assert.AreEqual(source, loaded.IdeaText); Assert.AreEqual(working, loaded.EnglishText); Assert.HasCount(2, loaded.Development!.TranslationHistory);
        Assert.AreEqual(2, requests.Count);
    });

    [TestMethod]
    public Task DevelopmentTranslationShowsLoadingUntilTheReplyIsComplete() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (_, _) =>
        { entered.SetResult(); await release.Task; return VideoDevelopmentTests.Answer("First, the cat makes tea.", true); } };
        var control = Create(files, assistant);
        Find<TextBox>(control, "VideoPrep.Development").Text = "Сначала кот готовит чай. Затем оператор машет рукой.";
        var work = control.NextAsync(); await entered.Task;
        Assert.IsTrue(Find<System.Windows.Controls.ProgressBar>(control, "VideoPrep.TranslationLoading").IsIndeterminate);
        Assert.IsFalse(Find<Button>(control, "VideoPrep.Next").IsEnabled);
        Assert.AreEqual("", Find<TextBox>(control, "VideoPrep.DevelopmentEnglish").Text);
        release.SetResult(); Assert.IsTrue(await work);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<System.Windows.Controls.ProgressBar>().Any());
        Assert.AreEqual("First, the cat makes tea.", Find<TextBox>(control, "VideoPrep.DevelopmentEnglish").Text);
    });

    [TestMethod]
    public Task ManualEnglishDevelopmentNeedsNoAiAndKeepsQuotedRussian() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var control = Create(files, assistant);
        var text = "First, the cat makes tea. Then the filmmaker waves and says «Снято!» (по-русски).";
        Find<TextBox>(control, "VideoPrep.Development").Text = text;
        Assert.IsTrue(await control.NextAsync()); Assert.AreEqual(0, assistant.Calls);
        Assert.AreEqual(text, Find<TextBox>(control, "VideoPrep.DevelopmentEnglish").Text);
        Assert.AreEqual("original", control.Session.Project.Development!.TranslationHistory.Single().Source);
    });

    [TestMethod]
    public Task ChangedWorkingConceptRequiresExplicitReviewWithoutDiscardingPlan() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Create(files, new()); var root = control.Session.Project;
        Find<TextBox>(control, "VideoPrep.Development").Text = "Мой план, который нужно сохранить.";
        root.EnglishText += " A new ending."; control.Session.Changed(); Assert.IsTrue(control.Checkpoint());
        var l = new LocalizationService(); l.Load("ru"); control.Configure(l.T);
        Assert.IsFalse(control.Next()); Assert.AreEqual("Мой план, который нужно сохранить.", root.Development!.IdeaText);
        var owner = new Window { Content = control, Width = 1000, Height = 800 }; owner.Show();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            var dialog = owner.OwnedWindows.OfType<VideoPreparationDecisionWindow>().Single();
            Find<Button>(dialog, "VideoPrep.Decision.continue").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.IsTrue(await control.NextAsync()); Assert.IsTrue(VideoDevelopment.SourceCurrent(root)); owner.Close();
        Assert.AreEqual("development_translation", root.CurrentStep);
    });

    [TestMethod]
    public Task CancelingAnEmptyPlanGenerationPreservesItsInitialVersion() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception("unreachable"); } };
        var control = Create(files, assistant); var root = control.Session.Project; var original = root.IdeaText;
        var task = control.ImproveConceptAsync(); await entered.Task;
        Find<Button>(control, "VideoPrep.AiCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await task;
        Assert.AreEqual(original, root.IdeaText); Assert.AreEqual("", root.Development!.IdeaText); Assert.HasCount(1, root.Development.IdeaHistory);
    });

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public Task SavedReceiptRestoresTheCorrectMaterialWithoutCallingAModel(bool translating) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant(); var control = Create(files, assistant);
        var root = control.Session.Project;
        if (translating) root.Development!.IdeaText = "Сначала кот готовит чай. Затем оператор машет рукой.";
        control.Session.Changed(); Assert.IsTrue(control.Checkpoint());
        var request = VideoDevelopmentTests.Request(root, translating) with { ProjectPath = control.Session.ArchivePath! };
        var answer = VideoDevelopmentTests.Answer(translating ? "First, the cat prepares tea." : "Сначала кот готовит чай.", translating);
        new VideoConceptReceiptStore(files.File("receipts")).Save(request, answer);
        var state = new BackgroundOperationState { Id = request.Id, Kind = VideoConceptWork.KindFor(request), Title = "Development restore test", Project = request.ProjectPath,
            Input = System.Text.Json.JsonSerializer.SerializeToElement(request) };
        await control.ResumeConceptAsync(state, default);
        Assert.AreEqual(0, assistant.Calls); Assert.AreEqual(VideoDevelopment.StepFor(request), root.CurrentStep);
        Assert.AreEqual(answer.Text, translating ? root.Development!.EnglishText : root.Development!.IdeaText);
        Assert.AreNotEqual(answer.Text, root.IdeaText);
    });

    [TestMethod]
    public Task LateAnswerCannotOverwriteAnotherMaterial() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistant = new VideoConceptTests.FakeAssistant { Run = async (_, _) => { entered.SetResult(); await release.Task; return VideoDevelopmentTests.Answer(); } };
        var control = Create(files, assistant); var root = control.Session.Project; var original = root.IdeaText;
        var task = control.ImproveConceptAsync(); await entered.Task;
        Find<TextBox>(control, "VideoPrep.Development").Text = "Сохранить мою свежую правку.";
        Assert.IsTrue(control.BackStepIfNeeded());
        Find<TextBox>(control, "VideoPrep.English").Text += " Another ending.";
        release.SetResult(); await task;
        Assert.AreEqual(original, root.IdeaText); StringAssert.EndsWith(root.EnglishText, "Another ending.");
        Assert.AreEqual("Сохранить мою свежую правку.", root.Development!.IdeaText);
        Assert.IsFalse(VideoDevelopment.SourceCurrent(root)); Assert.IsTrue(root.Development.IdeaHistory.Any(v => v.Source == "ai"));
    });

    private static async Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = dispatcher.InvokeAsync(action).Task.Unwrap();
            _ = task.ContinueWith(t =>
            { if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions); else if (t.IsCanceled) done.TrySetCanceled(); else done.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }, TaskScheduler.Default);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
