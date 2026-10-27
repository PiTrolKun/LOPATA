using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferenceDrawingUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Action action) => Ui.Value.InvokeAsync(action).Task.WaitAsync(TimeSpan.FromSeconds(30));
    private static Task OnUiAsync(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task PlusCreatesManualPromptInSameWindowAndKeepsItsTextInArchive(string language) => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var (p, _) = VideoReferenceDrawingTests.Root();
        var l = new LocalizationService(); l.Load(language); var calls = 0; var finishCalls = 0;
        void Save() { VideoReferencePlans.Capture(p); VideoPreparationArchive.Save(p, files.File("project.lopataAIVid")); }
        var window = new VideoReferenceDrawingWindow(null, l.T, p, Save, _ => { calls++; return Task.CompletedTask; },
            (_, _, _, _) => { calls++; return Task.CompletedTask; }, () => false, () => { }, () => { }, finish: () => { finishCalls++; return true; });
        var count = p.References!.Plan!.Items.Count;
        Click(window, "VideoPrep.Drawing.Add");
        Assert.AreEqual(count + 1, p.References.Plan.Items.Count);
        var added = p.References.Plan.Items.Last(); Assert.IsTrue(added.DirectPrompt); Assert.AreEqual("ready", p.References.Plan.Page);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(files.File("project.lopataAIVid"));
        var wizard = new VideoPreparationControl(l.T, session);
        Assert.IsTrue(Find<Button>(wizard, "VideoPrep.RefDrawingOpen").IsEnabled, "An empty added draft must remain reopenable.");
        LiterarySpellChecking.ReleaseTree(wizard.Content as DependencyObject);
        Assert.AreEqual("", Find<TextBox>(window, "VideoPrep.Drawing.Name").Text);
        Assert.AreEqual("", Find<TextBox>(window, "VideoPrep.Drawing.PromptText").Text);
        Find<TextBox>(window, "VideoPrep.Drawing.Name").Text = "Ещё одни часы";
        Find<TextBox>(window, "VideoPrep.Drawing.PromptText").Text = "A brass clock, neutral background.";
        Click(window, "VideoPrep.Drawing.Finish");
        Assert.AreEqual(0, finishCalls, "An unfinished item must not be silently omitted.");
        Assert.AreEqual(0, calls, "Adding and entering a manual prompt must not call any AI or model preparation.");
        window.Close();
        var loaded = VideoPreparationArchive.Load(files.File("project.lopataAIVid"), files.File("opened"));
        var restored = loaded.References!.Plan!.Items.Single(i => i.Id == added.Id);
        Assert.AreEqual("Ещё одни часы", restored.Name); Assert.AreEqual(added.English, restored.English);
        Assert.AreEqual(restored.English, restored.Native); Assert.IsTrue(restored.DirectPrompt);
        Assert.IsTrue(VideoReferencePlans.Ready(loaded, restored));
    });
    [TestMethod]
    public Task FinishClosesOnlyAfterConfirmedSaveAndStaysOutsideScrollingContent() => OnUiAsync(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var (p, item) = VideoReferenceDrawingTests.Root();
        var r = VideoReferenceDrawingTests.Request(p, item, files.File("draw"));
        var turn = await new ImageGenerationRuntime(new VideoReferenceDrawingTests.Worker()).RunAsync(r.Image, [], null, default);
        await VideoReferenceImageWork.ApplyAsync(p, r, turn, files.File("copies"), default);
        VideoReferenceDrawings.Select(p, p.References!.Drawing!.Variants.Single(), true);
        var l = new LocalizationService(); l.Load("ru"); var saved = false; var finished = 0; var closed = false;
        var window = new VideoReferenceDrawingWindow(null, l.T, p, () => { }, _ => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask, () => false, () => { }, () => { }, finish: () => { finished++; return saved; });
        window.Closed += (_, _) => closed = true;
        var footer = (UIElement)LogicalTreeHelper.GetParent(Find<Button>(window, "VideoPrep.Drawing.Finish"));
        Assert.AreEqual(Dock.Bottom, DockPanel.GetDock(footer));
        var left = (UIElement)LogicalTreeHelper.GetParent(Find<Button>(window, "VideoPrep.Drawing.Add"));
        Assert.AreEqual(Dock.Bottom, DockPanel.GetDock(Find<Button>(window, "VideoPrep.Drawing.Add")));
        Assert.IsInstanceOfType<DockPanel>(left);
        Click(window, "VideoPrep.Drawing.Finish"); Assert.AreEqual(1, finished); Assert.IsFalse(closed);
        saved = true; Click(window, "VideoPrep.Drawing.Finish"); Assert.AreEqual(2, finished); Assert.IsTrue(closed);
    });
    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task ManualWindowIsCopyOnlyAndAssistedWindowUsesListWithSubmittedPrompt(string language) => OnUi(() =>
    {
        var l = new LocalizationService(); l.Load(language); var (p, item) = VideoReferenceDrawingTests.Root();
        var manual = new VideoReferencePromptsWindow(null, l.T, p);
        var nodes = ScenarioNavigationTests.LogicalDescendants(manual).ToArray();
        Assert.IsTrue(nodes.OfType<TextBox>().All(t => t.IsReadOnly));
        var ids = nodes.Select(AutomationProperties.GetAutomationId).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        Assert.HasCount(1, ids.Where(id => id.StartsWith("VideoPrep.RefCopyPrompt.")).ToArray());
        Assert.IsFalse(ids.Any(id => id.Contains("Bind") || id.Contains("Import") || id.Contains("Generate")));
        VideoReferencePrompt? submitted = null; string? text = null;
        var saves = 0;
        var window = new VideoReferenceDrawingWindow(null, l.T, p, () => { saves++; VideoReferencePlans.Capture(p); }, _ => Task.CompletedTask,
            (i, prompt, w, h) => { submitted = i; text = prompt; return Task.CompletedTask; }, () => false, () => { }, () => { });
        nodes = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
        var models = nodes.OfType<ComboBox>().Single(); Assert.AreEqual(ImageGenerationCatalog.Manifest.Models.Length, models.Items.Count);
        var input = nodes.OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "VideoPrep.Drawing.PromptText");
        Assert.AreEqual("en-us", input.Language.IetfLanguageTag.ToLowerInvariant());
        input.Text = "My edited English prompt";
        nodes.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "VideoPrep.Drawing.Generate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreSame(item, submitted); Assert.AreEqual(input.Text, text);
        window.Close(); manual.Close();
        Assert.AreEqual(1, saves, "Closing the window must capture an edited prompt even before the debounce fires.");
        Assert.AreEqual("My edited English prompt", item.History.Single(v => v.Id == item.RevisionId).English);
    });
}
