using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoEditorTests
{
    private static VideoProject Sample()
    {
        var p = new VideoProject(); var a = new VideoAsset { Path = Path.GetFullPath("input.mp4"), Duration = 20, Kind = "video", AudioStream = 1, VideoStream = 0, Width = 640, Height = 360 };
        VideoImport.Insert(p, a, 0); return p;
    }
    [TestMethod]
    public void LinkedSplitMoveAndUndoPreserveSourceAndOffsets()
    {
        var edit = new VideoEditing(Sample()); var id = edit.Project.Tracks[0].Clips[0].Id;
        edit.Edit(p => VideoEditing.Split(p, id, 5));
        Assert.AreEqual(4, edit.Project.Tracks.Sum(t => t.Clips.Count));
        Assert.AreEqual(5d, edit.Project.Tracks[1].Clips[1].SourceIn);
        var right = edit.Project.Tracks[0].Clips[1]; edit.Edit(p => VideoEditing.Move(p, right.Id, 8));
        Assert.AreEqual(8d, edit.Project.Tracks[1].Clips[1].Start); edit.Undo(); Assert.AreEqual(5d, edit.Project.Tracks[0].Clips[1].Start);
        edit.Undo(); Assert.AreEqual(2, edit.Project.Tracks.Sum(t => t.Clips.Count)); edit.Redo(); Assert.AreEqual(4, edit.Project.Tracks.Sum(t => t.Clips.Count));
    }
    [TestMethod]
    public void InvalidEditRollsBackInsteadOfLosingProject()
    {
        var edit = new VideoEditing(Sample()); var before = JsonSerializer.Serialize(edit.Project);
        Assert.ThrowsExactly<InvalidDataException>(() => edit.Edit(p => p.Tracks[0].Clips[0].Speed = double.NaN));
        Assert.AreEqual(before, JsonSerializer.Serialize(edit.Project));
    }
    [TestMethod]
    public void LockedTracksCannotBeDraggedOrSplit()
    {
        var p = Sample(); p.Tracks[0].Locked = true; var c = p.Tracks[0].Clips[0];
        VideoEditing.Move(p, c.Id, 5); VideoEditing.Split(p, c.Id, 3); Assert.AreEqual(0d, c.Start); Assert.HasCount(1, p.Tracks[0].Clips);
    }
    [TestMethod]
    public void LinkedTimingPreservesAudioSyncAndRespectsLockedCompanion()
    {
        var edit = new VideoEditing(Sample()); var id = edit.Project.Tracks[0].Clips[0].Id;
        edit.Edit(p => VideoEditing.SetTiming(p, id, 3, 2, 9, 2));
        foreach (var c in edit.Project.Tracks.SelectMany(t => t.Clips))
        { Assert.AreEqual(3d, c.Start); Assert.AreEqual(2d, c.SourceIn); Assert.AreEqual(9d, c.Duration); Assert.AreEqual(2d, c.Speed); }
        edit.Project.Tracks[1].Locked = true;
        edit.Edit(p => VideoEditing.SetTiming(p, id, 4, 4, 8, 2));
        Assert.AreEqual(3d, edit.Project.Tracks[0].Clips[0].Start);
        Assert.AreEqual(2d, edit.Project.Tracks[0].Clips[0].SourceIn);
        Assert.AreEqual(9d, edit.Project.Tracks[1].Clips[0].Duration);
    }
    [TestMethod]
    public void LongSpeechRemainsValidRedClipAndSpeedCanFitIt()
    {
        var p = Sample(); var c = p.Tracks[1].Clips[0]; c.PlannedDuration = 5; Assert.IsTrue(VideoEditing.TooLong(p, c)); p.Validate();
        c.Speed = 4; c.Duration = 5; Assert.IsFalse(VideoEditing.TooLong(p, c)); p.Validate();
    }
    [TestMethod]
    public void SubtitleRangeIsShiftedAndClippedToFinalExport()
    {
        var rows = new[] { new NarrationSegment { Start = 1, End = 4, Text = "Русский текст" }, new NarrationSegment { Start = 4, End = 8, Text = "Вторая строка" } };
        var srt = VideoSubtitles.Srt(rows, 2, 6);
        StringAssert.Contains(srt, "00:00:00,000 --> 00:00:02,000"); StringAssert.Contains(srt, "00:00:02,000 --> 00:00:04,000");
        Assert.HasCount(2, VideoSubtitles.Parse(srt)); Assert.IsFalse(srt.Contains("LOPATA"));
    }
    [TestMethod]
    public void ProjectSourceCannotBeOverwrittenAndUnknownNarratorCannotFallback()
    {
        var p = Sample(); Assert.ThrowsExactly<IOException>(() => VideoProjectStore.Save(p, p.Assets[0].Path));
        Assert.ThrowsExactly<InvalidDataException>(() => new VideoNarratorRegistry().Create("not-installed"));
        StringAssert.Contains(VideoExport.Tempo(4), "atempo=2,atempo=2");
    }
    [TestMethod]
    [DataRow(".mp4", false)]
    [DataRow(".mkv", true)]
    public async Task ExportRealMediaKeepsSourcesAndWritesExpectedTracks(string extension, bool srt)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-video-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync;
        var requested = new HashSet<string>();
        try
        {
            // Test-only gate records requested components; no user licence receipts are created.
            ComponentLicenseGate.ConfirmAsync = (ids, _) => { foreach (var id in ids) requested.Add(id); return Task.CompletedTask; };
            await VideoRuntime.PrepareAsync(CancellationToken.None); await VideoRuntime.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(root, "источник.mp4");
            await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=12:d=2", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=2", "-c:v", "libopenh264", "-b:v", "500k", "-c:a", "aac", source], CancellationToken.None);
            var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var p = new VideoProject { Width = 320, Height = 180, Fps = 12 }; var asset = await VideoImport.ReadAsync(source, root, CancellationToken.None); VideoImport.Insert(p, asset, 0);
            var svg = Path.Combine(root, "element.svg"); await File.WriteAllTextAsync(svg, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 320 180'><rect x='200' y='20' width='60' height='60' fill='red'/></svg>");
            var vector = await VideoImport.ReadAsync(svg, root, CancellationToken.None); p.Assets.Add(vector); p.Tracks.Add(new() { Kind = "video", Name = "Overlay", Clips = [new() { AssetId = vector.Id, Duration = 2 }] });
            p.Narration.Add(new() { Start = 0, End = 1.5, Text = "Проверка русского текста" });
            if (extension == ".mkv") { p.Narration[0].SubtitleTitle = "forced"; p.Narration.Add(new() { Start = .2, End = 1, Text = "Вторая русская дорожка", SubtitleTrackId = "second", SubtitleTitle = "short", SubtitleDefault = true, SubtitleForced = true }); }
            var output = Path.Combine(root, "результат" + extension);
            await VideoExport.RunAsync(p, output, new(320, 180, 12, 0, 2, true, srt), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
            using var probe = await VideoRuntime.ProbeAsync(output, CancellationToken.None);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.AreEqual(1, streams.Count(s => s.GetProperty("codec_type").GetString() == "video"));
            Assert.AreEqual(1, streams.Count(s => s.GetProperty("codec_type").GetString() == "audio"));
            Assert.AreEqual(extension == ".mkv" ? 2 : 1, streams.Count(s => s.GetProperty("codec_type").GetString() == "subtitle"));
            Assert.AreEqual(srt ? 2 : 0, Directory.GetFiles(root, "результат*.srt").Length);
            if (extension == ".mkv") { var imported = await VideoImport.ReadAsync(output, root, CancellationToken.None); Assert.HasCount(2, imported.SubtitleStreams); Assert.IsFalse(imported.SubtitleStreams.First(s => s.Name.Contains("forced")).Forced); Assert.IsTrue(imported.SubtitleStreams.First(s => s.Name.Contains("short")).Forced); }
            Assert.AreEqual(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            Assert.IsTrue(requested.Contains("video.ffmpeg")); Assert.IsTrue(requested.Contains("svg-renderer"));
            var frame = Path.Combine(root, "frame.rgba"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-i", output, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", frame], CancellationToken.None);
            var pixels = await File.ReadAllBytesAsync(frame); var red = (40 * 320 + 220) * 4; var blue = (100 * 320 + 100) * 4;
            Assert.IsTrue(pixels[red] > 180 && pixels[red + 2] < 80, "SVG must remain a red overlay in final output");
            Assert.IsTrue(pixels[blue + 2] > 180 && pixels[blue] < 80, "Underlying video must stay blue");
            if (extension == ".mp4")
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var cancelled = Path.Combine(root, "cancelled.mp4"); var freeze = p.Clone(); freeze.Tracks[0].Clips[0].Freeze = true; freeze.Tracks[0].Clips[0].Duration = 60;
                await Assert.ThrowsAsync<OperationCanceledException>(() => VideoExport.RunAsync(freeze, cancelled, new(3840, 2160, 30, 0, 60, false, false), Guid.NewGuid().ToString("N"), null, cancel.Token));
                Assert.IsFalse(File.Exists(cancelled)); Assert.AreEqual(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
