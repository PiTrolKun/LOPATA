using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoSaveLifetimeTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d));
            ready.SetResult(d); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Action action) => Ui.Value.InvokeAsync(action).Task.WaitAsync(TimeSpan.FromSeconds(30));
    private static Task OnUiAsync(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    private static TextBox Input(VideoPreparationControl control, string id) => ScenarioNavigationTests.LogicalDescendants(control)
        .OfType<TextBox>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    [TestMethod]
    public void ProvenanceBuildVersionMatchesAssemblyAndPreservesSavedHistoricalVersions()
    {
        var assemblyVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(OutputProvenance).Assembly)!.InformationalVersion;
        Assert.AreEqual(assemblyVersion, new OutputProvenance().ProgramVersion);
        Parallel.For(0, 500, _ =>
        {
            var fresh = System.Text.Json.JsonSerializer.Deserialize<OutputProvenance>("{}")!;
            var old = System.Text.Json.JsonSerializer.Deserialize<OutputProvenance>("{\"ProgramVersion\":\"old-version\"}")!;
            Assert.AreEqual(assemblyVersion, fresh.ProgramVersion); Assert.AreEqual("old-version", old.ProgramVersion);
        });
    }

    [TestMethod]
    public Task FileOpenCompletesBeforeWarmupAndUsesTheProjectsChosenModel() => OnUiAsync(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("en");
        var session = new VideoPreparationSession(files.File("recovery"));
        var control = new VideoPreparationControl(l.T, session); var owner = new Window { Content = control };
        owner.Show(); owner.UpdateLayout();
        var resident = new VideoWarmReviewTests.Resident();
        control.ConfigureConceptAssistant(resident, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new AIHub.Models.DebugModelInfo
            { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()));
        var project = new VideoPreparationProject { Form = "story", CurrentStep = "idea", IdeaText = "A quiet town.", LastAssistantModelId = "core" };
        var path = files.File("opened.lopataAIVid"); VideoPreparationArchive.Save(project, path);
        Assert.IsTrue(control.OpenPath(path)); Assert.AreEqual(project.Id, session.Project.Id);
        for (var i = 0; i < 100 && resident.WarmRequests.Count == 0; i++) await Task.Delay(10);
        Assert.AreEqual("core", resident.WarmRequests.Single().ModelId);
        Assert.AreEqual(path, session.ArchivePath); Assert.AreEqual(project.IdeaText, session.Project.IdeaText);
        owner.Close();
    });

    private sealed class SlowWarmup : IVideoConceptAssistant, IVideoConceptResident
    {
        public event Action? RuntimeChanged;
        public VideoConceptRuntimeInfo? RuntimeInfo => null;
        public List<string> Models { get; } = [];
        public bool Canceled { get; private set; }
        public async Task WarmAsync(VideoConceptModelRequest r, CancellationToken token)
        {
            Models.Add(r.ModelId);
            if (Models.Count > 1) return;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public Task RetireAsync() { RuntimeChanged?.Invoke(); return Task.CompletedTask; }
        public Task<VideoConceptAnswer> GenerateAsync(VideoConceptRequest r, CancellationToken token) => throw new AssertFailedException("Opening a file must not generate text");
    }

    [TestMethod]
    public Task FileRequestDuringWarmupCancelsOnlyWarmupAndLoadsChosenProject() => OnUiAsync(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("en");
        var session = new VideoPreparationSession(files.File("recovery")); var control = new VideoPreparationControl(l.T, session);
        var resident = new SlowWarmup();
        control.ConfigureConceptAssistant(resident, _ => Task.FromResult<IReadOnlyList<VideoConceptChoice>>(VideoConceptModels.Ids.Select(id =>
            new VideoConceptChoice(id, VideoConceptModels.Name(id), new AIHub.Models.DebugModelInfo
            { Path = files.File(id + ".gguf"), Format = "gguf", IsRunnable = true }, true, "AiGpuEstimate")).ToArray()));
        var owner = new Window { Content = control }; owner.Show();
        for (var i = 0; i < 100 && resident.Models.Count == 0; i++) await Task.Delay(10);
        Assert.AreEqual("executor", resident.Models.Single()); Assert.IsFalse(session.IsDirty);
        var p = new VideoPreparationProject { Form = "story", CurrentStep = "idea", IdeaText = "A traveller arrives.", LastAssistantModelId = "core" };
        var path = files.File("during-warmup.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        Assert.IsTrue(await control.OpenPathWhenReadyAsync(path));
        for (var i = 0; i < 100 && resident.Models.Count < 2; i++) await Task.Delay(10);
        Assert.IsTrue(resident.Canceled); Assert.AreEqual("core", resident.Models.Last());
        Assert.AreEqual(path, session.ArchivePath); Assert.AreEqual(p.Id, session.Project.Id);
        Assert.IsFalse(session.IsDirty); owner.Close();
    });

    [TestMethod]
    public Task SaveKeepsEditorSelectionUndoAndSpellingWhileWritingBothArchives() => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var session = new VideoPreparationSession(files.File("recovery"));
        var p = session.Project; p.Form = "story"; p.CurrentStep = "idea"; p.IdeaText = "A quiet town.";
        session.Changed(); session.Save(files.File("project.lopataAIVid"));
        var l = new LocalizationService(); l.Load("en"); var control = new VideoPreparationControl(l.T, session);
        var owner = new Window { Content = control }; owner.Show(); owner.UpdateLayout();
        var editor = Input(control, "VideoPrep.Idea"); var root = control.Content;
        editor.Select(editor.Text.Length, 0); editor.SelectedText = " A traveller arrives.";
        var text = editor.Text; editor.Select(2, 5);
        for (var i = 0; i < 5; i++) Assert.IsTrue(control.Save());
        Assert.AreSame(root, control.Content); Assert.AreSame(editor, Input(control, "VideoPrep.Idea"));
        Assert.AreEqual(2, editor.SelectionStart); Assert.AreEqual(5, editor.SelectionLength);
        Assert.IsTrue(editor.CanUndo); Assert.IsTrue(SpellCheck.GetIsEnabled(editor));
        Assert.AreEqual(text, VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened")).IdeaText);
        Assert.AreEqual(text, VideoPreparationArchive.Load(session.RecoveryPath, files.File("recovered")).IdeaText);
        Assert.IsFalse(session.IsDirty); editor.Undo(); Assert.AreNotEqual(text, editor.Text);
        LiterarySpellChecking.ReleaseTree(control); owner.Close();
    });

    [TestMethod]
    public Task RebuildReleasesOldEditorsIncludingCollapsedSourceBeforeReplacement() => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("ru");
        var session = new VideoPreparationSession(files.File("recovery")); var p = session.Project;
        p.Form = "story"; p.CurrentStep = "idea"; p.IdeaText = "Путник идёт по тихому городу.";
        var control = new VideoPreparationControl(l.T, session); var before = Input(control, "VideoPrep.Idea");
        control.Configure(l.T);
        Assert.IsFalse(SpellCheck.GetIsEnabled(before));
        Assert.IsTrue(SpellCheck.GetIsEnabled(Input(control, "VideoPrep.Idea")));
        var hidden = new TextBox(); LiterarySpellChecking.Enable(hidden, "en");
        var tree = new Expander { IsExpanded = false, Content = hidden };
        LiterarySpellChecking.ReleaseTree(tree); Assert.IsFalse(SpellCheck.GetIsEnabled(hidden));
        LiterarySpellChecking.ReleaseTree(control);
    });

    [TestMethod]
    public Task UnloadReloadRestoresLanguageAndPreservesExplicitlyDisabledInput() => OnUi(() =>
    {
        var input = new TextBox { Text = "Example text." };
        LiterarySpellChecking.Enable(input, "en"); LiterarySpellChecking.Enable(input, "en");
        input.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(SpellCheck.GetIsEnabled(input));
        input.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.IsTrue(SpellCheck.GetIsEnabled(input)); Assert.AreEqual("en-us", input.Language.IetfLanguageTag.ToLowerInvariant());
        SpellCheck.SetIsEnabled(input, false);
        input.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        input.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.IsFalse(SpellCheck.GetIsEnabled(input)); Assert.AreEqual("Example text.", input.Text);
    });
}
