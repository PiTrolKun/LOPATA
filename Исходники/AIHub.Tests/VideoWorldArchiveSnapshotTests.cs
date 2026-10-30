using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoWorldArchiveSnapshotTests
{
    [TestMethod]
    public void CompressionPreservesExactUnicodeAndRejectsInvalidOrOversizedPayloads()
    {
        var text = "[{\"name\":\"Воин 🐱\",\"description\":\"  Снято!\\n\"}]";
        Assert.AreEqual(text, VideoWorldArchiveSnapshots.Unpack(VideoWorldArchiveSnapshots.Pack(text)));
        Assert.AreEqual(text, VideoWorldArchiveSnapshots.Unpack(text));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoWorldArchiveSnapshots.Unpack("br1:invalid!"));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoWorldArchiveSnapshots.Unpack("br1:"));
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Fastest, true)) stream.Write(new byte[8 * 1024 * 1024 + 1]);
        Assert.ThrowsExactly<InvalidDataException>(() => VideoWorldArchiveSnapshots.Unpack("br1:" + Convert.ToBase64String(output.ToArray())));
    }

    [TestMethod]
    public void LegacyArchiveAndNewWireFormatKeepEveryRevisionAndUndoRedo()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root();
        p.World!.Cards[0].Name = "Мастерская"; VideoWorld.Capture(p);
        var expected = p.World.History.ToArray(); var path = files.File("legacy.lopataAIVid");
        VideoPreparationArchive.Save(p, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!; JsonNode json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
            Assert.AreEqual(4, (int)json["Schema"]!);
            json["Schema"] = 1;
            foreach (var revision in json["Project"]!["World"]!["History"]!.AsArray())
                revision!["Cards"] = VideoWorldArchiveSnapshots.Unpack((string)revision["Cards"]!);
            entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("project.json").Open()); writer.Write(json.ToJsonString());
        }
        var loaded = VideoPreparationArchive.Load(path, files.File("opened"));
        CollectionAssert.AreEqual(expected, loaded.World!.History.ToArray());
        VideoPreparationArchive.Save(loaded, path);
        loaded = VideoPreparationArchive.Load(path, files.File("opened-again"));
        CollectionAssert.AreEqual(expected, loaded.World!.History.ToArray());
        Assert.IsTrue(VideoWorld.Move(loaded, -1)); Assert.AreEqual("", loaded.World.Cards[0].Name);
        Assert.IsTrue(VideoWorld.Move(loaded, 1)); Assert.AreEqual("Мастерская", loaded.World.Cards[0].Name);
    }

    [TestMethod]
    public void ManyLargeWorldRevisionsSaveAndCheckpointWithoutLosingHistory()
    {
        using var files = new VideoPreparationTests.TestFiles(); var session = new VideoPreparationSession(files.File("recovery"));
        var p = VideoWorldTests.Root();
        p.World!.Cards[0].Text.IdeaText = new string('я', 20000);
        for (var i = 0; i < 120; i++) { p.World.Cards[0].Name = "Правка " + i; VideoWorld.Capture(p); }
        Assert.IsGreaterThan(32 * 1024 * 1024, JsonSerializer.SerializeToUtf8Bytes(p).Length);
        var expected = p.World.History.ToArray(); var path = files.File("large.lopataAIVid");
        VideoPreparationArchive.Save(p, path);
        using (var zip = ZipFile.OpenRead(path)) Assert.IsLessThan(32 * 1024 * 1024L, zip.GetEntry("project.json")!.Length);
        session.Load(path); session.Project.World!.Cards[0].Name = "Последняя правка";
        session.Changed(); session.Checkpoint(); Assert.IsFalse(session.IsDirty);
        var reopened = VideoPreparationArchive.Load(path, files.File("reopened"));
        CollectionAssert.AreEqual(expected, reopened.World!.History.Take(expected.Length).ToArray());
        Assert.AreEqual("Последняя правка", reopened.World.Cards[0].Name);
        Assert.IsTrue(VideoWorld.Move(reopened, -1)); Assert.AreEqual("Правка 119", reopened.World.Cards[0].Name);
        Assert.IsTrue(VideoWorld.Move(reopened, 1)); Assert.AreEqual("Последняя правка", reopened.World.Cards[0].Name);
        var recovery = VideoPreparationArchive.Load(session.RecoveryPath, files.File("recovered"));
        Assert.AreEqual(reopened.World.RevisionId, recovery.World!.RevisionId);
    }

    [TestMethod]
    public void ExplicitProjectCopyCanBeResavedWithExactHistoryPreserved()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_PROJECT_REPAIR_SOURCE");
        var destination = Environment.GetEnvironmentVariable("LOPATA_PROJECT_REPAIR_DESTINATION");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination)) Assert.Inconclusive("Explicit source and destination required.");
        using var files = new VideoPreparationTests.TestFiles();
        var p = VideoPreparationArchive.Load(source, files.File("opened")); var history = p.World!.History.ToArray();
        var revision = p.World.RevisionId; var cards = VideoWorld.SerializeCards(p.World.Cards);
        var timer = System.Diagnostics.Stopwatch.StartNew(); VideoPreparationArchive.Save(p, destination);
        var saved = VideoPreparationArchive.Load(destination, files.File("reopened"));
        CollectionAssert.AreEqual(history, saved.World!.History.ToArray()); Assert.AreEqual(revision, saved.World.RevisionId);
        Assert.AreEqual(cards, VideoWorld.SerializeCards(saved.World.Cards)); Assert.AreEqual(p.Id, saved.Id);
        using var archive = ZipFile.OpenRead(destination);
        Console.WriteLine($"Project {p.Id}: {history.Length} revisions, manifest {archive.GetEntry("project.json")!.Length} bytes; save/reload {timer.Elapsed.TotalSeconds:F2}s");
        // Reproduce subsequent edits only on a temporary copy; the supplied destination stays unchanged.
        var session = new VideoPreparationSession(files.File("recovery")); session.ApplyLoaded(saved, files.File("edited.lopataAIVid"));
        for (var i = 0; i < 6; i++) { session.Project.World!.Cards[0].Name = "Контрольная правка " + i; VideoWorld.Capture(session.Project); }
        session.Changed(); session.Checkpoint();
        var edited = VideoPreparationArchive.Load(session.ArchivePath!, files.File("edited-opened"));
        Assert.AreEqual("Контрольная правка 5", edited.World!.Cards[0].Name);
        CollectionAssert.AreEqual(history, edited.World.History.Take(history.Length).ToArray());
        Assert.IsTrue(VideoWorld.Move(edited, -1)); Assert.IsTrue(VideoWorld.Move(edited, 1));
    }
}
