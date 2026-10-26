using AIHub.Controls.VideoEditor;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoVoiceSelectionTests
{
    internal static VideoProject TenFragments() => new() { Narration = Enumerable.Range(0, 10)
        .Select(i => new NarrationSegment { Start = i, End = i + 1, Text = "Привет." }).ToList() };
    private static VideoNarratorChoice Silero() => new("silero56", "ru", 100, new("xenia"));

    [TestMethod]
    public void OneSelectedFragmentAlsoUpdatesWholeNarrationWithoutOverwritingOtherFragments()
    {
        var project = TenFragments(); var choice = Silero();
        VideoVoiceProfiles.Apply(project, choice, false, new HashSet<string> { project.Narration[0].Id });
        Assert.AreEqual("silero56", project.DefaultVoice);
        CollectionAssert.AreEqual(new[] { "silero56" }, VideoEditorWindow.ModelNeed(project, "speech", null, true).Voices);
        Assert.AreEqual("silero56", project.Narration[0].Provider);
        Assert.IsTrue(project.Narration.Skip(1).All(n => n.Provider == "kokoro-sveta"));
        Assert.AreEqual(project.DefaultVoiceOptions, project.Clone().DefaultVoiceOptions);
    }

    [TestMethod]
    public void SharedRecordedAudioDoesNotBecomeMixedWhenPlannedFragmentProfilesDiffer()
    {
        var project = RecordedWhole(); var selected = project.Narration.Select(n => n.Id).ToHashSet();
        project.Narration[0].Provider = "silero56";
        var summary = VideoVoiceSummary.Resolve(project, selected, false, 0);
        Assert.IsTrue(summary.Recorded); Assert.HasCount(1, summary.Profiles);
        Assert.AreEqual("kokoro-sveta", summary.Profiles[0]!.Provider);
        var playing = VideoVoiceSummary.Resolve(project, selected, true, .5);
        Assert.HasCount(1, playing.Profiles); Assert.AreEqual("kokoro-sveta", playing.Profiles[0]!.Provider);
    }

    [TestMethod]
    public void NewChoiceKeepsTheOldRecordingAndPlaybackReportsItsActualVoice()
    {
        var project = RecordedWhole();
        VideoVoiceProfiles.Apply(project, Silero(), false, new HashSet<string> { project.Narration[0].Id });
        Assert.IsTrue(project.Narration.All(n => n.Stale)); Assert.HasCount(1, project.Assets);
        Assert.AreEqual("silero56", VideoVoiceSummary.Resolve(project, new HashSet<string> { project.Narration[0].Id }, false, 0).Profiles[0]!.Provider);
        Assert.AreEqual("kokoro-sveta", VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, .5).Profiles[0]!.Provider);
    }

    [TestMethod]
    public void PlaybackFollowsAudibleClipsAndDoesNotInventHistoryForOldAudio()
    {
        var project = RecordedWhole(); var track = project.Tracks.Last();
        track.Clips[0].Duration = 1;
        project.Assets.Add(new() { Id = "silero", Kind = "audio", NarratorProfile = new("silero56", "ru", 100, new("xenia")) });
        track.Clips.Add(new() { AssetId = "silero", Start = 2, Duration = 1 });
        Assert.AreEqual("silero56", VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, 2.5).Profiles[0]!.Provider);
        Assert.HasCount(0, VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, 1.5).Profiles);
        track.Muted = true; Assert.HasCount(0, VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, 2.5).Profiles);
        track.Muted = false; track.Volume = 0; Assert.HasCount(0, VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, .5).Profiles);
        track.Volume = 1; track.Clips[0].Volume = 0; Assert.HasCount(0, VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, .5).Profiles);
        track.Clips[0].Volume = 1; project.Assets[0].NarratorProfile = null;
        Assert.IsNull(VideoVoiceSummary.Resolve(project, new HashSet<string>(), true, .5).Profiles.Single());
    }

    [TestMethod]
    public void CommonOnlyChoicePreservesFragmentOverridesAndUndoRestoresBothChoices()
    {
        var edit = new VideoEditing(TenFragments());
        edit.Edit(p => VideoVoiceProfiles.Apply(p, Silero(), true, p.Narration.Select(n => n.Id).ToHashSet()));
        Assert.AreEqual("silero56", edit.Project.DefaultVoice);
        Assert.IsTrue(edit.Project.Narration.All(n => n.Provider == "kokoro-sveta"));
        edit.Undo(); Assert.AreEqual("kokoro-sveta", edit.Project.DefaultVoice);
        edit.Redo(); Assert.AreEqual("silero56", edit.Project.DefaultVoice);
    }

    private static VideoProject RecordedWhole()
    {
        var project = TenFragments();
        project.Assets.Add(new() { Id = "recording", Kind = "audio", NarratorProfile = new("kokoro-sveta", "ru", 100, new()) });
        project.Tracks.Add(new() { Kind = "audio", Name = "Narration", Clips = [new() { AssetId = "recording", Duration = 10 }] });
        foreach (var n in project.Narration) n.AudioAssetId = "recording";
        return project;
    }
}
