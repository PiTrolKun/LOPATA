using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipImportTests
{
    [TestMethod]
    public async Task FirstSongKeepsTheDraftAndPretypedLyrics()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var current = new SongClipProject { Name = "Draft", Lyrics = "Words entered before importing audio" };
        var source = files.File("first.wav");
        byte[] audio = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(source, audio);

        var imported = await SongClipImport.AddSongAsync(store, current, source, CancellationToken.None);

        Assert.AreSame(current, imported);
        Assert.AreEqual("Words entered before importing audio", imported.Lyrics);
        Assert.IsFalse(string.IsNullOrWhiteSpace(imported.Song));
        File.Delete(source);
        CollectionAssert.AreEqual(audio, await File.ReadAllBytesAsync(store.Resolve(imported.Id, imported.Song)));
    }

    [TestMethod]
    public async Task DifferentSongGetsANewProjectWithoutOldContentOrSharedMutableSettings()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var current = new SongClipProject
        {
            Name = "First song",
            Model = "h3_any_song",
            Lyrics = "Old lyrics",
            Timings = "[00:01.00]Old lyrics",
            PendingAttemptId = Guid.NewGuid().ToString("N"),
            Settings = new()
            {
                Width = 1920, Height = 1080, Fps = 30, Steps = 40, Interval = 16,
                Seed = 1257, Shader = "other-shader", Intensity = 1.2, Quality = 70,
                Prompt = "Artwork specifically describing the old song"
            }
        };
        byte[] oldAudio = [1, 2, 3, 4];
        var oldSource = files.File("old.wav");
        var referenceSource = files.File("old-reference.png");
        await File.WriteAllBytesAsync(oldSource, oldAudio);
        await File.WriteAllBytesAsync(referenceSource, [9, 8, 7]);
        current.Song = await store.ImportAsync(current.Id, oldSource, CancellationToken.None);
        current.Reference = await store.ImportAsync(current.Id, referenceSource, CancellationToken.None);
        current.Results.Add(new(Guid.NewGuid().ToString("N"), "attempts/old/clip.mp4", "glitchframe", 8, DateTimeOffset.UtcNow));
        store.Save(current);
        var originalState = JsonSerializer.Serialize(current);
        var oldProjectFile = Path.Combine(store.Folder(current.Id), "project.json");
        var originalDocument = await File.ReadAllBytesAsync(oldProjectFile);
        var source = files.File("new.wav");
        byte[] newAudio = [5, 6, 7, 8];
        await File.WriteAllBytesAsync(source, newAudio);

        var imported = await SongClipImport.AddSongAsync(store, current, source, CancellationToken.None);

        Assert.AreNotSame(current, imported);
        Assert.AreNotEqual(current.Id, imported.Id);
        Assert.AreEqual(current.Model, imported.Model);
        Assert.AreEqual(current.Settings with { Prompt = new SongClipSettings().Prompt }, imported.Settings);
        Assert.AreNotSame(current.Settings, imported.Settings);
        Assert.AreEqual("", imported.Lyrics);
        Assert.AreEqual("", imported.Timings);
        Assert.AreEqual("", imported.Reference);
        Assert.AreEqual("", imported.PendingAttemptId);
        Assert.AreEqual(0, imported.Results.Count);
        Assert.AreEqual(originalState, JsonSerializer.Serialize(current));
        CollectionAssert.AreEqual(originalDocument, await File.ReadAllBytesAsync(oldProjectFile));
        CollectionAssert.AreEqual(oldAudio, await File.ReadAllBytesAsync(store.Resolve(current.Id, current.Song)));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(store.Resolve(current.Id, current.Reference)));
        CollectionAssert.AreEqual(newAudio, await File.ReadAllBytesAsync(store.Resolve(imported.Id, imported.Song)));
        imported.Settings.Steps = 8;
        Assert.AreEqual(40, current.Settings.Steps);
    }

    [TestMethod]
    public async Task IdenticalBytesUnderAnotherFilenameKeepTheExistingProjectAndHistory()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var current = new SongClipProject { Lyrics = "Keep lyrics", Timings = "[00:01.00]Keep lyrics" };
        var original = files.File("first-name.wav");
        var renamed = files.File("another-name.wav");
        byte[] audio = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(original, audio);
        await File.WriteAllBytesAsync(renamed, audio);
        current.Song = await store.ImportAsync(current.Id, original, CancellationToken.None);
        current.Results.Add(new(Guid.NewGuid().ToString("N"), "attempts/old/clip.mp4", "glitchframe", 8, DateTimeOffset.UtcNow));
        var previousSong = current.Song;
        var previousResult = current.Results.Single();

        var imported = await SongClipImport.AddSongAsync(store, current, renamed, CancellationToken.None);

        Assert.AreSame(current, imported);
        Assert.AreEqual(previousSong, imported.Song);
        Assert.AreEqual("Keep lyrics", imported.Lyrics);
        Assert.AreEqual("[00:01.00]Keep lyrics", imported.Timings);
        Assert.AreSame(previousResult, imported.Results.Single());
        CollectionAssert.AreEqual(audio, await File.ReadAllBytesAsync(store.Resolve(imported.Id, imported.Song)));
    }

    [TestMethod]
    public async Task CanceledImportLeavesTheCurrentProjectAndItsSavedDocumentUntouched()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var current = new SongClipProject { Name = "Keep project", Lyrics = "Keep lyrics" };
        var original = files.File("original.wav");
        await File.WriteAllBytesAsync(original, [1, 2, 3, 4]);
        current.Song = await store.ImportAsync(current.Id, original, CancellationToken.None);
        store.Save(current);
        var originalState = JsonSerializer.Serialize(current);
        var projectFile = Path.Combine(store.Folder(current.Id), "project.json");
        var originalDocument = await File.ReadAllBytesAsync(projectFile);
        var source = files.File("replacement.wav");
        await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SongClipImport.AddSongAsync(store, current, source, cancellation.Token));

        Assert.AreEqual(originalState, JsonSerializer.Serialize(current));
        CollectionAssert.AreEqual(originalDocument, await File.ReadAllBytesAsync(projectFile));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(store.Resolve(current.Id, current.Song)));
        Assert.AreEqual(1, store.List().Count);
    }
}
