using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverArtTests
{
    [TestMethod]
    public void EmbeddedArtworkMatchesTheUserImageAndIsSquare()
    {
        var cover = MusicCoverArt.Default;
        Assert.AreEqual("A7A1BE6A97A17EAD5056C674CEE7A35724A632AE9CB1CC164F1C8E57436B361E",
            Convert.ToHexString(SHA256.HashData(cover.Data)));
        Assert.AreEqual("image/png", cover.Mime); Assert.AreEqual((byte)3, cover.Type);
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(cover.Data));
        Assert.AreEqual(1024, codec.Info.Width); Assert.AreEqual(1024, codec.Info.Height);
    }

    [TestMethod]
    [DataRow(MusicAudioFormat.Mp3)]
    [DataRow(MusicAudioFormat.Opus)]
    [DataRow(MusicAudioFormat.Flac)]
    [DataRow(MusicAudioFormat.Wav)]
    public async Task NativeOutputKeepsArtworkTagsAndTimedLyrics(MusicAudioFormat format)
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "source.wav"); MusicOutputTests.WriteWave(source);
            var sourceHash = await MusicOutputPublisher.HashAsync(source, default);
            var output = Path.Combine(root, "song" + MusicOutputSettings.Extension(format));
            var tags = new Dictionary<string, string> { ["title"] = "Song 01 — ё", ["artist"] = "LOPATA 02",
                ["lyrics"] = "Припев", ["LOPATA_TEST"] = "=;#\nNext" };
            await MusicAudioEncoder.Default.EncodeAsync(source, output, format, 160, tags, default);
            await AssertCover(output);
            var found = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, output, default);
            foreach (var tag in tags) Assert.AreEqual(tag.Value, found.Tags[tag.Key]);
            Assert.AreEqual(48000, found.SampleRate); Assert.AreEqual(2, found.Channels);
            Assert.AreEqual(sourceHash, await MusicOutputPublisher.HashAsync(source, default));
            var payload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(output, default);
            var timed = Path.Combine(root, "timed" + MusicOutputSettings.Extension(format));
            var timing = new MusicTimedLyrics { JobId = Guid.NewGuid().ToString("N"), Variant = 0,
                SourceLyrics = "Припев", LyricsHash = MusicTimedLyrics.HashLyrics("Припев"), AudioPayloadHash = payload,
                DurationSeconds = 1, Provider = "test", ProviderVersion = "1", Lines = [new(0, 6, .1, .8)] };
            await MusicLyricsTimingMetadata.WriteCopyAsync(output, timed, timing, default);
            await AssertCover(timed);
            Assert.AreEqual(payload, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(timed, default));
            Assert.IsNotNull(await MusicLyricsTimingMetadata.ReadAsync(timed, default));
            var converted = await new AudioConversionService().ConvertAsync(timed, root, MusicAudioFormat.Flac, 160, default);
            await AssertCover(converted.OutputPath);
            Assert.IsFalse(converted.ArchivedOnly.Contains("cover"));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tags").Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(MusicAudioFormat.Mp3)]
    [DataRow(MusicAudioFormat.Flac)]
    [DataRow(MusicAudioFormat.Wav)]
    public async Task AttachingArtworkDoesNotChangeAudioPayloadOrTextTags(MusicAudioFormat format)
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "source.wav"); MusicOutputTests.WriteWave(source);
            var path = Path.Combine(root, "bare" + MusicOutputSettings.Extension(format));
            var runtime = MusicAudioRuntime.Default; await runtime.PrepareAsync(default);
            var codec = format switch { MusicAudioFormat.Mp3 => "libmp3lame", MusicAudioFormat.Flac => "flac", _ => "pcm_s16le" };
            var args = new List<string> { "-nostdin", "-v", "error", "-n", "-i", source, "-map", "0:a:0", "-map_metadata", "-1", "-c:a", codec };
            if (format == MusicAudioFormat.Mp3) args.AddRange(["-id3v2_version", "0", "-write_id3v1", "0"]);
            args.AddRange(["-f", format.ToString().ToLowerInvariant(), path]);
            await runtime.RunAsync(false, args, default);
            var before = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, default);
            await AudioMetadataWriter.AttachAsync(path, format, new Dictionary<string, string> { ["title"] = "Title" }, [MusicCoverArt.Default], default);
            Assert.AreEqual(before, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, default));
            await AssertCover(path);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MainAndAdditionalOutputsShareCoverAndResumeDoesNotRewriteThem()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var worker = new Worker();
            var settings = new MusicExpertSettings(); settings.Values["cot"] = 2;
            var job = jobs.Create(root, Path.Combine(root, "output"), "Song 01", 1, 30, "", "", expert: settings,
                outputSettings: new() { Format = MusicAudioFormat.Opus, AdditionalFormat = MusicAudioFormat.Mp3 }, artist: "LOPATA 02");
            var runner = new MusicGenerationRunner(jobs, worker);
            await runner.RunAsync(job.Id, default);
            var completed = await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
            var variant = completed.Variants[0];
            await AssertCover(variant.ResultPath); await AssertCover(variant.AdditionalPath!);
            await runner.RunAsync(job.Id, default);
            var resumed = jobs.Load(job.Id).Variants[0];
            Assert.AreEqual(1, worker.Syntheses);
            Assert.AreEqual(variant.ResultHash, resumed.ResultHash); Assert.AreEqual(variant.AdditionalHash, resumed.AdditionalHash);
            await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task AssertCover(string path)
    {
        var found = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, path, default);
        Assert.HasCount(1, found.Covers); var cover = found.Covers[0];
        Assert.AreEqual((byte)3, cover.Type); Assert.AreEqual("image/png", cover.Mime);
        CollectionAssert.AreEqual(MusicCoverArt.Default.Data, cover.Data);
    }

    private sealed class Worker : IMusicYueWorker
    {
        public event Action<string>? Log { add { } remove { } }
        public int Syntheses { get; private set; }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) => throw new InvalidOperationException();
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { Syntheses++; MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
}
