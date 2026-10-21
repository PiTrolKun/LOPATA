using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAudioTaskTests
{
    [TestMethod]
    public void LegacyProjectIsOffAndSnapshotDoesNotShareMutableReferences()
    {
        var old = JsonSerializer.Deserialize<MusicProjectSnapshot>("{}")!; Assert.IsFalse(old.AudioTask.Enabled);
        var task = new MusicAudioTask { References = [new(Path.GetFullPath("example.wav"))] }; var copy = task.Snapshot();
        task.References[0] = task.References[0] with { Enabled = false }; Assert.IsTrue(copy.References[0].Enabled);
        new MusicAudioTask { Enabled = true, Mode = MusicAudioTaskMode.Restore, Source = new(Path.GetFullPath("source.wav")) }.Validate(MusicHeartMuLaCatalog.Variation);
        Assert.Throws<InvalidDataException>(() => new MusicAudioTask { Enabled = true }.Validate());
        Assert.Throws<InvalidDataException>(() => (copy with { Enabled = true }).Validate(MusicHeartMuLaCatalog.Variation));
    }
    [TestMethod]
    public void RepaintPreservesSamplesOutsideTheCrossfadeAndNeverChangesSource()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var source = Wave(files.Root, "source", 1200); var fresh = Wave(files.Root, "fresh", -700); var original = File.ReadAllBytes(source);
        var target = Path.Combine(files.Root, "result.wav"); MusicAudioTaskDsp.Splice(source, fresh, target, .3, .6, .05, default);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        var before = MusicPcm16.Read(source); var after = MusicPcm16.Read(target);
        CollectionAssert.AreEqual(before[..48000], after[..48000]); CollectionAssert.AreEqual(before[124800..], after[124800..]);
        Assert.AreEqual((short)-700, BinaryPrimitives.ReadInt16LittleEndian(after.AsSpan(48000 * 2, 2)));
    }
    [TestMethod]
    public void RetainedLayerIsNotNormalisedAndSilentNewLayerIsExactlyNeutral()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var source = Wave(files.Root, "source", 1700); var silence = Wave(files.Root, "silence", 0); var output = Path.Combine(files.Root, "mix.wav");
        MusicAudioTaskDsp.MixKeeping(source, silence, output, default);
        CollectionAssert.AreEqual(MusicPcm16.Read(source), MusicPcm16.Read(output));
    }
    [TestMethod]
    public async Task RestoreUsesNoGeneratorAndHasHonestMetadata()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Process", 1, 1, "", "", expert: MusicAceCatalog.Defaults());
        var input = await Input(jobs.Folder(job.Id)); var original = File.ReadAllBytes(input.Path);
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.Restore, Source = input, Normalize = true } }; jobs.Save(job);
        var synth = new Synth(); await new MusicGenerationRunner(jobs, synth).RunAsync(job.Id, default);
        Assert.AreEqual(0, synth.Calls); var done = jobs.Load(job.Id); Assert.IsTrue(done.Variants[0].Completed);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(input.Path));
        var tags = MusicSongMetadata.Create(done, done.Variants[0], 0); Assert.IsFalse(tags.ContainsKey("LOPATA_MODEL"));
        StringAssert.Contains(tags["LOPATA_AUDIO_PROCESSING"], "not declipping");
    }
    [TestMethod]
    public async Task FailedPublishingResumesWithoutAnotherCoverSynthesis()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Cover", 1, 1, "folk", "Words", expert: MusicAceCatalog.Defaults(), outputSettings: new() { Format = MusicAudioFormat.Wav });
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.Cover, Source = await Input(jobs.Folder(job.Id)) } }; jobs.Save(job);
        var synth = new Synth(); var encoder = new Encoder(); var runner = new MusicGenerationRunner(jobs, synth, encoder);
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(job.Id, default));
        Assert.IsNotNull(jobs.Load(job.Id).Variants[0].AudioTask?.RenderHash);
        await runner.RunAsync(job.Id, default); Assert.AreEqual(1, synth.Calls); Assert.IsTrue(jobs.Load(job.Id).Variants[0].Completed);
    }
    [TestMethod]
    public async Task StudioReceivesSavedSourceScoreWithoutPlanningItAgain()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var expert = MusicModelVariants.Defaults(MusicStudioRuntime.Variation); expert.Values["cot"] = 1;
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Cover", 1, 1, "folk", "Words", expert: expert);
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.Cover, Source = await Input(jobs.Folder(job.Id)) } }; jobs.Save(job);
        var synth = new Synth(); var score = new Score(); await new MusicGenerationRunner(jobs, synth, coverScoreWorker: score).RunAsync(job.Id, default);
        Assert.AreEqual(0, synth.Plans); Assert.AreEqual(1, synth.Calls); Assert.IsNotNull(synth.Last?.Abc);
        StringAssert.Contains(synth.Last!.Abc!, "K:C");
    }
    [TestMethod]
    public async Task FailedSecondSeparationKeepsTheFirstReceiptAndRenderedAudio()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Keep checkpoint", 1, 1, "folk", "Words", expert: MusicAceCatalog.Defaults());
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.ReplaceVoice, Source = await Input(jobs.Folder(job.Id)), SourceLyrics = "Words" } }; jobs.Save(job);
        var synth = new Synth(); await Assert.ThrowsAsync<IOException>(() => new MusicGenerationRunner(jobs, synth, stemsWorker: new InterruptedStems()).RunAsync(job.Id, default));
        var receipt = jobs.Load(job.Id).Variants[0].AudioTask!;
        Assert.IsNotNull(receipt.RenderHash); Assert.IsNotNull(receipt.OriginalStems); Assert.IsNull(receipt.RenderedStems); Assert.AreEqual(1, synth.Calls);
    }
    [TestMethod]
    public async Task AceKeepsAllReferencesAndDoesNotTurnReferenceOnlyIntoCover()
    {
        using var files = new MusicProjectTests.Files(); var input = await Input(files.Root);
        var task = new MusicAudioTask { Enabled = true, Mode = MusicAudioTaskMode.NewSong, References = [input, input with { Role = MusicReferenceRole.Voice }] };
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(new("jazz", "Words", 12, 13, 30) { Expert = MusicAceCatalog.Defaults(), AudioTask = task })));
        Assert.AreEqual("text2music", body.RootElement.GetProperty("params").GetProperty("task_type").GetString());
        Assert.AreEqual(2, body.RootElement.GetProperty("audio_task").GetProperty("references").GetArrayLength());
        Assert.IsFalse(JsonSerializer.Serialize(task.PublicReceipt()).Contains(files.Root));
        await File.AppendAllTextAsync(input.Path, "change"); await Assert.ThrowsAsync<InvalidDataException>(() => MusicAudioTaskFiles.VerifyAsync(input, default));
    }
    [TestMethod]
    public void LibraryOfferLookupDoesNotCreateDatabaseAndFindsExistingText()
    {
        using var files = new MusicProjectTests.Files(); var store = new LyricLibraryStore(files.Root);
        Assert.IsFalse(store.ContainsText("hello")); Assert.IsFalse(Directory.Exists(files.Root));
        store.Save(new() { Title = "Song", Text = "first\nsecond" }); Assert.IsTrue(store.ContainsText("first\r\nsecond")); Assert.IsFalse(store.ContainsText("different"));
    }
    [TestMethod]
    public async Task CoverLicenceRefusalPreventsAnyModelDownload()
    {
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (ids, _) => { Assert.Contains(MusicCoverScoreCatalog.LicenseId, ids); throw new OperationCanceledException(); };
        try { await Assert.ThrowsAsync<OperationCanceledException>(() => MusicCoverScoreCatalog.PrepareAsync(true, null, default)); }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
    internal static string Wave(string folder, string name, short level)
    {
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, name + ".wav"); var data = new byte[192000];
        for (var i = 0; i < data.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i, 2), level);
        MusicPcm16.Write(path, data); return path;
    }
    private static async Task<MusicAudioInput> Input(string folder)
    {
        var path = Wave(Path.Combine(folder, "audio-inputs"), Guid.NewGuid().ToString("N"), 1000);
        return new(path) { Name = "example.wav", Sha256 = await MusicAudioTaskFiles.HashAsync(path, default), End = 1 };
    }
    private sealed class Synth : IMusicYueWorker
    {
        public int Calls, Plans; public MusicYueRequest? Last;
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) { Plans++; return File.WriteAllTextAsync(planPath, "X:1\nK:C\nC", token); }
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string output, CancellationToken token) { Calls++; Last = request; Wave(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output), 500); return Task.CompletedTask; }
    }
    private sealed class Encoder : IMusicAudioEncoder
    {
        private bool _fail = true;
        public Task EncodeAsync(string source, string target, MusicAudioFormat format, int bitrate, IReadOnlyDictionary<string, string> metadata, CancellationToken token) {
            if (_fail) { _fail = false; throw new IOException("Simulated write failure"); }
            File.Copy(source, target); return Task.CompletedTask;
        }
    }
    private sealed class InterruptedStems : IMusicStemsWorker
    {
        private int _calls;
        public async Task<MusicStemReceipt> SeparateAsync(string source, string hash, string folder, Action<string> log, CancellationToken token) {
            if (++_calls == 2) throw new IOException("Simulated second separation failure");
            var voice = Wave(folder, "voice", 200); var music = Wave(folder, "music", 300);
            return new(voice, music, await MusicAudioTaskFiles.HashAsync(voice, token), await MusicAudioTaskFiles.HashAsync(music, token), hash, "fixture", "cpu", 1);
        }
    }
    private sealed class Score : IMusicCoverScoreWorker
    { public Task<string> TranscribeAsync(string root, string audio, bool harmony, Action<string> log, CancellationToken token) => Task.FromResult("X:1\nM:4/4\nQ:120\nK:C\nCDEF|\n"); }
}
