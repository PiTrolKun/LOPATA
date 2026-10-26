using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoMusicTests
{
    internal sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lopata-video-music-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    internal static void Wave(string path, double seconds, bool seam = false)
    {
        var samples = (int)(seconds * 48000); using var w = new BinaryWriter(File.Create(path));
        w.Write(0x46464952u); w.Write(36 + samples * 4); w.Write(0x45564157u); w.Write(0x20746d66u); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16); w.Write(0x61746164u); w.Write(samples * 4);
        for (var i = 0; i < samples; i++) { var sample = (short)(seam ? i < samples / 2 ? 10000 : -10000 : Math.Sin(i * Math.PI * 880 / 48000) * 6000); w.Write(sample); w.Write(sample); }
    }
    internal sealed class Worker : IMusicYueWorker
    {
        public event Action<string>? Log { add { } remove { } }
        internal int Calls, Plans; internal bool InterruptOnce; internal MusicYueRequest? Request;
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
        { request.Validate(); Plans++; return File.WriteAllTextAsync(planPath, MusicScoreTests.Abc, token); }
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Request = request; if (InterruptOnce) { InterruptOnce = false; throw new OperationCanceledException(token); } Wave(outputPath, 2); return Task.CompletedTask; }
    }
    [TestMethod]
    public async Task InterruptedMusicRetainsTheProjectAndResumesOnlyTheUnfinishedStage()
    {
        using var f = new Folder(); using var policy = ComponentAudit.UseTestStore(System.IO.Path.Combine(f.Path, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await VideoRuntime.PrepareAsync(default); var p = new VideoProject(); var before = JsonSerializer.Serialize(p);
            var work = System.IO.Path.Combine(f.Path, "interrupted"); Directory.CreateDirectory(work); var worker = new Worker { InterruptOnce = true };
            await Assert.ThrowsAsync<OperationCanceledException>(() => VideoBackgroundMusic.RunAsync(p, new(f.Path, 5, "Music"), work, _ => { }, default, worker));
            Assert.AreEqual(before, JsonSerializer.Serialize(p)); var request = worker.Request!; var plans = worker.Plans;
            var receipt = File.ReadAllText(System.IO.Path.Combine(work, "music-job.json"));
            await VideoBackgroundMusic.RunAsync(p, new(f.Path, 5, "Music"), work, _ => { }, default, worker);
            Assert.AreEqual(2, worker.Calls); Assert.AreEqual(plans, worker.Plans, "A confirmed planning stage must not run again");
            Assert.AreEqual(request.LanguageSeed, worker.Request!.LanguageSeed); Assert.AreEqual(request.SoundSeed, worker.Request.SoundSeed);
            Assert.AreEqual(receipt, File.ReadAllText(System.IO.Path.Combine(work, "music-job.json"))); Assert.AreEqual(1, p.Tracks.Single(t => t.BackgroundMusic).Clips.Count);
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public async Task BackgroundMusicUsesNativeDurationLoopsAndRetainsProvenanceOnRegenerationAndResume()
    {
        using var f = new Folder(); using var policy = ComponentAudit.UseTestStore(System.IO.Path.Combine(f.Path, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await VideoRuntime.PrepareAsync(default); var p = new VideoProject(); var work = System.IO.Path.Combine(f.Path, "operation1"); Directory.CreateDirectory(work);
            var worker = new Worker(); await VideoBackgroundMusic.RunAsync(p, new(f.Path, 5, "Фоновая музыка"), work, _ => { }, default, worker);
            Assert.AreEqual(1, worker.Calls); Assert.AreEqual(5, worker.Request!.DurationSeconds); Assert.IsTrue(worker.Request.Wishes!.Instrumental); Assert.AreEqual("", worker.Request.Lyrics); Assert.AreEqual(VideoBackgroundMusic.Style, worker.Request.Style);
            Assert.IsTrue(worker.Request.EffectiveExpert.Integer("semantic_sampling.min_tokens") <= worker.Request.SequenceLimit);
            var instrumental = await AIHub.Services.MusicScore.MusicScoreNative.InspectAsync(worker.Request.Abc, default);
            Assert.AreEqual(0, instrumental.Sheet.Notes.Vocal.Length); Assert.IsTrue(instrumental.Sheet.Notes.Ins.Length > 0);
            var track = p.Tracks.Single(t => t.BackgroundMusic); var clip = track.Clips.Single(); Assert.AreEqual(.15, track.Volume); Assert.IsTrue(clip.GeneratedMusic); Assert.AreEqual(5, clip.Duration, .03); Assert.IsTrue(clip.FadeOut > 0);
            var final = p.Asset(clip.AssetId!); Assert.AreEqual(4, final.Models.Count); p.Validate();
            using (var probe = await VideoRuntime.ProbeAsync(final.Path, default)) { var tag = probe.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().Single(i => i.Name.Equals("LOPATA_PROVENANCE", StringComparison.OrdinalIgnoreCase)).Value.GetString()!; using var facts = JsonDocument.Parse(tag); Assert.AreEqual("LOPATA", facts.RootElement.GetProperty("Program").GetString()); Assert.AreEqual(4, facts.RootElement.GetProperty("Models").GetArrayLength()); Assert.IsFalse(tag.Contains(f.Path, StringComparison.OrdinalIgnoreCase)); }
            await VideoRuntime.RunAsync(false, ["-v", "error", "-ss", "4.6", "-i", final.Path, "-t", "0.2", "-f", "s16le", "-c:a", "pcm_s16le", "-n", System.IO.Path.Combine(f.Path, "tail.raw")], default);
            Assert.IsTrue(File.ReadAllBytes(System.IO.Path.Combine(f.Path, "tail.raw")).Any(b => b != 0));
            var unchanged = JsonSerializer.Serialize(p); await VideoBackgroundMusic.RunAsync(p, new(f.Path, 5, "Фоновая музыка"), work, _ => { }, default, worker); Assert.AreEqual(1, worker.Calls); Assert.AreEqual(unchanged, JsonSerializer.Serialize(p));
            var edit = new VideoEditing(p); var previous = p.Clone(); var next = p.Clone(); var work2 = System.IO.Path.Combine(f.Path, "operation2"); Directory.CreateDirectory(work2);
            await VideoBackgroundMusic.RunAsync(next, new(f.Path, 5, "Фоновая музыка"), work2, _ => { }, default, worker);
            edit.Edit(x => { x.Assets = next.Assets; x.Tracks = next.Tracks; }); Assert.AreEqual(1, edit.Project.Tracks.Count(t => t.BackgroundMusic)); Assert.AreEqual(1, edit.Project.Tracks.Single(t => t.BackgroundMusic).Clips.Count); Assert.AreEqual(4, edit.Project.Assets.Count);
            Assert.IsTrue(previous.Assets.All(a => File.Exists(a.Path))); edit.Undo(); Assert.AreEqual(previous.Tracks.Single(t => t.BackgroundMusic).Clips.Single().AssetId, edit.Project.Tracks.Single(t => t.BackgroundMusic).Clips.Single().AssetId);
            var locked = p.Clone(); locked.Tracks.Single(t => t.BackgroundMusic).Locked = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => VideoBackgroundMusic.RunAsync(locked, new(f.Path, 5, "Music"), work2, _ => { }, default, worker));
            Assert.AreEqual(360, VideoBackgroundMusic.RequestedSeconds(900)); Assert.AreEqual(1, VideoBackgroundMusic.RequestedSeconds(.5));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public async Task ExplicitInstalledYuE2ProducesARealBackgroundTrack()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_VIDEO_MUSIC_NATIVE_ROOT");
        if (string.IsNullOrEmpty(root)) { Assert.Inconclusive("Explicit installed YuE2 smoke test required."); return; }
        var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_MUSIC_EVIDENCE") ?? throw new InvalidOperationException("Evidence path required");
        var work = System.IO.Path.Combine(evidence, "Native"); Directory.CreateDirectory(work);
        using var policy = ComponentAudit.UseTestStore(System.IO.Path.Combine(evidence, "native-audit"), false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var prepare = new VideoModelPreparation(); Assert.IsTrue(await prepare.PrepareAsync(root, new("music", []), false, null, cancel.Token), "Installed music files must be verified; this test does not download");
        await VideoRuntime.PrepareAsync(cancel.Token); var p = new VideoProject { Name = "Native background music test" };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await VideoBackgroundMusic.RunAsync(p, new(root, 40, "Фоновая музыка"), work, s => Console.WriteLine(s), cancel.Token);
        var clip = p.Tracks.Single(t => t.BackgroundMusic).Clips.Single(); var result = p.Asset(clip.AssetId!); Assert.AreEqual(40, clip.Duration, .1); Assert.IsTrue(File.Exists(result.Path));
        using var probe = await VideoRuntime.ProbeAsync(result.Path, cancel.Token); Assert.AreEqual(4, result.Models.Count); p.Validate();
        VideoProjectArchive.Save(p, System.IO.Path.Combine(evidence, "Музыка YuE2.lopataVideo"), cancel.Token);
        var score = Directory.GetFiles(work, "*.abc", SearchOption.AllDirectories).First(p => !p.EndsWith(".model.abc", StringComparison.Ordinal));
        var notes = await AIHub.Services.MusicScore.MusicScoreNative.InspectAsync(await File.ReadAllTextAsync(score, cancel.Token), cancel.Token);
        Assert.AreEqual(0, notes.Sheet.Notes.Vocal.Length); Assert.IsTrue(notes.Sheet.Notes.Ins.Length > 0);
        await File.WriteAllTextAsync(System.IO.Path.Combine(evidence, "native.json"), JsonSerializer.Serialize(new { Requested = 40, Actual = result.Duration, watch.Elapsed.TotalSeconds, result.Models, Result = result.Path, VocalNotes = notes.Sheet.Notes.Vocal.Length, InstrumentNotes = notes.Sheet.Notes.Ins.Length }), cancel.Token);
    }
    [TestMethod]
    public void LoopTileJoinsItsTailToHeadWithoutASampleJump()
    {
        using var f = new Folder(); var source = System.IO.Path.Combine(f.Path, "source.wav"); var tile = System.IO.Path.Combine(f.Path, "tile.wav"); Wave(source, 2, true); var hash = SHA256.HashData(File.ReadAllBytes(source));
        VideoMusicLoop.CreateTile(source, tile, default); Assert.AreEqual(1.5, MusicWaveFile.ReadDuration(tile).TotalSeconds, .0001);
        var bytes = File.ReadAllBytes(tile); Assert.AreEqual(BitConverter.ToInt16(bytes, 44), BitConverter.ToInt16(bytes, bytes.Length - 4));
        for (var at = bytes.Length - 24000 * 4 + 4; at < bytes.Length; at += 4) Assert.IsTrue(Math.Abs(BitConverter.ToInt16(bytes, at) - BitConverter.ToInt16(bytes, at - 4)) <= 2);
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }
    private sealed class Downloads(byte[] data) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data), RequestMessage = request }); }
    }
    [TestMethod]
    public async Task PreparationAcquiresOnlyRequestedModelsAndHonoursAuditModeAndReinstallation()
    {
        using var f = new Folder(); var data = new byte[] { 1, 2, 3, 4, 5 }; var store = new ManagedModelLibraryStore(System.IO.Path.Combine(f.Path, "library"));
        var card = new ManagedModelArtifactCard { ModelArtifactId = "test-video-model", ModelsRoot = f.Path, InstallDirectory = System.IO.Path.Combine(f.Path, "model"), Revision = "v1", IsManaged = true,
            Files = [new() { RelativePath = "weights.bin", SourceUrl = "https://test.invalid/weights.bin", SizeBytes = data.Length, Sha256 = Convert.ToHexString(SHA256.HashData(data)) }] };
        using var handler = new Downloads(data); using var client = new HttpClient(handler); using var prepare = new VideoModelPreparation(store, client, (_, _) => [card]); prepare.ParallelConnections = 3;
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            using (ComponentAudit.UseTestStore(System.IO.Path.Combine(f.Path, "audit"), false)) {
                Assert.IsFalse(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default)); Assert.AreEqual(0, handler.Calls);
                Assert.IsTrue(await prepare.PrepareAsync(f.Path, new("analyze", []), true, null, default)); Assert.IsTrue(handler.Calls > 0); Assert.AreEqual(3, prepare.ParallelConnections);
                Assert.IsTrue(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default)); var originalDate = store.Load(card.ModelArtifactId)!.LastVerifiedAt;
                var path = System.IO.Path.Combine(card.InstallDirectory, "weights.bin"); using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.IsTrue(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default));
                Assert.AreEqual(originalDate, store.Load(card.ModelArtifactId)!.LastVerifiedAt);
                File.WriteAllBytes(path, [6, 7, 8, 9, 0]);
                using (ComponentAudit.ForceFull()) Assert.IsFalse(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default));
                File.Delete(path); Assert.IsFalse(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default)); Assert.IsTrue(await prepare.PrepareAsync(f.Path, new("analyze", []), true, null, default));
            }
            using (ComponentAudit.UseTestStore(System.IO.Path.Combine(f.Path, "audit"), true)) Assert.IsTrue(await prepare.PrepareAsync(f.Path, new("analyze", []), false, null, default));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
        var music = VideoModelPreparation.Cards(f.Path, new("music", [])); Assert.AreEqual(MusicModelVariants.Cards(f.Path, MusicStudioRuntime.Variation).Count, music.Count); Assert.IsFalse(music.Any(c => c.ModelArtifactId.Contains("whisper", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(ManagedModelCatalog.OmniGammaArtifactId, VideoModelPreparation.Cards(f.Path, new("analyze", [])).Single().ModelArtifactId);
        var project = new VideoProject(); project.Narration.Add(new() { Start = 0, End = 1, Text = "RU", Provider = "kokoro-sveta" }); project.Narration.Add(new() { Start = 1, End = 2, Text = "EN", Provider = "kokoro-heart" });
        var need = AIHub.Controls.VideoEditor.VideoEditorWindow.ModelNeed(project, "speech", null, false);
        var all = VideoModelPreparation.Cards(f.Path, need); Assert.AreEqual(3, all.Count, "Two model cards and their shared runtime are required");
        Assert.Contains(ManagedModelCatalog.KokoroEnglishArtifactId, all.Select(c => c.ModelArtifactId));
        Assert.Contains(ManagedModelCatalog.KokoroRussianArtifactId, all.Select(c => c.ModelArtifactId));
        need = AIHub.Controls.VideoEditor.VideoEditorWindow.ModelNeed(project, "speech", [project.Narration[1].Id], false);
        var english = VideoModelPreparation.Cards(f.Path, need); Assert.AreEqual(2, english.Count);
        Assert.Contains(ManagedModelCatalog.KokoroEnglishArtifactId, english.Select(c => c.ModelArtifactId));
        Assert.Contains(VideoNarratorRuntime.Card(f.Path, "kokoro-heart").ModelArtifactId, english.Select(c => c.ModelArtifactId));
        Assert.DoesNotContain(ManagedModelCatalog.KokoroRussianArtifactId, english.Select(c => c.ModelArtifactId));
    }
}
