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
public sealed class VideoReferencePlanUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    { var end = DateTime.UtcNow.AddSeconds(10); while (!condition()) { if (DateTime.UtcNow > end) Assert.Fail("Operation did not complete."); await Task.Delay(15); } }
    private static VideoPreparationControl Create(VideoPreparationTests.TestFiles files, string language,
        VideoConceptTests.FakeAssistant assistant)
    {
        var p = VideoReferencePlanTests.Root(); p.LastAssistantModelId = "profile-katarau";
        VideoPreparationArchive.Save(p, files.File("project.lopataAIVid"));
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(files.File("project.lopataAIVid"));
        var l = new LocalizationService(); l.Load(language); var control = new VideoPreparationControl(l.T, session);
        control.ConfigureConceptAssistant(assistant, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new DebugModelInfo { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()), new(files.File("receipts")));
        return control;
    }
    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task ManualSetEditingHistoryAndSaveWorkWithoutAnyModelCall(string language) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant(); var control = Create(files, language, assistant);
        Click(control, "VideoPrep.RefPlanMode.manual"); Click(control, "VideoPrep.RefPlanAdd");
        var item = control.Session.Project.References!.Plan!.Items.Single();
        Find<TextBox>(control, "VideoPrep.RefPlanName." + item.Id).Text = "Cat reference";
        Find<TextBox>(control, "VideoPrep.RefPlanBrief." + item.Id).Text = "Keep round glasses.";
        Click(control, "VideoPrep.RefPlanPrepare");
        var native = Find<TextBox>(control, "VideoPrep.RefPlanNative"); var english = Find<TextBox>(control, "VideoPrep.RefPlanEnglish");
        Assert.IsTrue(SpellCheck.GetIsEnabled(native)); Assert.IsTrue(SpellCheck.GetIsEnabled(english));
        Assert.AreEqual("en-us", english.Language.IetfLanguageTag.ToLowerInvariant());
        Assert.AreEqual(language == "ru" ? "ru-ru" : "en-us", native.Language.IetfLanguageTag.ToLowerInvariant());
        native.Text = "Кот в очках."; english.Text = "Cat wearing glasses."; Assert.IsTrue(control.Checkpoint());
        english.Text += " Green apron."; Assert.IsTrue(control.Save());
        Click(control, "VideoPrep.RefPlanBackToList"); Click(control, "VideoPrep.RefPlanPrepare");
        Click(control, "VideoPrep.RefPlanPrevious"); Assert.AreEqual("Cat wearing glasses.", Find<TextBox>(control, "VideoPrep.RefPlanEnglish").Text);
        Click(control, "VideoPrep.RefPlanNext"); StringAssert.EndsWith(Find<TextBox>(control, "VideoPrep.RefPlanEnglish").Text, "Green apron.");
        Click(control, "VideoPrep.RefPlanFinish"); Assert.AreEqual("ready", control.Session.Project.References.Plan.Page);
        Assert.AreEqual(0, assistant.Calls);
        var opened = VideoPreparationArchive.Load(control.Session.ArchivePath!, files.File("opened")); Assert.AreEqual("ready", opened.References!.Plan!.Page);
        LiterarySpellChecking.ReleaseTree(control); await Task.CompletedTask;
    });
    [TestMethod]
    public Task AssistedDiscoveryAndPromptGenerationInheritChosenModelAndUseSeparateRequests() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var requests = new List<VideoConceptRequest>();
        VideoPreparationControl? control = null;
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) =>
        { requests.Add(r); return Task.FromResult(r.Purpose == VideoReferencePromptProtocol.ListPurpose ? VideoReferencePlanTests.ListAnswer(control!.Session.Project) : VideoReferencePlanTests.PromptAnswer()); } };
        control = Create(files, "ru", assistant);
        Click(control, "VideoPrep.RefPlanDiscover");
        await Until(() => control.Session.Project.References!.Plan!.Items.Count == 1);
        await Until(() => Find<Button>(control, "VideoPrep.RefPlanPrepare").IsEnabled);
        var plan = control.Session.Project.References!.Plan!; Assert.AreEqual("profile-katarau", requests.Single().ModelId);
        Click(control, "VideoPrep.RefPlanPrepare"); await Until(() => plan.Items[0].History.Count == 1);
        await Until(() => !Find<TextBox>(control, "VideoPrep.RefPlanEnglish").IsReadOnly);
        Assert.AreEqual(2, requests.Count); Assert.AreEqual(VideoReferencePromptProtocol.PromptPurpose, requests[1].Purpose);
        Assert.AreNotEqual(requests[0].Id, requests[1].Id); Assert.IsTrue(VideoReferencePlans.Ready(control.Session.Project));
        Assert.AreEqual("Рыжий кот в круглых очках.", Find<TextBox>(control, "VideoPrep.RefPlanNative").Text);
        Find<TextBox>(control, "VideoPrep.RefPlanEnglish").Text += " Manual edit.";
        Assert.IsTrue(control.Save());
        Click(control, "VideoPrep.RefPlanBackToList"); Click(control, "VideoPrep.RefPlanPrepare");
        await Task.Delay(30); Assert.AreEqual(2, requests.Count); // Already prepared and manually reviewed material is reused.
        StringAssert.EndsWith(Find<TextBox>(control, "VideoPrep.RefPlanEnglish").Text, "Manual edit.");
        LiterarySpellChecking.ReleaseTree(control);
    });
    [TestMethod]
    public Task ManualImprovementDialogDefaultsToStrongestAvailableModelAndCancelDoesNotGenerate() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var assistant = new VideoConceptTests.FakeAssistant();
        var control = Create(files, "ru", assistant); control.Session.Project.LastAssistantModelId = "";
        Click(control, "VideoPrep.RefPlanMode.manual"); Click(control, "VideoPrep.RefPlanAdd");
        var item = control.Session.Project.References!.Plan!.Items.Single();
        Find<TextBox>(control, "VideoPrep.RefPlanName." + item.Id).Text = "Cat";
        Click(control, "VideoPrep.RefPlanPrepare");
        var owner = new Window { Content = control }; owner.Show(); var selected = "";
        _ = owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = owner.OwnedWindows.OfType<VideoConceptOptionsWindow>().Single();
            selected = dialog.SelectedModelId; dialog.DialogResult = false;
        }, DispatcherPriority.Background);
        Click(control, "VideoPrep.RefPlanImprove");
        await Until(() => selected != ""); Assert.AreEqual("executor", selected); Assert.AreEqual(0, assistant.Calls);
        owner.Close(); LiterarySpellChecking.ReleaseTree(control);
    });
    [TestMethod]
    public Task ContextFailureStopsAnimationAndUnlocksTheFormWithoutRetrying() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var assistant = new VideoConceptTests.FakeAssistant { Run = (_, _) => {
                VideoConceptAssistant.OutputTokens(8192, 8192); return Task.FromResult(VideoReferencePlanTests.PromptAnswer()); } };
            var control = Create(files, "ru", assistant); var original = control.Session.Project.IdeaText;
            Click(control, "VideoPrep.RefPlanDiscover");
            await Until(() => controller.State?.Phase == BackgroundOperationPhase.Failed && Find<Button>(control, "VideoPrep.RefPlanDiscover").IsEnabled);
            Assert.AreEqual(1, assistant.Calls); Assert.IsFalse(controller.BlocksNewWork);
            Assert.IsTrue(Find<Button>(control, "VideoPrep.New").IsEnabled);
            Assert.IsTrue(Find<Button>(control, "VideoPrep.RefPlanAdd").IsEnabled);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).Any(e => AutomationProperties.GetAutomationId(e) == "VideoPrep.RefPlanProgress"));
            StringAssert.Contains(Find<TextBlock>(control, "VideoPrep.Status").Text, "Задание остановлено");
            Assert.AreEqual(original, control.Session.Project.IdeaText);
            Click(control, "VideoPrep.RefPlanAdd"); Assert.HasCount(1, control.Session.Project.References!.Plan!.Items);
            LiterarySpellChecking.ReleaseTree(control);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });
}
