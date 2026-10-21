using System.IO;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicNewProjectTests
{
    [TestMethod]
    public Task DefaultsCapturedAfterRestoringCoverDoNotSeedNewProjects() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var store = new MusicProjects(Path.Combine(files.Root, "projects"));
        using var view = new MusicWorkspaceControl(store, new(Path.Combine(files.Root, "jobs")),
            new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.Editor.Lyrics = "Test cover lyrics";
        view.Generation.SetOptions(new("Cover title", 2, 60) { Artist = "Cover artist", Comment = "Cover comment",
            AudioTask = new() { Source = new(Path.Combine(files.Root, "source.wav")) } });
        view.Generation.Melody = MusicMidiTests.Tune();
        view.Projects.Fix(); var savedId = view.Projects.Current.Id;
        view.ConfigureOutput(files.Root, _ => { }); // Restore happened before initial output configuration.
        view.Projects.New();
        Assert.AreEqual("", view.Editor.Lyrics);
        Assert.AreEqual("", view.Generation.Options.Title);
        Assert.AreEqual("", view.Generation.Options.Artist);
        Assert.AreEqual("", view.Generation.Options.Comment);
        Assert.IsFalse(view.Generation.Options.AudioTask.Enabled);
        Assert.IsNull(view.Generation.Options.AudioTask.Source);
        Assert.HasCount(0,view.Generation.Melody.Notes);
        Assert.IsFalse(view.Generation.Melody.Enabled);
        Assert.AreEqual(files.Root, view.Tracks.OutputFolder);
        Assert.IsNull(view.Projects.DisplayedLyricsJobId);
        Assert.HasCount(0, view.Projects.Current.Steps);
        Assert.AreEqual("Test cover lyrics", store.Load(savedId).Saved.Lyrics);
        view.Projects.Open(savedId);
        Assert.AreEqual("Test cover lyrics", view.Editor.Lyrics);
        Assert.HasCount(4,view.Generation.Melody.Notes);
        view.Projects.New();
        Assert.AreEqual("", view.Editor.Lyrics);
        Assert.AreEqual("", view.Generation.Options.Title);
    });
}
