using System.IO;
using System.Text.Json;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicOutputTests
{
    [TestMethod]
    public async Task AllFourFormatsKeepUnicodeLyricsAndGenerationMetadata()
    {
        var root = Temporary();
        try {
            var wave = Path.Combine(root, "source.wav"); WriteWave(wave);
            var runtime = MusicAudioRuntime.Default; var encoder = new MusicAudioEncoder(runtime);
            var tags = new Dictionary<string, string> { ["title"] = "Название ё = ; # \\", ["artist"] = "Исполнитель", ["comment"] = "Заметка\nВторая строка",
                ["lyrics"] = "[Verse]\r\nМолоко́ — музыка\n{Исполнитель}\rЕщё строка", ["LOPATA_SEED"] = "123456", ["LOPATA_PARAMETERS"] = "cfg_scale=1.9\nsteps=32" };
            foreach (var format in Enum.GetValues<MusicAudioFormat>()) {
                var path = Path.Combine(root, "song" + MusicOutputSettings.Extension(format));
                await encoder.EncodeAsync(wave, path, format, 160, tags, CancellationToken.None);
                using var probe = JsonDocument.Parse(await runtime.RunAsync(true, ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], CancellationToken.None));
                var found = new List<string>(); Collect(probe.RootElement);
                foreach (var tag in tags) Assert.IsTrue(found.Contains(tag.Value), format + " lost " + tag.Key + ": " + probe.RootElement);
                var decoded = Path.Combine(root, format + "-decoded.wav"); await runtime.DecodeAsync(path, decoded, CancellationToken.None);
                if (!MusicOutputSettings.Lossy(format)) CollectionAssert.AreEqual(Pcm(wave), Pcm(decoded), format + " changed PCM samples.");
                void Collect(JsonElement item) {
                    if (item.ValueKind == JsonValueKind.String) found.Add(item.GetString()!);
                    else if (item.ValueKind == JsonValueKind.Object) foreach (var field in item.EnumerateObject()) Collect(field.Value);
                    else if (item.ValueKind == JsonValueKind.Array) foreach (var field in item.EnumerateArray()) Collect(field);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task EveryLossyBitrateEncodesAndRefusedConsentCreatesNoOutput()
    {
        var root = Temporary(); var previousGate = ComponentLicenseGate.ConfirmAsync;
        try {
            var wave = Path.Combine(root, "source.wav"); WriteWave(wave);
            foreach (var format in new[] { MusicAudioFormat.Opus, MusicAudioFormat.Mp3 })
            foreach (var bitrate in MusicOutputSettings.Bitrates) {
                var path = Path.Combine(root, bitrate + MusicOutputSettings.Extension(format));
                await MusicAudioEncoder.Default.EncodeAsync(wave, path, format, bitrate, new Dictionary<string, string>(), CancellationToken.None);
                if (format == MusicAudioFormat.Mp3) {
                    using var probe = JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true, ["-v", "error", "-show_entries", "stream=bit_rate", "-of", "json", path], CancellationToken.None));
                    Assert.AreEqual((bitrate * 1000).ToString(), probe.RootElement.GetProperty("streams")[0].GetProperty("bit_rate").GetString());
                }
            }
            ComponentLicenseGate.ConfirmAsync = (_, _) => throw new OperationCanceledException("Consent declined.");
            var denied = Path.Combine(root, "denied.opus");
            await Assert.ThrowsAsync<OperationCanceledException>(() => MusicAudioEncoder.Default.EncodeAsync(wave, denied, MusicAudioFormat.Opus, 160,
                new Dictionary<string, string>(), CancellationToken.None));
            Assert.IsFalse(File.Exists(denied)); Assert.IsTrue(File.Exists(wave));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task DuplicateFailurePreservesPcmAndResumeDoesNotGenerateAgain()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var worker = new Worker();
            var encoder = new SecondFails(); var settings = new MusicExpertSettings(); settings.Values["cot"] = 2;
            var job = jobs.Create(root, Path.Combine(root, "out"), "Песня", 1, 30, "blues", "Слова", expert: settings,
                outputSettings: new() { AdditionalFormat = MusicAudioFormat.Flac }, artist: "Тест", comment: "Комментарий");
            var ready = 0; var runner = new MusicGenerationRunner(jobs, worker, encoder); runner.TrackReady += _ => ready++;
            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(job.Id, CancellationToken.None));
            var interrupted = jobs.Load(job.Id); Assert.AreEqual(0, ready); Assert.AreEqual(1, worker.Syntheses);
            Assert.IsFalse(interrupted.Variants[0].Completed); Assert.IsTrue(File.Exists(interrupted.Variants[0].AudioFile));
            Assert.IsTrue(File.Exists(interrupted.Variants[0].ResultPath)); Assert.IsFalse(File.Exists(interrupted.Variants[0].AdditionalPath));
            await runner.RunAsync(job.Id, CancellationToken.None);
            var completed = jobs.Load(job.Id); Assert.AreEqual(1, ready); Assert.AreEqual(1, worker.Syntheses);
            Assert.IsTrue(completed.Variants[0].Completed); Assert.IsFalse(File.Exists(completed.Variants[0].AudioFile));
            Assert.IsTrue(File.Exists(completed.Variants[0].AdditionalPath)); Assert.AreEqual(1, jobs.Tracks(job.Id).Count);
            Assert.IsNotNull(jobs.Tracks(job.Id)[0].AdditionalPath);
            await runner.RunAsync(job.Id, CancellationToken.None); Assert.AreEqual(1, worker.Syntheses);
            File.AppendAllText(completed.Variants[0].AdditionalPath!, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(job.Id, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task EncodingCancellationKeepsPcmAndRemovesOnlyItsPartialFile()
    {
        var root = Temporary();
        try {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var worker = new Worker();
            var settings = new MusicExpertSettings(); settings.Values["cot"] = 2;
            var job = jobs.Create(root, Path.Combine(root, "out"), "Отмена", 1, 30, "", "", expert: settings, outputSettings: new());
            var runner = new MusicGenerationRunner(jobs, worker, new CancelEncoding());
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, CancellationToken.None));
            var variant = jobs.Load(job.Id).Variants[0];
            Assert.IsTrue(File.Exists(variant.AudioFile)); Assert.IsFalse(File.Exists(variant.ResultPath));
            Assert.AreEqual(0, Directory.GetFiles(job.OutputFolder, "*.partial").Length);
            Assert.IsFalse(variant.Completed);
            await new MusicGenerationRunner(jobs, worker).RunAsync(job.Id, CancellationToken.None);
            Assert.AreEqual(1, worker.Syntheses); Assert.IsTrue(jobs.Load(job.Id).Variants[0].Completed);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void OutputPreferencesAndIdentityAreStrictAndDurable()
    {
        var root = Temporary();
        try {
            var preferences = new MusicOutputPreferences(Path.Combine(root, "output.json")); Assert.AreEqual(320, preferences.Load().Bitrate);
            var value = new MusicOutputSettings { Format = MusicAudioFormat.Mp3, Bitrate = 160, AdditionalFormat = MusicAudioFormat.Wav };
            preferences.Save(value); Assert.AreEqual(value, preferences.Load());
            Assert.Throws<InvalidDataException>(() => (value with { AdditionalFormat = MusicAudioFormat.Mp3 }).Validate());
            Assert.Throws<InvalidDataException>(() => (value with { Bitrate = 128 }).Validate());
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs"));
            var job = jobs.Create(root, root, "", 1, 30, "", "", outputSettings: value);
            StringAssert.StartsWith(Path.GetFileName(job.Variants[0].ResultPath), "Music_");
            Assert.AreEqual("", job.Artist); Assert.AreEqual("", job.Comment);
            var named = jobs.Create(root, root, "Название", 1, 30, "", "", outputSettings: value, artist: "Исполнитель");
            Assert.AreEqual("Исполнитель — Название.mp3", Path.GetFileName(named.Variants[0].ResultPath));
            var tags = MusicSongMetadata.Create(named, named.Variants[0], 0);
            Assert.AreEqual(named.Variants[0].SoundSeed.ToString(), tags["LOPATA_SEED"]); Assert.AreEqual("Название", tags["title"]);
            File.WriteAllText(Path.Combine(root, "output.json"), "{\"Format\":999}"); Assert.Throws<InvalidDataException>(() => preferences.Load());
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class SecondFails : IMusicAudioEncoder
    {
        private int _calls;
        public Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate, IReadOnlyDictionary<string, string> metadata, CancellationToken token)
        { if (++_calls == 2) throw new IOException("Interrupted additional encoding."); return MusicAudioEncoder.Default.EncodeAsync(source, destination, format, bitrate, metadata, token); }
    }
    private sealed class CancelEncoding : IMusicAudioEncoder
    {
        public Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate, IReadOnlyDictionary<string, string> metadata, CancellationToken token)
        { File.WriteAllText(destination, "partial"); throw new OperationCanceledException("Cancelled while encoding."); }
    }
    private sealed class Worker : IMusicYueWorker
    {
        public event Action<string>? Log { add { } remove { } }
        public int Syntheses { get; private set; }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) => throw new InvalidOperationException();
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { Syntheses++; WriteWave(outputPath); return Task.CompletedTask; }
    }
    internal static string Temporary() { var root = Path.Combine(Path.GetTempPath(), "lopata-music-output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    internal static void WriteWave(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8); file.Write(36 + 48000 * 4); file.Write("WAVEfmt "u8); file.Write(16); file.Write((short)1); file.Write((short)2);
        file.Write(48000); file.Write(192000); file.Write((short)4); file.Write((short)16); file.Write("data"u8); file.Write(48000 * 4);
        for (var i = 0; i < 48000; i++) { file.Write((short)(Math.Sin(i * .05) * 10000)); file.Write((short)(Math.Cos(i * .03) * 8000)); }
    }
    private static byte[] Pcm(string path)
    {
        using var input = new BinaryReader(File.OpenRead(path)); input.BaseStream.Position = 12;
        while (input.BaseStream.Position + 8 <= input.BaseStream.Length) {
            var id = input.ReadBytes(4); var size = input.ReadInt32(); if (id.AsSpan().SequenceEqual("data"u8)) return input.ReadBytes(size);
            input.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidDataException("Missing PCM.");
    }
}
