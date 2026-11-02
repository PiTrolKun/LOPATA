using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipSnapshotTests
{
    [TestMethod]
    public void LegacyGlitchframeSnapshotKeepsItsOriginalBytesAndTimestamp()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var attempt = Attempt();
        var legacy = JsonSerializer.SerializeToNode(attempt)!.AsObject();
        legacy.Remove("Model"); legacy["Settings"]!.AsObject().Remove("Motion");
        var path = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id + "/snapshot.json");
        SongClipStore.Atomic(path, legacy);
        var bytes = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path);
        store.EnsureAttemptSnapshot(attempt);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void ChangedInputsCannotReplaceAnExistingAttempt()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects")); var attempt = Attempt();
        store.EnsureAttemptSnapshot(attempt);
        var path = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id + "/snapshot.json");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => store.EnsureAttemptSnapshot(attempt with { Lyrics = "changed" }));
        Assert.Throws<InvalidDataException>(() => store.EnsureAttemptSnapshot(attempt with
            { Settings = attempt.Settings with { Motion = attempt.Settings.Motion with { Steps = 30 } } }));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    private static SongClipAttempt Attempt() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        "attachments/song.wav", "", "Song", "Lyrics", "", new(), "models", 8);
}
