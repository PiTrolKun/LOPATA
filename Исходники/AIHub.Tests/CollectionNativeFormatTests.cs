using System.IO;
using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicMidi;
using Lopata.Collection;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class CollectionNativeFormatTests
{
    [TestMethod]
    public void ActualGifCaptureWriterRoundTripsAndDoesNotAcceptOrdinaryComments()
    {
        using var files = new Files();
        var previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        var journal = files.Path("journal");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", journal);
        try
        {
            var store = new GifFrameStore(files.Path("frames"), true);
            store.Append(Pixels(), 0); store.Append(Pixels(), 200);
            var output = store.Assemble(files.Path("neutral.gif"), default);
            Assert.AreEqual("screen-capture/gif", CollectionOriginReader.Read(output)?.Scenario);
            Assert.AreEqual(output, CollectionJournal.Read(journal).Single().Path);
            Assert.Throws<IOException>(() => store.Assemble(output, default));
            Assert.Throws<OperationCanceledException>(() => store.Assemble(files.Path("cancelled.gif"), new CancellationToken(true)));
            Assert.IsFalse(File.Exists(files.Path("cancelled.gif")));
            Assert.HasCount(1, CollectionJournal.Read(journal));
            foreach (var comment in new[] { "LOPATA", "{\"Program\":\"Another\",\"Schema\":1}", "{\"title\":\"LOPATA\"}" })
            {
                using (var stream = File.Create(files.Path("LOPATA.gif")))
                using (var writer = new GifStreamEncoder(stream, 1, 1, false, comment))
                { writer.Add([0], 1); writer.Finish(); }
                Assert.IsNull(CollectionOriginReader.Read(files.Path("LOPATA.gif")));
            }
        }
        finally { Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", previous); }
    }

    [TestMethod]
    [DataRow("mp4")]
    [DataRow("mkv")]
    [DataRow("webm")]
    public async Task ActualCaptureEncoderCommentRoundTripsForEverySupportedContainer(string format)
    {
        Assert.IsTrue(File.Exists(VideoEncoder.Executable), "Bundled capture encoder is required for this production-writer regression.");
        using var files = new Files();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var own in new[] { true, false })
        {
            var store = new VideoFrameStore(files.Path(own ? "own-frames" : "foreign-frames"),
                new ScreenCaptureSettings { VideoFormat = format, VideoFps = 15, Processing = "cpu", AudioMode = "off" });
            store.Manifest.Provenance = new OutputProvenance { Program = own ? "LOPATA" : "Another", Scenario = "screen-capture/video" };
            store.Append(Pixels(), 0); store.Append(Pixels(), 200); store.Save();
            var result = await VideoEncoder.AssembleAsync(store, files.Path((own ? "neutral." : "LOPATA.") + format), timeout.Token);
            if (own) Assert.AreEqual("screen-capture/video", CollectionOriginReader.Read(result.Path)?.Scenario);
            else Assert.IsNull(CollectionOriginReader.Read(result.Path), "Native encoded_by=LOPATA alone cannot prove origin.");
        }
    }

    [TestMethod]
    public void UserMelodyExportsAreJournalledWhileBackendPreviewWritesAreNot()
    {
        using var files = new Files();
        var previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        var journal = files.Path("journal");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", journal);
        try
        {
            var melody = new MusicMelody { Notes = [new(60, 0, 480)] };
            MusicMidiFile.Write(files.Path("backend-preview.mid"), melody);
            Assert.IsEmpty(CollectionJournal.Read(journal));
            var midi = files.Path("neutral.midi"); var project = files.Path("neutral.json");
            MusicMelodyWindow.SaveUserMelody(midi, melody, false);
            MusicMelodyWindow.SaveUserMelody(project, melody, true);
            Assert.AreEqual("music/melody", CollectionOriginReader.Read(midi)?.Scenario);
            Assert.AreEqual("music/melody", CollectionOriginReader.Read(project)?.Scenario);
            CollectionAssert.AreEquivalent(new[] { midi, project }, CollectionJournal.Read(journal).Select(r => r.Path).ToArray());
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(project))!;
            data["App"] = JsonSerializer.SerializeToElement("Another");
            File.WriteAllText(project, JsonSerializer.Serialize(data));
            Assert.IsNull(CollectionOriginReader.Read(project));
            File.WriteAllText(files.Path("LOPATA.json"), "{\"App\":\"LOPATA\",\"Schema\":1,\"Version\":\"0.7.5\",\"Melody\":{}}");
            Assert.IsNull(CollectionOriginReader.Read(files.Path("LOPATA.json")));
            Assert.Throws<DirectoryNotFoundException>(() => MusicMelodyWindow.SaveUserMelody(files.Path("missing/failed.mid"), melody, false));
            Assert.HasCount(2, CollectionJournal.Read(journal));
        }
        finally { Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", previous); }
    }

    private static GifPixels Pixels()
    {
        var bytes = new byte[64 * 64 * 4];
        for (var offset = 0; offset < bytes.Length; offset += 4) { bytes[offset] = 100; bytes[offset + 2] = 200; bytes[offset + 3] = 255; }
        return new(64, 64, bytes);
    }

    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CollectionNativeFormats", Guid.NewGuid().ToString("N"));
        internal Files() => Directory.CreateDirectory(root);
        internal string Path(string name) => System.IO.Path.Combine(root, name);
        public void Dispose() => Directory.Delete(root, true);
    }
}
