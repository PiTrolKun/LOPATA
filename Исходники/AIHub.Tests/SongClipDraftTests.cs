using System.IO;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipDraftTests
{
    [TestMethod]
    public void UntouchedNewDraftNeverCreatesAProjectDirectory()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var project = new SongClipProject();
        var updated = project.UpdatedUtc;

        Assert.IsFalse(store.SaveDraft(project));
        Assert.IsFalse(store.SaveDraft(project));
        Assert.AreEqual(updated, project.UpdatedUtc);
        Assert.IsFalse(Directory.Exists(store.Folder(project.Id)));
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    [DataRow("name")]
    [DataRow("lyrics")]
    [DataRow("timings")]
    [DataRow("song")]
    [DataRow("reference")]
    [DataRow("pending")]
    [DataRow("result")]
    [DataRow("pinned")]
    [DataRow("model")]
    [DataRow("settings")]
    [DataRow("motion-settings")]
    public void DeliberateDraftDataIsPersistedAndListed(string field)
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var project = new SongClipProject();
        switch (field)
        {
            case "name": project.Name = "An authored draft"; break;
            case "lyrics": project.Lyrics = "Words before audio import"; break;
            case "timings": project.Timings = "[00:01]Words"; break;
            case "song": project.Song = "attachments/song.wav"; break;
            case "reference": project.Reference = "attachments/reference.png"; break;
            case "pending": project.PendingAttemptId = Guid.NewGuid().ToString("N"); break;
            case "result": project.Results.Add(new(Guid.NewGuid().ToString("N"), "attempts/clip.mp4", "glitchframe", 2, DateTimeOffset.UtcNow)); break;
            case "pinned": project.Pinned = true; break;
            case "model": project.Model = H3MusicRunner.ModelId; break;
            case "settings": project.Settings.Steps = 40; break;
            case "motion-settings": project.Settings.Motion.ContextFrames = 19; break;
            default: Assert.Fail("Unknown draft data case."); break;
        }

        Assert.IsTrue(store.SaveDraft(project));
        Assert.AreEqual(project.Id, store.Load(project.Id).Id);
        Assert.AreEqual(project.Id, store.List().Single().Id);
    }

    [TestMethod]
    public void LegacyEmptyProjectsAreHiddenWithoutChangingTheirFiles()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var empty = new SongClipProject();
        var meaningful = new SongClipProject { Name = "Keep this project" };
        store.Save(empty); store.Save(meaningful);
        var path = Path.Combine(store.Folder(empty.Id), "project.json");
        var before = File.ReadAllBytes(path);

        Assert.AreEqual(meaningful.Id, store.List().Single().Id);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.AreEqual(empty.Id, store.Load(empty.Id).Id);
    }

    [TestMethod]
    public void ExistingProjectCanBeClearedAndSavedWithoutResurrectingOldContent()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var project = new SongClipProject { Name = "Remove this name", Lyrics = "Remove these words" };
        store.Save(project);
        project.Name = ""; project.Lyrics = "";

        Assert.IsTrue(store.SaveDraft(project));
        var restored = store.Load(project.Id);
        Assert.AreEqual("", restored.Name);
        Assert.AreEqual("", restored.Lyrics);
        Assert.AreEqual(project.Id, restored.Id);
    }

    [TestMethod]
    [DataRow("attachments/reference.png")]
    [DataRow("attempts/saved-attempt/snapshot.json")]
    public void OwnedMaterialKeepsAnOtherwiseEmptyLegacyProjectVisible(string relative)
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var project = new SongClipProject();
        store.Save(project);
        var material = store.Resolve(project.Id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(material)!);
        File.WriteAllBytes(material, [1, 2, 3]);

        Assert.AreEqual(project.Id, store.List().Single().Id);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(material));
    }
}
