using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoSelectionTests
{
    internal static VideoProject Fixture()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("selection-source.mp4"), Kind = "video", Duration = 60 }; p.Assets.Add(a);
        foreach (var start in new[] { 2d, 10d }) {
            var link = Guid.NewGuid().ToString("N");
            foreach (var t in p.Tracks) t.Clips.Add(new() { AssetId = a.Id, Start = start, Duration = 4, SourceIn = start, LinkId = link });
        }
        p.Narration.AddRange([new() { Start = 2, End = 6, Text = "Первый текст", SubtitleTrackId = "main" }, new() { Start = 10, End = 14, Text = "Второй текст", SubtitleTrackId = "main" }]);
        return p;
    }
    [TestMethod]
    public void GroupMovePreservesOffsetsMovesLinkedPairsOnceAndUndoRestoresAll()
    {
        var p = Fixture(); var edit = new VideoEditing(p);
        VideoItemId[] selected = [new(p.Tracks[0].Clips[0].Id), new(p.Tracks[1].Clips[0].Id), new(p.Tracks[0].Clips[1].Id), new(p.Narration[0].Id, true)];
        edit.Edit(x => VideoSelectionEditing.Move(x, selected, 3));
        CollectionAssert.AreEqual(new[] { 5d, 13d }, p.Tracks[0].Clips.Select(c => c.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 5d, 13d }, p.Tracks[1].Clips.Select(c => c.Start).ToArray());
        Assert.AreEqual(5, VideoSubtitles.Cues(p)[0].Start); Assert.AreEqual(10, VideoSubtitles.Cues(p)[1].Start);
        Assert.AreEqual(5, p.Narration[0].Start); Assert.AreEqual(9, p.Narration[0].End);
        var frame = VideoAnalysisFrames.Plan(p, p.Narration[0])[0]; Assert.AreEqual(5.05, frame.TimelineTime, .001); Assert.AreEqual(2.05, frame.SourceTime, .001);
        edit.Undo(); Assert.AreEqual(2, edit.Project.Tracks[0].Clips[0].Start); Assert.IsNull(edit.Project.Narration[0].SubtitleStart);
        edit.Redo(); Assert.AreEqual(5, edit.Project.Tracks[1].Clips[0].Start);
        VideoSelectionEditing.Move(edit.Project, selected, -100); Assert.AreEqual(0, edit.Project.Tracks[0].Clips[0].Start); Assert.AreEqual(8, edit.Project.Tracks[0].Clips[1].Start);
    }
    [TestMethod]
    public void ClipboardKeepsIndependentPairLinksAndTrackPlacement()
    {
        var p = Fixture(); var selected = p.Tracks[0].Clips.Select(c => new VideoItemId(c.Id)).Concat(p.Narration.Select(n => new VideoItemId(n.Id, true))).ToArray();
        var bundle = VideoSelectionEditing.Capture(p, selected); var pasted = VideoSelectionEditing.Paste(p, bundle, 20); p.Validate();
        Assert.HasCount(6, pasted); Assert.HasCount(4, p.Tracks[0].Clips); Assert.HasCount(4, p.Tracks[1].Clips);
        var copy = p.Tracks[0].Clips.Skip(2).ToArray(); Assert.AreNotEqual(copy[0].LinkId, copy[1].LinkId);
        for (var i = 0; i < 2; i++) { Assert.AreEqual(copy[i].LinkId, p.Tracks[1].Clips[i + 2].LinkId); Assert.AreNotEqual(copy[i].LinkId, p.Tracks[0].Clips[i].LinkId); }
        CollectionAssert.AreEqual(new[] { 20d, 28d }, copy.Select(c => c.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 20d, 28d }, VideoSubtitles.Cues(p).Skip(2).Select(n => n.Start).ToArray());
    }
    [TestMethod]
    public void LockedLinkedPartnerRejectsWholeGroupWithoutPartialEdits()
    {
        var edit = new VideoEditing(Fixture()); edit.Project.Tracks[1].Locked = true;
        VideoItemId[] selected = [new(edit.Project.Tracks[0].Clips[0].Id), new(edit.Project.Narration[0].Id, true)];
        var before = JsonSerializer.Serialize(edit.Project);
        Assert.ThrowsExactly<InvalidOperationException>(() => edit.Edit(p => VideoSelectionEditing.Delete(p, selected)));
        Assert.AreEqual(before, JsonSerializer.Serialize(edit.Project));
    }
    [TestMethod]
    public void LinkedSelectionMapsBothWaysWithoutAdjacentCuesOrBackgroundMusic()
    {
        var p = Fixture(); var video = p.Tracks[0].Clips[0]; var audio = p.Tracks[1].Clips[0]; var text = p.Narration[0];
        var music = new VideoAsset { Path = Path.GetFullPath("background.wav"), Kind = "audio", Duration = 60 }; p.Assets.Add(music);
        var background = new VideoClip { AssetId = music.Id, Duration = 30 }; p.Tracks[1].Clips.Add(background);
        foreach (var root in new[] { new VideoItemId(video.Id), new(audio.Id), new(text.Id, true) }) {
            var mapped = VideoSelectionMap.Expand(p, [root]); Assert.HasCount(3, mapped);
            Assert.Contains(new VideoItemId(video.Id), mapped); Assert.Contains(new VideoItemId(audio.Id), mapped); Assert.Contains(new VideoItemId(text.Id, true), mapped);
            Assert.DoesNotContain(new VideoItemId(background.Id), mapped); Assert.DoesNotContain(new VideoItemId(p.Narration[1].Id, true), mapped);
        }
        var selection = new VideoSelection { Resolve = roots => VideoSelectionMap.Expand(p, roots) };
        selection.Replace(p.Tracks[0].Clips.Select(c => new VideoItemId(c.Id)).ToArray()); Assert.AreEqual(6, selection.Count);
        selection.Toggle(new(text.Id, true)); Assert.AreEqual(3, selection.Count); Assert.IsFalse(selection.Contains(new(video.Id)));
        selection.Replace(selection.Items); Assert.AreEqual(3, selection.Count, "Replacing from the current collection must preserve the selection");
    }
    [TestMethod]
    public void SharedWholeNarrationSelectsAllItsFragmentsAndMovesOnlyOnce()
    {
        var p = Fixture(); var voice = new VideoAsset { Path = Path.GetFullPath("whole-voice.wav"), Kind = "audio", Duration = 20 }; p.Assets.Add(voice);
        var speech = new VideoClip { AssetId = voice.Id, Duration = 20 }; p.Tracks[1].Clips.Add(speech);
        for (var i = 0; i < 2; i++) { p.Narration[i].AudioAssetId = voice.Id; p.Narration[i].SpeechOffset = 2 + i * 8; p.Narration[i].SpeechLength = 4; }
        var mapped = VideoSelectionMap.Expand(p, [new(p.Narration[0].Id, true)]); Assert.HasCount(7, mapped);
        VideoSelectionEditing.Move(p, mapped, 2); Assert.AreEqual(2, speech.Start); Assert.AreEqual(4, p.Tracks[0].Clips[0].Start); Assert.AreEqual(12, p.Tracks[0].Clips[1].Start);
        CollectionAssert.AreEqual(new[] { 4d, 12d }, VideoSubtitles.Cues(p).Select(n => n.Start).ToArray()); p.Validate();
    }
    [TestMethod]
    public void MovingManuallyRetimedTextDoesNotShiftAnUnrelatedOriginalAnalysisIntervalBelowZero()
    {
        var p = Fixture(); var n = p.Narration[0]; VideoSubtitles.SetRange(p, n.Id, 10, 14);
        var group = VideoSelectionMap.Expand(p, [new(n.Id, true)]); VideoSelectionEditing.Move(p, group, -5);
        Assert.AreEqual(5, VideoSubtitles.Cues(p)[0].Start); Assert.AreEqual(2, n.Start); Assert.AreEqual(5, p.Tracks[0].Clips[1].Start); p.Validate();
    }
    [TestMethod]
    public void SubtitleLaneTimingMatchesSpeechThenManualOverridePersistsAndExports()
    {
        var p = Fixture(); var n = p.Narration[0]; p.Tracks[1].Clips.Add(new() { AssetId = p.Assets[0].Id, NarrationId = n.Id, Start = 3, Duration = 8 });
        Assert.AreEqual(3, VideoSubtitles.Cues(p)[0].Start); Assert.AreEqual(7, VideoSubtitles.Cues(p)[0].End, "Long speech must not extend the four-second text slot");
        VideoSubtitles.SetRange(p, n.Id, 5, 9); var copy = p.Clone(); copy.Validate();
        Assert.AreEqual(5, VideoSubtitles.Cues(copy)[0].Start); Assert.AreEqual(9, VideoSubtitles.Cues(copy)[0].End);
        StringAssert.Contains(VideoSubtitles.Srt(VideoSubtitles.Cues(copy), 0, 20), "00:00:05,000 --> 00:00:09,000");
        Assert.AreEqual(3, copy.Tracks[1].Clips[^1].Start); Assert.AreEqual(8, copy.Tracks[1].Clips[^1].Duration);
    }
    [TestMethod]
    public void LongSpeechKeepsOriginalSlotsAndManualResetIsUndoable()
    {
        var p = Fixture(); var edit = new VideoEditing(p); var n = p.Narration[0];
        var voice = new VideoClip { AssetId = p.Assets[0].Id, NarrationId = n.Id, Start = n.Start, Duration = 11.525, PlannedDuration = n.End - n.Start };
        p.Tracks[1].Clips.Add(voice);
        Assert.IsTrue(VideoEditing.TooLong(p, voice)); Assert.AreEqual(2, voice.Start); Assert.AreEqual(11.525, voice.Duration);
        Assert.AreEqual(6, VideoSubtitles.Cues(p)[0].End); Assert.IsTrue(VideoSubtitles.Cues(p)[0].End <= VideoSubtitles.Cues(p)[1].Start);
        VideoSubtitles.SetRange(p, n.Id, 3, 15); Assert.AreEqual(15, VideoSubtitles.Cues(p)[0].End, "Explicit manual timing is preserved");
        edit.Edit(x => VideoSubtitles.ResetRanges(x, [n.Id])); Assert.AreEqual(6, VideoSubtitles.Cues(edit.Project)[0].End);
        edit.Undo(); Assert.AreEqual(15, VideoSubtitles.Cues(edit.Project)[0].End);
        Assert.AreEqual(11.525, edit.Project.Tracks[1].Clips[^1].Duration);
    }
    [TestMethod]
    public void SplitAndDeleteSelectionKeepSubtitleTracksAndLinkedMediaConsistent()
    {
        var p = Fixture(); var first = p.Tracks[0].Clips[0]; var text = p.Narration[0];
        VideoSelectionEditing.Split(p, [new(first.Id), new(p.Tracks[1].Clips[0].Id), new(text.Id, true)], 4); p.Validate();
        Assert.HasCount(3, p.Tracks[0].Clips); Assert.HasCount(3, p.Tracks[1].Clips); Assert.HasCount(3, p.Narration);
        var right = p.Narration.Last(); Assert.AreEqual(text.SubtitleTrackId, right.SubtitleTrackId); Assert.AreEqual(4, right.SubtitleStart); Assert.AreEqual(6, right.SubtitleEnd);
        VideoSelectionEditing.Delete(p, [new(first.Id), new(text.Id, true)]); p.Validate(); Assert.HasCount(2, p.Tracks[0].Clips); Assert.HasCount(2, p.Tracks[1].Clips); Assert.HasCount(2, p.Narration);
    }
    [TestMethod]
    public async Task ManualSubtitleExportMatchesLaneThroughIndependentContainerReadback()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-subtitle-selection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            var source = Path.Combine(root, "source.mp4");
            await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=12:d=2", "-c:v", "libopenh264", "-b:v", "200k", source], CancellationToken.None);
            var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));
            var p = new VideoProject { Width = 160, Height = 90, Fps = 12 }; var asset = await VideoImport.ReadAsync(source, root, CancellationToken.None); VideoImport.Insert(p, asset, 0);
            p.Narration.Add(new() { Start = 0, End = 2, Text = "Проверяем время субтитров" }); VideoSubtitles.SetRange(p, p.Narration[0].Id, .5, 1.25);
            var saved = Path.Combine(root, "project.lopataVideo"); VideoProjectStore.Save(p, saved); p = VideoProjectStore.Load(saved);
            var output = Path.Combine(root, "timing.mkv"); await VideoExport.RunAsync(p, output, new(160, 90, 12, 0, 2, true, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
            var extracted = Path.Combine(root, "readback.srt"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-i", output, "-map", "0:s:0", "-c:s", "copy", "-f", "srt", extracted], CancellationToken.None);
            var cue = VideoSubtitles.Parse(await File.ReadAllTextAsync(extracted)).Single(); Assert.AreEqual(.5, cue.Start, .002); Assert.AreEqual(1.25, cue.End, .002); Assert.AreEqual(p.Narration[0].Text, cue.Text);
            CollectionAssert.AreEqual(before, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
