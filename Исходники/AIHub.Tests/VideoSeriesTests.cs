using System.IO;
using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoSeriesTests
{
    internal static VideoProject Fixture()
    {
        var p = VideoSelectionTests.Fixture();
        // The original test fixture has explicit pairs and two event slots.
        var voice = new VideoAsset { Path = Path.GetFullPath("series-voice.wav"), Kind = "audio", Duration = 12 }; p.Assets.Add(voice);
        p.Tracks.Add(new() { Name = "Narration", Kind = "audio", Clips = [new() { AssetId = voice.Id, NarrationId = p.Narration[0].Id, Start = 2, Duration = 9, PlannedDuration = 4 }] });
        p.Narration[0].AudioAssetId = voice.Id; VideoSeries.Ensure(p); return p;
    }
    [TestMethod]
    public void LegacyPairsTextAndVoiceHaveOneStableSeriesIndependentOfTime()
    {
        var p = Fixture(); var first = p.Tracks[0].Clips[0]; var n = p.Narration[0];
        var number = first.SeriesNumbers.Single(); Assert.AreEqual(number, p.Tracks[1].Clips[0].SeriesNumbers.Single()); Assert.AreEqual(number, n.SeriesNumbers.Single()); Assert.AreEqual(number, p.Tracks[2].Clips[0].SeriesNumbers.Single());
        Assert.AreNotEqual(number, p.Tracks[0].Clips[1].SeriesNumbers.Single());
        var next = p.NextSeriesNumber; p.Tracks[2].Clips[0].Start = 50; VideoSeries.Ensure(p);
        Assert.AreEqual(number, p.Tracks[2].Clips[0].SeriesNumbers.Single()); Assert.AreEqual(next, p.NextSeriesNumber);
        p.SelectBySeries = true; var group = VideoSeries.Expand(p, [new(first.Id)]); Assert.HasCount(4, group);
        StringAssert.EndsWith(VideoSeries.Label(p, p.Tracks[0], first, k => k), "_0001");
        StringAssert.EndsWith(VideoSeries.Label(p, n, k => k), "_0001");
    }
    [TestMethod]
    public void LayerMaskExcludesOriginalAudioFromEveryGroupEditDespitePhysicalLink()
    {
        var p = Fixture(); p.SelectBySeries = true; p.SeriesLayers = VideoSeriesLayers.Video | VideoSeriesLayers.Subtitles;
        var first = p.Tracks[0].Clips[0]; var original = p.Tracks[1].Clips[0]; var voice = p.Tracks[2].Clips[0];
        var selected = VideoSeries.Expand(p, [new(first.Id)]); Assert.HasCount(2, selected);
        var edit = new VideoEditing(p); edit.Edit(x => VideoSelectionEditing.Move(x, selected, 3));
        Assert.AreEqual(5, first.Start); Assert.AreEqual(2, original.Start); Assert.AreEqual(2, voice.Start);
        var copy = VideoSelectionEditing.Capture(p, selected); Assert.HasCount(1, copy.Clips);
        edit.Edit(x => VideoSelectionEditing.Delete(x, selected)); Assert.HasCount(2, p.Tracks[1].Clips); Assert.HasCount(1, p.Tracks[2].Clips);
        edit.Undo(); Assert.HasCount(2, edit.Project.Tracks[0].Clips);
    }
    [TestMethod]
    public void SplitAndPasteGiveNewCommonSeriesAndPreserveOldNumbers()
    {
        var p = Fixture(); p.SelectBySeries = true;
        var first = p.Tracks[0].Clips[0]; var old = first.SeriesNumbers.Single();
        var selected = VideoSeries.Expand(p, [new(first.Id)]);
        VideoSelectionEditing.Split(p, selected, 4); p.Validate();
        var rightVideo = p.Tracks[0].Clips.Single(c => c.Start == 4); var rightText = p.Narration.Single(n => n.Start == 4);
        var next = rightVideo.SeriesNumbers.Single(); Assert.AreNotEqual(old, next);
        Assert.AreEqual(next, p.Tracks[1].Clips.Single(c => c.Start == 4).SeriesNumbers.Single());
        Assert.AreEqual(next, rightText.SeriesNumbers.Single());
        var rightVoice = p.Tracks[2].Clips.Single(c => c.Start == 4); Assert.AreEqual(next, rightVoice.SeriesNumbers.Single()); Assert.AreEqual(rightText.Id, rightVoice.NarrationId);
        var capture = VideoSelectionEditing.Capture(p, VideoSeries.Expand(p, [new(rightVideo.Id)]));
        var pasted = VideoSelectionEditing.Paste(p, capture, 20); p.Validate();
        var pastedNumbers = p.Tracks.SelectMany(t => t.Clips).Where(c => pasted.Contains(new(c.Id))).SelectMany(c => c.SeriesNumbers).Distinct().ToArray();
        Assert.HasCount(1, pastedNumbers); Assert.AreNotEqual(next, pastedNumbers[0]);
        Assert.AreEqual(old, first.SeriesNumbers.Single()); Assert.AreEqual(next, rightText.SeriesNumbers.Single());
    }
    [TestMethod]
    public void EventCutsShareSeriesAndDoNotRenumberExistingImportedMaterial()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("events-series.mp4"), Kind = "video", AudioStream = 1, Duration = 10 }; VideoImport.Insert(p, a, 0);
        VideoEventCuts.Apply(p, p.Tracks[0].Clips[0].Id, [new() { Start = 0, End = 3 }, new() { Start = 3, End = 6 }, new() { Start = 6, End = 10 }]);
        var videos = p.Tracks[0].Clips.OrderBy(c => c.Start).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, videos.Select(c => c.SeriesNumbers.Single()).ToArray());
        foreach (var c in videos) { Assert.AreEqual(c.SeriesNumbers.Single(), p.Tracks[1].Clips.Single(x => x.Start == c.Start).SeriesNumbers.Single()); Assert.AreEqual(c.SeriesNumbers.Single(), p.Narration.Single(n => n.Start == c.Start).SeriesNumbers.Single()); }
    }
    [TestMethod]
    public void SeriesPreferencesPersistAndWholeVoiceRemainsIndivisible()
    {
        var p = Fixture(); var voice = p.Tracks[2].Clips[0]; voice.NarrationId = null;
        foreach (var n in p.Narration) n.AudioAssetId = voice.AssetId;
        voice.SeriesNumbers = []; VideoSeries.Ensure(p); Assert.HasCount(2, voice.SeriesNumbers);
        p.SelectBySeries = true; Assert.HasCount(7, VideoSeries.Expand(p, [new(p.Narration[0].Id, true)]));
        p.SeriesLayers = VideoSeriesLayers.Subtitles; p.StretchAudio = false;
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lopataVideo"); VideoProjectStore.Save(p, file);
        var loaded = VideoProjectStore.Load(file); Assert.IsTrue(loaded.SelectBySeries); Assert.AreEqual(VideoSeriesLayers.Subtitles, loaded.SeriesLayers); Assert.IsFalse(loaded.StretchAudio);
        Assert.AreEqual(p.NextSeriesNumber, loaded.NextSeriesNumber); Assert.AreEqual(JsonSerializer.Serialize(p.Tracks), JsonSerializer.Serialize(loaded.Tracks));
        var legacy = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SERIES_PROJECT");
        if (!string.IsNullOrWhiteSpace(legacy)) {
            var before = File.ReadAllBytes(legacy); var restored = VideoProjectStore.Load(legacy);
            foreach (var pair in restored.Tracks.SelectMany(t => t.Clips).Where(c => c.LinkId is not null).GroupBy(c => c.LinkId)) Assert.AreEqual(1, pair.Select(c => VideoSeries.Suffix(c.SeriesNumbers)).Distinct().Count());
            foreach (var c in restored.Tracks.SelectMany(t => t.Clips).Where(c => c.NarrationId is not null)) CollectionAssert.AreEqual(restored.Narration.Single(n => n.Id == c.NarrationId).SeriesNumbers, c.SeriesNumbers);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(legacy), "Reading a legacy project must not rewrite it");
            var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SERIES_EVIDENCE")!; Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "legacy-series.json"), JsonSerializer.Serialize(new { restored.NextSeriesNumber, Clips = restored.Tracks.SelectMany(t => t.Clips.Select(c => new { c.Id, c.SeriesNumbers, Label = VideoSeries.Label(restored, t, c, s => s) })), Text = restored.Narration.Select(n => new { n.Id, n.SeriesNumbers }) }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    [TestMethod]
    public void AudioStretchPreservesSourceIntervalFromEitherEndAndUndoRestoresSpeed()
    {
        var p = Fixture(); var c = p.Tracks[2].Clips[0]; var edit = new VideoEditing(p); var sourceEnd = c.SourceIn + c.Duration * c.Speed;
        edit.Edit(x => VideoAudioStretch.Resize(x, c.Id, c.Start, 4)); Assert.AreEqual(2.25, c.Speed); Assert.AreEqual(4, c.Duration); Assert.IsFalse(VideoEditing.TooLong(p, c)); Assert.AreEqual(sourceEnd, c.SourceIn + c.Duration * c.Speed);
        edit.Edit(x => VideoAudioStretch.Resize(x, c.Id, 1, 5)); Assert.AreEqual(1, c.Start); Assert.AreEqual(1.8, c.Speed); Assert.AreEqual(sourceEnd, c.SourceIn + c.Duration * c.Speed);
        edit.Undo(); Assert.AreEqual(2.25, edit.Project.Tracks[2].Clips[0].Speed); Assert.AreEqual(2, edit.Project.Tracks[0].Clips[0].Start);
        Assert.ThrowsExactly<InvalidDataException>(() => VideoAudioStretch.Resize(edit.Project, c.Id, 0, .001));
    }
}
