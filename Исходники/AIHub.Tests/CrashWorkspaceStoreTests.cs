using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class CrashWorkspaceStoreTests
{
    [TestMethod]
    public void InterruptedWriteLeavesPreviousWorkspaceAndNextSaveReplacesIt()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-workspace-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.json");
            var store = new CrashWorkspaceStore(path);
            Assert.IsNull(store.Load());

            var placement = new RememberedWindowPlacement { HasValue = true, LeftRatio = 0.2, WasMaximized = true };
            var layout = new CrashWindowLayout(placement, 2);
            var workspace = new CrashWorkspaceSnapshot(CrashWorkspaceStore.CurrentSchema,
                new(ScenarioNavigationCatalog.SongClip, Project: "song-project", Scroll: 17), layout, false,
                [new(ScenarioNavigationCatalog.VideoEditor, "video-project", null, 5.1, true, layout, Dirty: true)]);
            store.Save(JsonSerializer.SerializeToUtf8Bytes(workspace));
            File.WriteAllText(path + ".tmp", "{incomplete");

            var restored = store.Load();
            Assert.IsNotNull(restored);
            Assert.AreEqual(workspace.Page, restored.Page);
            Assert.AreEqual(workspace.Windows[0].Project, restored.Windows[0].Project);
            Assert.AreEqual(workspace.Windows[0].Position, restored.Windows[0].Position);
            Assert.IsTrue(restored.Windows[0].Dirty);
            Assert.AreEqual(0.2, restored.MainWindow.Placement.LeftRatio);
            Assert.IsTrue(restored.MainWindow.Placement.WasMaximized);

            store.Save(JsonSerializer.SerializeToUtf8Bytes(workspace with { WasInTray = true }));
            Assert.IsTrue(store.Load()!.WasInTray);
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public void VectorDraftSurvivesCorruptOrReplacedUserArchive(bool replaced)
    {
        using var directory = new VectorEditorProjectTests.Folder();
        var archive = Path.Combine(directory.Path, "original.lopataSVG");
        var recovery = Path.Combine(directory.Path, "recovery.lopataSVG");
        var saved = VectorProject.New("Saved", "Sheet");
        VectorProjectArchive.Save(saved, archive);
        var draft = saved.Copy();
        draft.Name = "Unsaved";
        VectorProjectArchive.Save(draft, recovery);

        var opened = MainWindow.LoadCrashRecoveryVectorProject(archive, recovery, saved.Id);
        Assert.IsNotNull(opened);
        Assert.AreEqual(archive, opened.Value.Archive);

        if (replaced) VectorProjectArchive.Save(VectorProject.New("Another project", "Sheet"), archive);
        else File.WriteAllText(archive, "broken archive");

        opened = MainWindow.LoadCrashRecoveryVectorProject(archive, recovery, saved.Id);
        Assert.IsNotNull(opened);
        Assert.AreEqual("Unsaved", opened.Value.Project.Name);
        Assert.IsNull(opened.Value.Archive);
        Assert.IsNull(MainWindow.LoadCrashRecoveryVectorProject(archive, recovery, Guid.NewGuid().ToString("N")));
    }
}
