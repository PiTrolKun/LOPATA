using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Shorts;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Tests;

[TestClass]
public sealed class ShortsProjectTests
{
    [TestMethod]
    public void LegacyDraftAndItsLastJobAreImportedOnce()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new ShortsProjects(files.File("shorts"));
        var id = Guid.NewGuid().ToString("N");
        var draft = new ShortsDraft(true, 179, Song: files.File("song.wav"), Start: 31.68,
            References: [new(files.File("reference.png"), "Singer")], JobId: id, OutputPath: files.File("finished.mp4"),
            ExcerptFrames: 900, FadeEnding: true);
        store.WriteDraft(draft);
        var migrated = store.SaveWorkspace(store.Restore(store.LoadDraft()), draft);
        // A stale active pointer following a successful project write must not create a duplicate.
        var reopened = store.Restore(draft);
        Assert.AreEqual(migrated.Id, reopened.Id);
        Assert.HasCount(1, store.List());
        Assert.AreEqual(id, reopened.Steps.Single().JobId);
        Assert.AreEqual(draft.Start, reopened.Workspace.Start);
        Assert.AreEqual(draft.ExcerptFrames, reopened.Workspace.ExcerptFrames);
        Assert.AreEqual(draft.References![0], reopened.Workspace.References![0]);
        Assert.AreEqual(draft.OutputPath, reopened.Workspace.OutputPath);
        Assert.IsTrue(reopened.Workspace.FadeEnding);
    }

    [TestMethod]
    public void LegacyDraftWithoutJobImportsOnceAndNewDraftStaysTemporaryAcrossRestart()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new ShortsProjects(files.File("shorts"));
        var legacy = new ShortsDraft(Prompt: "Unsubmitted legacy draft");
        var project = store.SaveWorkspace(store.Restore(legacy), legacy);
        Assert.AreEqual(project.Id, store.Restore(legacy).Id);

        var temporary = store.New(new(Prompt: "New draft"));
        store.WriteDraft(temporary.Workspace);
        var reopened = store.Restore(store.LoadDraft());
        Assert.AreEqual(temporary.Id, reopened.Id);
        Assert.AreEqual(temporary.Workspace.Prompt, reopened.Workspace.Prompt);
        Assert.IsFalse(reopened.Persistent);
        Assert.HasCount(1, store.List());
    }

    [TestMethod]
    public void RequestHistoryIsOwnedByProjectAndRetryKeepsOriginalSnapshot()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new ShortsProjects(files.File("shorts"));
        var first = store.New(new(Prompt: "First draft"));
        var second = store.New(new(Prompt: "Second draft"));
        var job = Guid.NewGuid().ToString("N");
        Assert.IsEmpty(store.List());

        var submitted = store.Record(first, first.Workspace, job, DateTimeOffset.Now);
        var retried = store.Record(submitted, submitted.Workspace with { OutputPath = files.File("new.mp4") }, job, DateTimeOffset.Now);
        var manual = store.SaveWorkspace(second, second.Workspace, manual: true);
        store.Rename(manual, " Ручной проект ");
        store.WriteDraft(manual.Workspace);

        Assert.HasCount(2, store.List());
        Assert.AreEqual("Ручной проект", store.Restore(store.LoadDraft()).Name);
        Assert.IsTrue(store.Load(manual.Id).Manual);
        Assert.IsFalse(retried.Manual);
        Assert.AreEqual("First draft", retried.Steps.Single().Snapshot.Prompt);
        Assert.IsNull(retried.Steps.Single().Snapshot.OutputPath);
        Assert.AreEqual(files.File("new.mp4"), retried.Workspace.OutputPath);
        Assert.AreEqual(first.Id, store.FindOwner(first.Id, job)?.Id);
        Assert.IsNull(store.FindOwner(second.Id, job));
        Assert.IsEmpty(store.Load(second.Id).Steps);
    }

    [TestMethod]
    public Task UnchangedTextEventKeepsTheCurrentJobLinked() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new ShortsProjects(files.File("shorts"));
        var draft = new ShortsDraft(Prompt: "A saved prompt", OutputPath: files.File("output.mp4"));
        var id = Guid.NewGuid().ToString("N");
        var project = store.Record(store.New(draft), draft, id, DateTimeOffset.Now);
        store.WriteDraft(project.Workspace);

        var localization = new LocalizationService();
        localization.Load("ru");
        var control = new ShortsControl(localization.T, projects: store);
        var prompt = ScenarioNavigationTests.LogicalDescendants(control).OfType<System.Windows.Controls.TextBox>()
            .Single(element => AutomationProperties.GetAutomationId(element) == "Shorts.Prompt");
        prompt.RaiseEvent(new TextChangedEventArgs(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, UndoAction.None));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.AreEqual(id, store.LoadDraft().JobId);
        Assert.AreEqual(id, store.Load(project.Id).Workspace.JobId);
    });

    [TestMethod]
    public Task ManagerSelectsProjectsAndOffersAttemptActions() => ScenarioNavigationTests.Sta(() =>
    {
        var id = Guid.NewGuid().ToString("N");
        var project = new ScenarioProjectRow(id, "Проект", DateTimeOffset.Now, 1, false, true);
        var opened = "";
        var settingsRestored = false;
        var resultOpened = false;
        var manager = new ScenarioProjectsWindow(new("Shorts.Projects", key => key, () => false,
            () => project, () => [project], () => { }, () => { }, requested => opened = requested, _ => { },
            History: requested =>
            {
                Assert.AreEqual(id, requested);
                return [new("Confirmed result", () => resultOpened = true, () => settingsRestored = true)];
            }));
        try
        {
            var controls = ScenarioNavigationTests.LogicalDescendants(manager).ToArray();
            var table = controls.OfType<DataGrid>().Single(t => AutomationProperties.GetAutomationId(t) == "Shorts.Projects.Automatic");
            Assert.HasCount(1, table.Items);
            table.SelectedIndex = 0;
            Assert.HasCount(1, controls.OfType<ListBox>().Single(b => AutomationProperties.GetAutomationId(b) == "Shorts.Projects.History").Items);
            Click("Open"); Click("OpenResult"); Click("RestoreSettings");
            Assert.AreEqual(id, opened);
            Assert.IsTrue(resultOpened);
            Assert.IsTrue(settingsRestored);

            void Click(string key)
            {
                var button = controls.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Shorts.Projects." + key);
                Assert.IsTrue(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        }
        finally { manager.Close(); }
    });
}
