using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicStemsTests
{
    [TestMethod]
    public async Task SeparateVariantsPublishOneBundleEachAndPreserveRoles()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "results"), "Проверка", 2, 1, "folk", "Строка песни",
            outputSettings: new() { Format = MusicAudioFormat.Wav }, separateTracks: true);
        var synth = new Synth(); var stems = new Stems(); var encoder = new Encoder(); var tracks = new List<MusicTrack>();
        var runner = new MusicGenerationRunner(jobs, synth, encoder, stemsWorker: stems); runner.TrackReady += tracks.Add;
        await runner.RunAsync(job.Id, default);
        var ready = jobs.Load(job.Id);
        Assert.AreEqual(2, tracks.Count); Assert.AreEqual(2, synth.Count); Assert.AreEqual(2, stems.Count);
        Assert.AreEqual(2, ready.Variants.Select(v => Path.GetDirectoryName(v.ResultPath)).Distinct().Count());
        foreach (var v in ready.Variants) {
            Assert.IsTrue(MusicGenerationJobs.ResultVisible(v)); Assert.IsFalse(File.Exists(v.AudioFile));
            var folder = Path.GetDirectoryName(v.ResultPath)!;
            CollectionAssert.AreEquivalent(new[] { "voice.wav", "music.wav", "song.txt" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
            var text = await File.ReadAllTextAsync(MusicStemBundle.TextPath(v));
            StringAssert.Contains(text, "Строка песни"); StringAssert.Contains(text, "Unavailable");
            var metadata = JsonDocument.Parse(text.Split('\n')[0]["LOPATA-METADATA ".Length..]);
            Assert.AreEqual("text-and-parameters", metadata.RootElement.GetProperty("LOPATA_FILE_ROLE").GetString());
            await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, Array.IndexOf(ready.Variants, v), default);
        }
        Assert.IsTrue(tracks.All(t => t.IsLayered));
        CollectionAssert.AreEquivalent(new[] { "voice", "music", "voice", "music" }, encoder.Roles.ToArray());
        Assert.AreEqual(2, encoder.BundleIds.Distinct().Count());
        Assert.IsTrue(encoder.BundleIds.GroupBy(x => x).All(g => g.Count() == 2));
    }

    [TestMethod]
    public async Task FailedSecondRoleResumesWithoutSynthesizingOrSeparatingAgain()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Recovery", 1, 1, "rock", "Words",
            outputSettings: new() { Format = MusicAudioFormat.Wav }, separateTracks: true);
        var synth = new Synth(); var stems = new Stems(); var encoder = new Encoder { FailSecondOnce = true };
        var runner = new MusicGenerationRunner(jobs, synth, encoder, stemsWorker: stems);
        await Assert.ThrowsAsync<MusicStemsException>(() => runner.RunAsync(job.Id, default));
        var interrupted = jobs.Load(job.Id).Variants[0];
        Assert.IsFalse(MusicGenerationJobs.ResultVisible(interrupted)); Assert.IsTrue(File.Exists(interrupted.AudioFile));
        Assert.IsTrue(File.Exists(interrupted.ResultPath));
        await runner.RunAsync(job.Id, default);
        Assert.AreEqual(1, synth.Count); Assert.AreEqual(1, stems.Count);
        Assert.IsTrue(MusicGenerationJobs.ResultVisible(jobs.Load(job.Id).Variants[0]));
    }

    [TestMethod]
    public async Task OrdinaryJobNeverInvokesSeparatorAndLegacySnapshotDefaultsOff()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Normal", 1, 1, "rock", "Words", outputSettings: new() { Format = MusicAudioFormat.Wav });
        var stems = new Stems();
        await new MusicGenerationRunner(jobs, new Synth(), new Encoder(), stemsWorker: stems).RunAsync(job.Id, default);
        Assert.AreEqual(0, stems.Count); Assert.IsFalse(jobs.Tracks(job.Id).Single().IsLayered);
        Assert.IsFalse(JsonSerializer.Deserialize<MusicProjectSnapshot>("{}")!.SeparateTracks);
    }

    [TestMethod]
    public async Task FolderCopyPreservesTheWholeBundleAndRefusesOverwrite()
    {
        using var files = new MusicProjectTests.Files();
        var source = Path.Combine(files.Root, "song"); var destination = Path.Combine(files.Root, "copies");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        foreach (var name in new[] { "voice.wav", "music.wav", "song.txt" }) await File.WriteAllTextAsync(Path.Combine(source, name), name);
        await MusicStemFolder.CopyAsync(source, destination, default);
        await MusicStemFolder.CopyAsync(source, files.Root, default); // already saved here; preserve it
        Assert.AreEqual(3, Directory.GetFiles(Path.Combine(destination, "song")).Length);
        await Assert.ThrowsAsync<IOException>(() => MusicStemFolder.CopyAsync(source, destination, default));
        await Assert.ThrowsAsync<IOException>(() => MusicStemFolder.CopyAsync(source, source, default));
    }

    [TestMethod]
    public async Task MeasuredSourceTimingRefreshesTxtAndKeepsTheSharedIdentity()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Timing", 1, 1, "rock", "Words",
            outputSettings: new() { Format = MusicAudioFormat.Wav }, separateTracks: true);
        await new MusicGenerationRunner(jobs, new Synth(), new Encoder(), stemsWorker: new Stems()).RunAsync(job.Id, default);
        var variant = jobs.Load(job.Id).Variants[0]; var before = variant.Stems!.TextHash;
        var timing = new MusicTimedLyrics { JobId = job.Id, Variant = 0, SourceLyrics = "Words", LyricsHash = MusicTimedLyrics.HashLyrics("Words"),
            AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.ResultPath, default), DurationSeconds = 1,
            Provider = "fixture-measured-contract", ProviderVersion = "1", Lines = [new(0, 5, .1, .8)] };
        await MusicStemPublisher.SaveSourceTimingAsync(jobs, job.Id, 0, timing, default);
        await MusicStemPublisher.FinalizeAsync(jobs, job.Id, 0, default);
        var after = await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
        Assert.AreNotEqual(before, after.Variants[0].Stems!.TextHash);
        StringAssert.Contains(await File.ReadAllTextAsync(MusicStemBundle.TextPath(variant)), "[0.100–0.800] Words");
        Assert.AreEqual(job.Id, jobs.Tracks(job.Id).Single().JobId);
    }

    [TestMethod]
    public async Task TxtReceiptRecoversACompletedAtomicReplacementAfterAnInterruption()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "TXT recovery", 1, 1, "rock", "Words",
            outputSettings: new() { Format = MusicAudioFormat.Wav }, separateTracks: true);
        await new MusicGenerationRunner(jobs, new Synth(), new Encoder(), stemsWorker: new Stems()).RunAsync(job.Id, default);
        var v = jobs.Load(job.Id).Variants[0]; var text = MusicStemBundle.TextPath(v); var before = v.Stems!.TextHash;
        await File.AppendAllTextAsync(text, "\nRecovered timing fixture\n");
        var after = await MusicOutputPublisher.HashAsync(text, default);
        var journal = Path.Combine(jobs.Folder(job.Id), "stems-text-0.transaction.json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new { JobId = job.Id, Index = 0, Nonce = Guid.NewGuid().ToString("N"), Before = before, After = after }));
        var recovered = jobs.Load(job.Id);
        Assert.AreEqual(after, recovered.Variants[0].Stems!.TextHash); Assert.IsFalse(File.Exists(journal));
        await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
    }

    [TestMethod]
    public async Task RefusedComponentConsentPreventsSeparationPreparation()
    {
        var previous = ComponentLicenseGate.ConfirmAsync; var calls = 0;
        ComponentLicenseGate.ConfirmAsync = (ids, _) => { calls++; Assert.Contains(MusicStemsCatalog.LicenseId, ids); throw new OperationCanceledException(); };
        try { await Assert.ThrowsAsync<OperationCanceledException>(() => MusicStemsCatalog.PrepareAsync(false, null, default)); Assert.AreEqual(1, calls); }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod]
    public async Task MissingPrivateStemCanBeRebuiltWithoutNewMusicOrMixedReceipts()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Private recovery", 1, 1, "rock", "Words",
            outputSettings: new() { Format = MusicAudioFormat.Wav }, separateTracks: true);
        var synth = new Synth(); var stems = new Stems();
        var runner = new MusicGenerationRunner(jobs, synth, new Encoder { FailSecondOnce = true }, stemsWorker: stems);
        await Assert.ThrowsAsync<MusicStemsException>(() => runner.RunAsync(job.Id, default));
        var v = jobs.Load(job.Id).Variants[0]; File.Delete(v.Stems!.Receipt!.VoicePcm);
        await runner.RunAsync(job.Id, default);
        Assert.AreEqual(1, synth.Count); Assert.AreEqual(2, stems.Count);
        var complete = await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
        Assert.IsTrue(MusicGenerationJobs.ResultVisible(complete.Variants[0]));
    }

    private sealed class Synth : IMusicYueWorker
    {
        public int Count;
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string output, CancellationToken token) => File.WriteAllTextAsync(output, "X:1\nK:C\nC", token);
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string output, CancellationToken token) {
            Count++; WriteWave(output); return Task.CompletedTask;
        }
    }
    private sealed class Stems : IMusicStemsWorker
    {
        public int Count;
        public async Task<MusicStemReceipt> SeparateAsync(string source, string sourceHash, string folder, Action<string> log, CancellationToken token) {
            Count++; Directory.CreateDirectory(folder); var attempt = Path.Combine(folder, "attempt-0"); Directory.CreateDirectory(attempt);
            var voice = Path.Combine(attempt, "voice.wav"); var music = Path.Combine(attempt, "music.wav");
            File.Copy(source, voice); File.Copy(source, music);
            return new(voice, music, await MusicOutputPublisher.HashAsync(voice, token), await MusicOutputPublisher.HashAsync(music, token), sourceHash, "CPUExecutionProvider", "cpu", 1);
        }
    }
    private sealed class Encoder : IMusicAudioEncoder
    {
        public bool FailSecondOnce; private int _count;
        public List<string> Roles = []; public List<string> BundleIds = [];
        public Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate, IReadOnlyDictionary<string, string> metadata, CancellationToken token) {
            if (++_count == 2 && FailSecondOnce) { FailSecondOnce = false; throw new IOException("Simulated second-role write failure"); }
            if (metadata.TryGetValue("LOPATA_FILE_ROLE", out var role)) { Roles.Add(role); BundleIds.Add(metadata["LOPATA_SONG_BUNDLE"]); }
            File.Copy(source, destination, false); return Task.CompletedTask;
        }
    }
    private static void WriteWave(string path) {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x46464952u); w.Write(192036u); w.Write(0x45564157u); w.Write(0x20746d66u); w.Write(16u);
        w.Write((ushort)1); w.Write((ushort)2); w.Write(48000u); w.Write(192000u); w.Write((ushort)4); w.Write((ushort)16);
        w.Write(0x61746164u); w.Write(192000u); w.Write(new byte[192000]);
    }
}
