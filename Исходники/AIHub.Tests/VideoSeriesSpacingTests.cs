using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoSeriesSpacingTests
{
    internal static VideoProject Fixture()
    {
        var p = VideoSeriesTests.Fixture(); var n = p.Narration[1];
        p.Tracks[2].Clips.Add(new() { AssetId = p.Tracks[2].Clips[0].AssetId, NarrationId = n.Id, Start = 10, Duration = 5, PlannedDuration = 4 });
        var music = new VideoAsset { Path = System.IO.Path.GetFullPath("music.wav"), Kind = "audio", Duration = 90 }; p.Assets.Add(music);
        p.Tracks.Add(new() { Kind = "audio", Name = "Music", Clips = [new() { AssetId = music.Id, Duration = 90 }] }); VideoSeries.Ensure(p); return p;
    }
    [TestMethod]
    public void AllLayersMoveTogetherWithoutMovingIndependentMusicOrChangingDurations()
    {
        var p = Fixture(); p.SelectBySeries = true; p.SeriesLayers = VideoSeriesLayers.None;
        var original = p.Clone(); var plan = VideoSeriesSpacing.Build(p); Assert.IsTrue(plan.Changed > 0); plan.Apply(p); p.Validate();
        var delta = p.Tracks[0].Clips[1].Start - original.Tracks[0].Clips[1].Start; Assert.IsTrue(delta > 1);
        Assert.AreEqual(delta, p.Tracks[1].Clips[1].Start - original.Tracks[1].Clips[1].Start, .0001);
        Assert.AreEqual(delta, p.Tracks[2].Clips[1].Start - original.Tracks[2].Clips[1].Start, .0001); Assert.AreEqual(delta, p.Narration[1].Start - original.Narration[1].Start, .0001);
        Assert.AreEqual(0, p.Tracks[3].Clips[0].Start); Assert.AreEqual(original.Tracks[2].Clips[0].Duration, p.Tracks[2].Clips[0].Duration); Assert.AreEqual(1, p.Tracks[2].Clips[1].Speed);
        foreach (var t in p.Tracks) { var ordered = t.Clips.OrderBy(c => c.Start).ToArray(); for (var i = 1; i < ordered.Length; i++) Assert.IsTrue(ordered[i - 1].Start + ordered[i - 1].Duration <= ordered[i].Start + .001); }
    }
    [TestMethod]
    public void ToggleWithoutEditsRestoresExactlyAndEditsKeepArrangementWithUndo()
    {
        var p = Fixture(); var edit = new VideoEditing(p); var before = JsonSerializer.Serialize(p);
        var preview = new VideoSpacingSession(edit); Assert.IsTrue(preview.Untouched); Assert.IsTrue(preview.Finish()); Assert.AreEqual(before, JsonSerializer.Serialize(edit.Project));
        preview = new(edit); var spaced = edit.Project.Tracks[0].Clips[1].Start;
        edit.Edit(x => x.Tracks[2].Clips[0].Volume = .6); Assert.IsFalse(preview.Untouched); Assert.IsFalse(preview.Finish()); Assert.AreEqual(spaced, edit.Project.Tracks[0].Clips[1].Start);
        edit.Undo(); edit.Undo(); Assert.AreEqual(before, JsonSerializer.Serialize(edit.Project));
    }
    [TestMethod]
    public void LockedAffectedTrackRejectsWithoutPartialMovesAndNonOverlappingPlanIsNoOp()
    {
        var p = Fixture(); p.Tracks[1].Locked = true; var before = JsonSerializer.Serialize(p);
        Assert.ThrowsExactly<InvalidOperationException>(() => VideoSeriesSpacing.Build(p)); Assert.AreEqual(before, JsonSerializer.Serialize(p));
        var edit = new VideoEditing(VideoSelectionTests.Fixture()); var session = new VideoSpacingSession(edit); Assert.IsFalse(edit.CanUndo); Assert.IsFalse(session.Finish());
    }
}
