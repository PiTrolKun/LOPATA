using System.IO;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLyricsPlaybackTests
{
    [TestMethod, DataRow("\n"), DataRow("\r\n"), DataRow("\r")]
    public void CanonicalTimesMapToExactEditorOffsetsIncludingAccentsAndSurrogates(string newline)
    {
        var f = new Fixture(); var shown = f.Lyrics.Replace("\n", newline);
        var first = f.Range(shown, 3.5); var second = f.Range(shown, 8.5);
        Assert.IsNotNull(first); Assert.IsNotNull(second);
        Assert.AreEqual(Fixture.Chorus, shown.Substring(first.Value.Start, first.Value.Length));
        Assert.AreEqual(Fixture.Chorus, shown.Substring(second.Value.Start, second.Value.Length));
        Assert.AreEqual(shown.IndexOf(Fixture.Chorus, StringComparison.Ordinal), first.Value.Start);
        Assert.AreEqual(shown.LastIndexOf(Fixture.Chorus, StringComparison.Ordinal), second.Value.Start);
        Assert.IsTrue(second.Value.Start > first.Value.Start, "Repeated lyrics must have distinct occurrences.");
    }

    [TestMethod]
    public void IntroInterludeOutroAndIntervalBoundariesNeverBorrowAnotherLine()
    {
        var f = new Fixture();
        foreach (var position in new[] { -1d, 0, 2.99, 5, 7.99, 10, 12, 100, double.NaN, double.PositiveInfinity })
            Assert.IsNull(f.Range(f.Lyrics, position), $"Unexpected sung line at {position}.");
        Assert.IsNotNull(f.Range(f.Lyrics, 3)); Assert.IsNotNull(f.Range(f.Lyrics, 4.999));
        Assert.IsNotNull(f.Range(f.Lyrics, 8)); Assert.IsNotNull(f.Range(f.Lyrics, 9.999));
    }

    [TestMethod]
    public void IdenticalLyricsNeverOverrideJobVariantProjectOrDisplayedStepIdentity()
    {
        var f = new Fixture(); Assert.IsNotNull(f.Range(f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { JobId = Guid.NewGuid().ToString("N") }, f.Project, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { Variant = 1 }, f.Project, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { Path = null }, f.Project, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Project with { Id = Guid.NewGuid().ToString("N") }, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Project, null, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Project, Guid.NewGuid().ToString("N"), f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Project with { Steps = [f.Project.Steps[0] with { Number = 2 }] }, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Project with { Steps = [] }, f.Timing.JobId, f.Lyrics, 3.5));
    }

    [TestMethod]
    public void EditingAndChangedSnapshotsImmediatelyRejectTimingButExactUndoCanRestoreIt()
    {
        var f = new Fixture();
        foreach (var altered in new[] { f.Lyrics + " ", f.Lyrics.Replace("мяу́", "мяу"), f.Lyrics.Replace("🌅", "🌞"), f.Lyrics.ToUpperInvariant() })
            Assert.IsNull(f.Range(altered, 3.5), "Only newline convention may differ from the recorded snapshot.");
        var changedProject = f.Project with { Steps = [f.Project.Steps[0] with { Snapshot = new() { Lyrics = f.Lyrics + "!" } }] };
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, changedProject, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNotNull(f.Range(f.Lyrics, 3.5));
        Assert.AreEqual(f.Lyrics, f.Project.Steps[0].Snapshot.Lyrics);
        Assert.AreEqual(f.Lyrics, f.Timing.SourceLyrics);
    }

    [TestMethod]
    public void AdditionalFormatChangesNoTimingButAnotherPerformanceAlwaysDoes()
    {
        var f = new Fixture(); var converted = f.Track with { Path = "same-performance.mp3", AdditionalPath = "same-performance.opus" };
        Assert.AreEqual(f.Range(f.Lyrics, 3.5), MusicLyricsPlaybackBinding.TryGetRange(f.Timing, converted, f.Project, f.Timing.JobId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, converted with { Variant = 1 }, f.Project, f.Timing.JobId, f.Lyrics, 3.5));
        // Conversion eligibility/payload identity is checked by the loading service, not inferred here from filenames.
    }

    [TestMethod]
    public Task SettingsOnlyNavigationKeepsProvenanceWhileOldTrackUsesItsOwnTimesForIdenticalText() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T);
        view.Editor.Lyrics = new Fixture().Lyrics;
        var first = jobs.Create(files.Root, files.Root, "First", 1, 12, "rock", view.Editor.Lyrics);
        first = view.Projects.Record(first, view.Projects.Capture());
        var second = jobs.Create(files.Root, files.Root, "Second", 1, 12, "rock", view.Editor.Lyrics);
        second = view.Projects.Record(second, view.Projects.Capture());
        var timing = new Fixture(first.Id, view.Projects.Current.Id).Timing;
        var track = new MusicTrack("recorded.opus", "First", TimeSpan.FromSeconds(12), DateTime.UtcNow) { JobId = first.Id, Variant = 0 };
        view.Projects.CycleMode(); Assert.AreEqual(MusicHistoryMode.Settings, view.Projects.Mode);
        view.Projects.Navigate(-1);
        Assert.AreEqual(second.Id, view.Projects.DisplayedLyricsJobId);
        Assert.IsNotNull(MusicLyricsPlaybackBinding.TryGetRange(timing, track, view.Projects.Current, view.Projects.DisplayedLyricsJobId, view.Editor.Lyrics, 3.5));
        view.Projects.CycleMode(); Assert.AreEqual(MusicHistoryMode.Off, view.Projects.Mode);
        Assert.IsFalse(view.Projects.CanNavigate(1)); Assert.AreEqual(second.Id, view.Projects.DisplayedLyricsJobId);
        view.Projects.CycleMode(); view.Projects.Navigate(1); view.Projects.Navigate(-1);
        Assert.AreEqual(first.Id, view.Projects.DisplayedLyricsJobId);
        Assert.IsNotNull(MusicLyricsPlaybackBinding.TryGetRange(timing, track, view.Projects.Current, view.Projects.DisplayedLyricsJobId, view.Editor.Lyrics, 3.5));
        var oldId = view.Projects.Current.Id; view.Projects.New();
        Assert.AreNotEqual(oldId, view.Projects.Current.Id); Assert.IsNull(view.Projects.DisplayedLyricsJobId);
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(timing, track, view.Projects.Current, view.Projects.DisplayedLyricsJobId, view.Editor.Lyrics, 3.5));
    });

    [TestMethod]
    public void NewAttemptWithIdenticalSourceDoesNotDisableAnOldTrackButNeverBorrowsItsTimes()
    {
        var f = new Fixture(); var nextId = Guid.NewGuid().ToString("N");
        var nextStep = new MusicProjectStep(2, nextId, DateTimeOffset.UtcNow, new() { Lyrics = f.Lyrics });
        var project = f.Project with { Steps = [f.Project.Steps[0], nextStep] };
        Assert.AreEqual(f.Range(f.Lyrics, 3.5), MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, project, nextId, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { JobId = nextId }, project, nextId, f.Lyrics, 3.5),
            "An identical new performance cannot borrow the old performance's times.");
        var edited = project with { Steps = [f.Project.Steps[0], nextStep with { Snapshot = new() { Lyrics = f.Lyrics + "!" } }] };
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, edited, nextId, f.Lyrics, 3.5),
            "The displayed provenance must also have the exact measured source.");
    }

    [TestMethod]
    public void PlayerUsesTheSelectedFileClockIndependentlyOfGenerationOrHistoryAndNeverDifferentText()
    {
        var f = new Fixture();
        var range = MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Lyrics, 3.5);
        Assert.AreEqual(f.Range(f.Lyrics, 3.5), range);
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track, f.Lyrics + "!", 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { JobId = Guid.NewGuid().ToString("N") }, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { Variant = 1 }, f.Lyrics, 3.5));
        Assert.IsNull(MusicLyricsPlaybackBinding.TryGetRange(f.Timing, f.Track with { Path = null }, f.Lyrics, 3.5));
    }

    private sealed class Fixture
    {
        public const string Chorus = "Солнце, вставай: мяу́! 🌅";
        public string Lyrics { get; } = "[Chorus]\n" + Chorus + "\n[Verse 2]\nКот проспал, но солнце уже встало.\n[Chorus]\n" + Chorus;
        public MusicTimedLyrics Timing { get; }
        public MusicTrack Track { get; }
        public MusicProject Project { get; }
        public Fixture(string? jobId = null, string? projectId = null)
        {
            jobId ??= Guid.NewGuid().ToString("N"); projectId ??= Guid.NewGuid().ToString("N");
            Timing = new() { JobId = jobId, Variant = 0, ProjectId = projectId, ProjectStep = 1,
                SourceLyrics = Lyrics, LyricsHash = MusicTimedLyrics.HashLyrics(Lyrics), AudioPayloadHash = new string('a', 64),
                DurationSeconds = 12, Provider = "fixture", ProviderVersion = "1", Lines = [
                    new(Lyrics.IndexOf(Chorus, StringComparison.Ordinal), Chorus.Length, 3, 5),
                    new(Lyrics.LastIndexOf(Chorus, StringComparison.Ordinal), Chorus.Length, 8, 10)] };
            Timing.Validate();
            Track = new("recorded.opus", "Cat and sun", TimeSpan.FromSeconds(12), DateTime.UtcNow) { JobId = jobId, Variant = 0 };
            Project = new(projectId, 1, DateTimeOffset.UtcNow) { Steps = [new(1, jobId, DateTimeOffset.UtcNow, new() { Lyrics = Lyrics })] };
        }
        public MusicPlaybackTextRange? Range(string lyrics, double seconds) => MusicLyricsPlaybackBinding.TryGetRange(Timing, Track, Project, Timing.JobId, lyrics, seconds);
    }
}
