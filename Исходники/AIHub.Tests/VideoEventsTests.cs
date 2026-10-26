using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoEventsTests
{
    private static VideoProject Sample()
    {
        var p = new VideoProject(); VideoImport.Insert(p, new() { Path = Path.GetFullPath("events-source.mp4"), Duration = 30, Kind = "video", Width = 1920, Height = 1080, VideoStream = 0, AudioStream = 1 }, 5);
        foreach (var c in p.Tracks.SelectMany(t => t.Clips)) { c.SourceIn = 2; c.Duration = 6; c.Speed = 2; }
        return p;
    }
    private static List<NarrationSegment> Events() => [new() { Start = 0, End = 6 }, new() { Start = 6, End = 10 }, new() { Start = 10, End = 30 }];
    [TestMethod]
    public void ScreenChangesGroupAnimationsWithoutInventingCutsInStaticVideo()
    {
        var events = VideoEventDetection.Segment(10, [new(0, 0), new(2, .01), new(2.2, .02), new(3.1, .01), new(6, .2), new(6.2, .02)]);
        CollectionAssert.AreEqual(new[] { 0d, 2, 6 }, events.Select(n => n.Start).ToArray()); Assert.AreEqual(10, events[^1].End);
        Assert.HasCount(1, VideoEventDetection.Segment(10, [new(1, .0001), new(8, .0001)]));
    }
    [TestMethod]
    public void EventCutsSplitLinkedAudioPreserveFramingAndMapSpeedAndSourceIn()
    {
        var p = Sample(); var first = p.Tracks[0].Clips[0]; first.Scale = 2; first.X = 100; first.CropLeft = .1; first.Rotation = 5;
        p.Narration.Add(new() { Start = 5, End = 11 });
        VideoEventCuts.Apply(p, first.Id, Events()); p.Validate();
        foreach (var t in p.Tracks) {
            Assert.HasCount(3, t.Clips); CollectionAssert.AreEqual(new[] { 5d, 7, 9 }, t.Clips.Select(c => c.Start).ToArray());
            CollectionAssert.AreEqual(new[] { 2d, 6, 10 }, t.Clips.Select(c => c.SourceIn).ToArray());
        }
        foreach (var c in p.Tracks[0].Clips) { Assert.AreEqual(2, c.Scale); Assert.AreEqual(100, c.X); Assert.AreEqual(.1, c.CropLeft); Assert.AreEqual(5, c.Rotation); }
        CollectionAssert.AreEqual(new[] { 5d, 7, 9 }, p.Narration.Select(n => n.Start).ToArray());
        Assert.IsFalse(string.IsNullOrWhiteSpace(p.Narration[0].Caption));
        foreach (var c in p.Tracks[0].Clips.ToArray()) VideoEventCuts.Apply(p, c.Id, Events());
        Assert.HasCount(3, p.Tracks[0].Clips); Assert.HasCount(3, p.Narration);
    }
    [TestMethod]
    public void RedetectionKeepsUserTextAndNeverSplitsHalfOfALockedPair()
    {
        var p = Sample(); var clip = p.Tracks[0].Clips[0]; var note = new NarrationSegment { Start = 5, End = 11, Text = "Мой отредактированный текст" }; p.Narration.Add(note);
        VideoEventCuts.Apply(p, clip.Id, Events()); Assert.HasCount(1, p.Narration); Assert.AreEqual(note.Text, p.Narration[0].Text); Assert.AreEqual(note.Id, p.Narration[0].Id);
        p = Sample(); p.Tracks[1].Locked = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => VideoEventCuts.Apply(p, p.Tracks[0].Clips[0].Id, Events()));
        Assert.HasCount(1, p.Tracks[0].Clips); Assert.HasCount(1, p.Tracks[1].Clips);
    }
    [TestMethod]
    public async Task ExplicitExistingProjectCanBeRepairedWithoutTouchingItsRecoveryFile()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVENT_PROJECT"); if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Explicit legacy project copy required.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVENT_EVIDENCE") ?? Path.GetTempPath(), "legacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            var original = await File.ReadAllBytesAsync(path!); var p = VideoProjectStore.Load(path!); var originalId = p.Id; p.Id = Guid.NewGuid().ToString("N");
            var previousClips = p.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count); var previousDuration = p.Duration;
            var snapshot = Path.Combine(root, "input.lopataVideo"); VideoProjectStore.Save(p, snapshot);
            var result = await new VideoWork().RunAsync(new(Guid.NewGuid().ToString("N"), snapshot, "detect"), CancellationToken.None);
            var repaired = VideoProjectStore.Load(result); Assert.IsTrue(repaired.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count) > previousClips);
            Assert.AreEqual(previousDuration, repaired.Duration, .001); CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path!));
            File.Copy(result, Path.Combine(root, "repaired-copy.lopataVideo"), true);
            await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { OriginalProjectId = originalId, SeparateCopyId = repaired.Id, BeforeVideoClips = previousClips, AfterVideoClips = repaired.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count), TextFragments = repaired.Narration.Count, DurationUnchanged = true, OriginalRecoveryUnchanged = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public async Task ExplicitRecordingNowHasVisibleBoundariesAndTimelineCutsOnEveryImport()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVENT_RECORDING"); if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Explicit user-authorized recording required.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVENT_EVIDENCE") ?? Path.GetTempPath(), "recording-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path!));
            await VideoRuntime.PrepareAsync(CancellationToken.None); var a = await VideoImport.ReadAsync(path!, root, CancellationToken.None);
            var events = await VideoAnalysis.DetectAsync(a, Path.Combine(root, "detection"), CancellationToken.None);
            Assert.IsTrue(events.Count >= 5, "The UI recording must not remain a single interval");
            Assert.IsTrue(events.Any(n => n.Start is > 4 and < 7), "Opening the first section must be recognized");
            var p = new VideoProject(); VideoImport.Insert(p, a, 0); VideoEventCuts.Apply(p, p.Tracks[0].Clips[0].Id, events);
            var firstCount = p.Tracks[0].Clips.Count;
            var b = await VideoImport.ReadAsync(path!, root, CancellationToken.None); VideoImport.Insert(p, b, a.Duration);
            var secondClip = p.Tracks.Where(t => t.Kind == "video").SelectMany(t => t.Clips).Single(c => c.AssetId == b.Id);
            VideoEventCuts.Apply(p, secondClip.Id, events); p.Validate();
            Assert.AreEqual(firstCount * 2, p.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count));
            Assert.AreEqual(firstCount * 2, p.Narration.Count); Assert.AreEqual(a.Duration * 2, p.Tracks.Where(t => t.Kind == "video").SelectMany(t => t.Clips).Sum(c => c.Duration), .001);
            var project = Path.Combine(root, "verified-import.lopataVideo"); VideoProjectStore.Save(p, project); var reopened = VideoProjectStore.Load(project); Assert.AreEqual(p.Narration.Count, reopened.Narration.Count);
            var requestPath = Path.Combine(root, "redetection-input.lopataVideo"); VideoProjectStore.Save(reopened, requestPath);
            var result = await new VideoWork().RunAsync(new(Guid.NewGuid().ToString("N"), requestPath, "detect"), CancellationToken.None);
            var redetected = VideoProjectStore.Load(result); Assert.AreEqual(p.Narration.Count, redetected.Narration.Count); Assert.AreEqual(firstCount * 2, redetected.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count));
            CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path!)));
            await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { Detector = VideoEventDetection.Revision, FragmentsPerImport = firstCount, VideoClips = p.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count), TextFragments = p.Narration.Count, RedetectionPreservesCuts = true, OriginalUnchanged = true, Sha256 = Convert.ToHexString(before), Boundaries = events.Select(n => n.Start).Append(a.Duration).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
