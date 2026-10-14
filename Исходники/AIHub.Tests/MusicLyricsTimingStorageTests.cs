using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLyricsTimingStorageTests
{
    [TestMethod]
    public void OccurrencesKeepRepeatedChorusAndRejectInvalidOrOverlappingTimes()
    {
        var value = Timing("Припев\nПрипев", new string('A', 64)); value.Validate();
        Assert.IsNull(value.FindLine(.5)); Assert.AreEqual(0, value.FindLine(1.5)!.SourceStart);
        Assert.IsNull(value.FindLine(2.5)); Assert.AreEqual(7, value.FindLine(3.5)!.SourceStart);
        Assert.Throws<InvalidDataException>(() => (value with { Lines = [new(0, 6, double.NaN, 2)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (value with { Lines = [new(0, 6, 1, 3), new(7, 6, 2, 4)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (value with { Lines = [new(0, 6, 1, 2), new(0, 6, 3, 4)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (value with { SourceLyrics = "Другой текст" }).Validate());
        Assert.AreEqual(MusicTimedLyrics.HashLyrics("a\nb"), MusicTimedLyrics.HashLyrics("a\r\nb"));
    }
    [TestMethod]
    public async Task CacheSurvivesPlayerLockAndJobLoadCompletesDeferredTransaction()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var job = jobs.Create(root, root, "test", 1, 30, "", "Припев\nПрипев");
            Wave(job.Variants[0].ResultPath); var hash = await MusicOutputPublisher.HashAsync(job.Variants[0].ResultPath, CancellationToken.None);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = hash, DurationSeconds = 5 }] }; jobs.Save(job);
            var timing = Timing(job.Lyrics, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(job.Variants[0].ResultPath, CancellationToken.None)) with { JobId = job.Id };
            var store = new MusicLyricsTimingStore(jobs);
            using (var playing = new FileStream(job.Variants[0].ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                await store.PublishAsync(job.Id, 0, timing, CancellationToken.None);
                Assert.AreEqual(hash, await MusicOutputPublisher.HashAsync(job.Variants[0].ResultPath, CancellationToken.None));
                Assert.IsNotNull(await store.ReadAsync(jobs.Load(job.Id), 0, CancellationToken.None));
                Assert.AreEqual(1, Directory.GetFiles(jobs.Folder(job.Id), "*.transaction.json").Length);
            }
            var recovered = jobs.Load(job.Id); Assert.IsNotNull(recovered.Variants[0].ResultHash);
            await MusicOutputPublisher.MatchHashAsync(recovered.Variants[0].ResultPath, recovered.Variants[0].ResultHash!, CancellationToken.None);
            Assert.AreEqual(0, Directory.GetFiles(jobs.Folder(job.Id), "*.transaction.json").Length);
            Assert.IsNotNull(await store.ReadAsync(recovered, 0, CancellationToken.None));
            var metadata = await MusicLyricsTimingMetadata.ReadAsync(recovered.Variants[0].ResultPath, CancellationToken.None);
            Assert.AreEqual(timing.LyricsHash, metadata!.LyricsHash);
            Assert.IsNull(await store.ReadAsync(recovered with { Lyrics = "Иные слова" }, 0, CancellationToken.None));
            var audio = File.ReadAllBytes(recovered.Variants[0].ResultPath); audio[44] ^= 1; File.WriteAllBytes(recovered.Variants[0].ResultPath, audio);
            Assert.IsNull(await store.ReadAsync(recovered, 0, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PublishAsync(job.Id, 0, timing, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task CancelledCopyNeverReplacesOriginalAndNeverPublishesPartialCache()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var job = jobs.Create(root, root, "cancel", 1, 30, "", "Припев\nПрипев");
            Wave(job.Variants[0].ResultPath); var hash = await MusicOutputPublisher.HashAsync(job.Variants[0].ResultPath, CancellationToken.None);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = hash }] }; jobs.Save(job);
            var value = Timing(job.Lyrics, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(job.Variants[0].ResultPath, CancellationToken.None)) with { JobId = job.Id };
            await Assert.ThrowsAsync<OperationCanceledException>(() => new MusicLyricsTimingStore(jobs).PublishAsync(job.Id, 0, value, new CancellationToken(true)));
            Assert.AreEqual(hash, await MusicOutputPublisher.HashAsync(job.Variants[0].ResultPath, CancellationToken.None));
            Assert.AreEqual(0, Directory.GetFiles(jobs.Folder(job.Id), "lyrics-timing-*").Length);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task CrashAfterFileReplaceRecoversReceiptWithoutAcceptingForeignStage()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var job = jobs.Create(root, root, "crash", 1, 30, "", "Припев\nПрипев");
            var path = job.Variants[0].ResultPath; Wave(path); var oldHash = await MusicOutputPublisher.HashAsync(path, CancellationToken.None);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = oldHash }] }; jobs.Save(job);
            var value = Timing(job.Lyrics, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, CancellationToken.None)) with { JobId = job.Id };
            var nonce = Guid.NewGuid().ToString("N"); var temporary = path + ".timing-" + nonce + ".tmp"; var backup = path + ".timing-" + nonce + ".bak";
            await MusicLyricsTimingMetadata.WriteCopyAsync(path, temporary, value, CancellationToken.None);
            var newHash = await MusicOutputPublisher.HashAsync(temporary, CancellationToken.None);
            var journal = Path.Combine(jobs.Folder(job.Id), "lyrics-timing-0.transaction.json");
            File.WriteAllText(journal, JsonSerializer.Serialize(new { JobId = job.Id, Variant = 0, Additional = false,
                OriginalHash = oldHash, NewHash = newHash, TemporaryName = Path.GetFileName(temporary), BackupName = Path.GetFileName(backup) }));
            File.Replace(temporary, path, backup); // Simulate power loss before job.json receives its new hash.
            var recovered = jobs.Load(job.Id); Assert.AreEqual(newHash, recovered.Variants[0].ResultHash); Assert.IsFalse(File.Exists(journal));
            await MusicOutputPublisher.MatchHashAsync(path, newHash, CancellationToken.None);
            Assert.IsNotNull(await new MusicLyricsTimingStore(jobs).ReadAsync(recovered, 0, CancellationToken.None));
            File.WriteAllText(journal, JsonSerializer.Serialize(new { JobId = job.Id, Variant = 0, Additional = false,
                OriginalHash = newHash, NewHash = oldHash, TemporaryName = "..\\foreign.tmp", BackupName = "..\\foreign.bak" }));
            Assert.AreEqual(newHash, jobs.Load(job.Id).Variants[0].ResultHash); Assert.AreEqual(newHash, await MusicOutputPublisher.HashAsync(path, CancellationToken.None));
            Assert.IsFalse(File.Exists(journal)); Assert.IsTrue(File.Exists(Path.Combine(jobs.Folder(job.Id), "lyrics-timing-recovery-error.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task ValidCacheWithoutEmbeddedTagsAutomaticallyRetriesPortableWrite()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var job = jobs.Create(root, root, "cache", 1, 30, "", "Припев\nПрипев");
            var path = job.Variants[0].ResultPath; Wave(path); var hash = await MusicOutputPublisher.HashAsync(path, CancellationToken.None);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = hash }] }; jobs.Save(job);
            var value = Timing(job.Lyrics, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, CancellationToken.None)) with { JobId = job.Id };
            File.WriteAllText(Path.Combine(jobs.Folder(job.Id), "lyrics-timing-0.json"), JsonSerializer.Serialize(value));
            Assert.IsNull(await MusicLyricsTimingMetadata.ReadAsync(path, CancellationToken.None));
            Assert.IsNotNull(await new MusicLyricsTimingStore(jobs).ReadAsync(job, 0, CancellationToken.None));
            Assert.IsNotNull(await MusicLyricsTimingMetadata.ReadAsync(path, CancellationToken.None));
            var receipt = jobs.Load(job.Id).Variants[0].ResultHash!; await MusicOutputPublisher.MatchHashAsync(path, receipt, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => MusicOutputPublisher.MatchHashAsync(path, hash, CancellationToken.None));
            var fresh = await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, CancellationToken.None);
            Assert.AreEqual(receipt, fresh.Variants[0].ResultHash, "Late metadata must not make a runner's stale receipt invalidate audio.");
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task ContainerRoundTripsPreserveOriginalTagsCoverAndWholeDecodedAudio()
    {
        var root = Temporary();
        try {
            var source = Path.Combine(root, "source.wav"); Wave(source);
            using var bitmap = new SkiaSharp.SKBitmap(2, 2); bitmap.Erase(SkiaSharp.SKColors.Red);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            var cover = new AudioCover("image/png", 3, "Обложка", png.ToArray());
            var coverMetadata = AudioConversionMetadata.Id3(new Dictionary<string, string> { ["lyrics"] = "Припев\nПрипев", ["title"] = "Музыка ё", ["LOPATA_UNKNOWN_TAG"] = "custom" }, [cover]);
            using (var wave = new FileStream(source, FileMode.Open, FileAccess.ReadWrite)) { wave.Position = wave.Length;
                wave.Write("id3 "u8); using var writer = new BinaryWriter(wave, System.Text.Encoding.UTF8, leaveOpen: true); writer.Write(coverMetadata.Length); writer.Write(coverMetadata);
                if ((coverMetadata.Length & 1) != 0) writer.Write((byte)0); wave.Position = 4; writer.Write((uint)(wave.Length - 8)); }
            foreach (var format in Enum.GetValues<MusicAudioFormat>()) {
                var path = Path.Combine(root, "encoded" + MusicOutputSettings.Extension(format));
                await MusicAudioEncoder.Default.EncodeAsync(source, path, format, 160, new Dictionary<string, string> {
                    ["lyrics"] = "Припев\nПрипев", ["title"] = "Музыка ё", ["LOPATA_UNKNOWN_TAG"] = "Custom = value;#\nSecond" }, CancellationToken.None);
                await RoundTrip(path, Path.Combine(root, "timed" + MusicOutputSettings.Extension(format)), root);
            }
            // The conversion fixture carries a real APIC/Vorbis picture through all four container writers.
            foreach (var format in Enum.GetValues<MusicAudioFormat>()) {
                var converted = await new AudioConversionService().ConvertAsync(source, root, format, 160, CancellationToken.None);
                var output = Path.Combine(root, "cover-timed" + MusicOutputSettings.Extension(format));
                await RoundTrip(converted.OutputPath, output, root);
                var found = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, output, CancellationToken.None);
                Assert.IsTrue(found.Covers.Any(c => c.Data.SequenceEqual(cover.Data)), "Lost cover in " + format);
            }
            // Optional reproducible integration fixtures: copies, never the user's original files.
            if (Environment.GetEnvironmentVariable("LOPATA_KARAOKE_STORAGE_FIXTURES") is { Length: > 0 } fixtures)
                foreach (var path in Directory.GetFiles(fixtures, "real-source.*").Where(p => Path.GetExtension(p) is ".mp3" or ".flac" or ".opus"))
                    await RoundTrip(path, Path.Combine(fixtures, "real-timed" + Path.GetExtension(path)), root);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task DuplicateFormatsGetTheirOwnVerifiedClockAndReceipt()
    {
        var root = Temporary();
        try {
            var source = Path.Combine(root, "clock-source.wav"); Wave(source); var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs"));
            var job = jobs.Create(root, root, "clock", 1, 30, "", "Припев\nПрипев", outputSettings: new() { Format = MusicAudioFormat.Opus, AdditionalFormat = MusicAudioFormat.Mp3 });
            var variant = job.Variants[0]; var runtime = MusicAudioRuntime.Default;
            await MusicAudioEncoder.Default.EncodeAsync(source, variant.ResultPath, MusicAudioFormat.Opus, 160, new Dictionary<string, string>(), CancellationToken.None);
            await MusicAudioEncoder.Default.EncodeAsync(source, variant.AdditionalPath!, MusicAudioFormat.Mp3, 160, new Dictionary<string, string>(), CancellationToken.None);
            job = job with { Variants = [variant with { Completed = true, DurationSeconds = 5,
                ResultHash = await MusicOutputPublisher.HashAsync(variant.ResultPath, CancellationToken.None),
                AdditionalHash = await MusicOutputPublisher.HashAsync(variant.AdditionalPath!, CancellationToken.None) }] }; jobs.Save(job);
            var timing = Timing(job.Lyrics, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.ResultPath, CancellationToken.None)) with { JobId = job.Id };
            var store = new MusicLyricsTimingStore(jobs); await store.PublishAsync(job.Id, 0, timing, CancellationToken.None);
            var mapped = await new MusicLyricsTimingClock(runtime).MapAsync(variant.ResultPath, variant.AdditionalPath!, timing, root, CancellationToken.None);
            Assert.IsNotNull(mapped); Assert.IsNotNull(mapped.Clock); Assert.IsTrue(Math.Abs(mapped.Clock.OffsetSeconds) < .03);
            Assert.AreNotEqual(timing.AudioPayloadHash, mapped.AudioPayloadHash);
            await store.PublishAdditionalAsync(job.Id, 0, mapped, CancellationToken.None); var ready = jobs.Load(job.Id);
            await MusicOutputPublisher.MatchHashAsync(variant.AdditionalPath!, ready.Variants[0].AdditionalHash!, CancellationToken.None);
            Assert.IsNotNull(await store.ReadAdditionalAsync(ready, 0, CancellationToken.None));
            Assert.IsNotNull(await MusicLyricsTimingMetadata.ReadAsync(variant.AdditionalPath!, CancellationToken.None));
            var shifted = Path.Combine(root, "shifted.wav"); var original = File.ReadAllBytes(source); const int padding = 48000;
            var shiftedBytes = new byte[original.Length + padding]; original.AsSpan(0, 44).CopyTo(shiftedBytes); original.AsSpan(44).CopyTo(shiftedBytes.AsSpan(44 + padding));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(shiftedBytes.AsSpan(4), shiftedBytes.Length - 8);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(shiftedBytes.AsSpan(40), shiftedBytes.Length - 44); File.WriteAllBytes(shifted, shiftedBytes);
            var shiftedTiming = await new MusicLyricsTimingClock(runtime).MapAsync(variant.ResultPath, shifted, timing, root, CancellationToken.None);
            Assert.IsNotNull(shiftedTiming); Assert.AreEqual(.25, shiftedTiming.Clock!.OffsetSeconds, .005);
            Assert.AreEqual(1.25, shiftedTiming.Lines[0].StartSeconds, .005);
            var unrelated = Path.Combine(root, "other.wav"); Wave(unrelated);
            var bytes = File.ReadAllBytes(unrelated); var random = new Random(999);
            for (var i = 44; i < bytes.Length; i++) bytes[i] = (byte)random.Next(256); File.WriteAllBytes(unrelated, bytes);
            Assert.IsNull(await new MusicLyricsTimingClock(runtime).MapAsync(variant.ResultPath, unrelated, timing, root, CancellationToken.None));
            if (Environment.GetEnvironmentVariable("LOPATA_KARAOKE_STORAGE_FIXTURES") is { Length: > 0 } fixtures) {
                var realMain = Path.Combine(fixtures, "real-source.flac"); var realAdditional = Path.Combine(fixtures, "real-pair.opus");
                if (File.Exists(realMain) && File.Exists(realAdditional)) {
                    var mainTags = await Probe(realMain); var additionalTags = await Probe(realAdditional);
                    Assert.AreEqual(mainTags["LOPATA_JOB"], additionalTags["LOPATA_JOB"], "The real fixture must be the same generation.");
                    Assert.AreEqual(mainTags["track"], additionalTags["track"]);
                    Assert.AreEqual(mainTags["LOPATA_SEED"], additionalTags["LOPATA_SEED"]);
                    var realTiming = Timing("Припев\nПрипев", await MusicLyricsTimingMetadata.AudioPayloadHashAsync(realMain, CancellationToken.None));
                    var clock = await new MusicLyricsTimingClock(runtime).MapAsync(realMain, realAdditional, realTiming, root, CancellationToken.None);
                    Assert.IsNotNull(clock, "Actual paired FLAC/Opus clock mapping failed.");
                    File.WriteAllText(Path.Combine(fixtures, "real-pair-clock.json"), JsonSerializer.Serialize(new { Primary = realMain, Additional = realAdditional,
                        Generation = mainTags["LOPATA_JOB"], Clock = clock.Clock, DurationSeconds = clock.DurationSeconds, TimingAccuracyTest = false }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task OpusMultiPageCommentRetaggingPreservesAllPacketsAndChecksums()
    {
        var root = Temporary();
        try {
            var wave = Path.Combine(root, "source.wav"); Wave(wave); var opus = Path.Combine(root, "source.opus");
            await MusicAudioEncoder.Default.EncodeAsync(wave, opus, MusicAudioFormat.Opus, 160, new Dictionary<string, string>(), CancellationToken.None);
            var payload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(opus, CancellationToken.None);
            var value = Timing("Припев\nПрипев\n" + new string('я', 50000), payload);
            var first = Path.Combine(root, "first.opus"); var second = Path.Combine(root, "second.opus");
            await MusicLyricsTimingMetadata.WriteCopyAsync(opus, first, value, CancellationToken.None);
            await MusicLyricsTimingMetadata.WriteCopyAsync(first, second, value, CancellationToken.None);
            Assert.AreEqual(payload, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(second, CancellationToken.None));
            Assert.AreEqual(value.SourceLyrics, (await MusicLyricsTimingMetadata.ReadAsync(second, CancellationToken.None))!.SourceLyrics);
            await MusicAudioRuntime.Default.RunAsync(false, ["-nostdin", "-v", "error", "-xerror", "-i", second, "-map", "0:a:0", "-f", "null", "-"], CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task RoundTrip(string source, string output, string root)
    {
        if (File.Exists(output)) File.Delete(output);
        var payload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(source, CancellationToken.None);
        var value = Timing("Припев\nПрипев", payload);
        await MusicLyricsTimingMetadata.WriteCopyAsync(source, output, value, CancellationToken.None);
        Assert.AreEqual(payload, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(output, CancellationToken.None));
        var read = await MusicLyricsTimingMetadata.ReadAsync(output, CancellationToken.None); Assert.IsNotNull(read); Assert.AreEqual(2, read.Lines.Length);
        var before = await Probe(source); var after = await Probe(output);
        foreach (var tag in before) { Assert.IsTrue(after.TryGetValue(tag.Key, out var actual), "Lost " + tag.Key); Assert.AreEqual(tag.Value, actual, "Changed " + tag.Key); }
        var runtime = MusicAudioRuntime.Default;
        var beforePcm = Path.Combine(root, Guid.NewGuid() + ".wav"); var afterPcm = Path.Combine(root, Guid.NewGuid() + ".wav");
        await runtime.DecodeAsync(source, beforePcm, CancellationToken.None); await runtime.DecodeAsync(output, afterPcm, CancellationToken.None);
        CollectionAssert.AreEqual(File.ReadAllBytes(beforePcm), File.ReadAllBytes(afterPcm), "Decoded samples/duration changed.");
        File.Delete(beforePcm); File.Delete(afterPcm);
    }
    private static async Task<Dictionary<string, string>> Probe(string path)
    {
        using var json = JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true,
            ["-v", "error", "-show_entries", "stream_tags:format_tags", "-of", "json", path], CancellationToken.None));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement element) { if (element.TryGetProperty("tags", out var tags)) foreach (var tag in tags.EnumerateObject()) result[tag.Name] = tag.Value.GetString()!; }
        if (json.RootElement.TryGetProperty("format", out var format)) Collect(format);
        if (json.RootElement.TryGetProperty("streams", out var streams)) foreach (var stream in streams.EnumerateArray()) Collect(stream);
        return result;
    }
    private static MusicTimedLyrics Timing(string lyrics, string payload) => new() { JobId = new string('a', 32), Variant = 0,
        SourceLyrics = lyrics, LyricsHash = MusicTimedLyrics.HashLyrics(lyrics), AudioPayloadHash = payload, DurationSeconds = 5,
        Provider = "storage-roundtrip-fixture", ProviderVersion = "1", Lines = [new(0, 6, 1, 2), new(7, 6, 3, 4)] };
    private static string Temporary() { var path = Path.Combine(Path.GetTempPath(), "lopata-timing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Wave(string path)
    {
        using var file = File.Create(path); using var w = new BinaryWriter(file); var samples = 48000 * 5;
        w.Write("RIFF"u8); w.Write(36 + samples * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(samples * 4);
        for (var i = 0; i < samples; i++) { var sample = (short)(Math.Sin(i * .07 + i * (double)i / samples * .004) * 8000); w.Write(sample); w.Write(sample); }
    }
}
