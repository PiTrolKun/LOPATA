using System.IO;
using System.Text;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Lopata.Collection;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class CollectionExportIntegrationTests
{
    [TestMethod]
    public async Task RecoveryCheckpointsAndUnchangedTransferAttachmentsStayOutsideTheJournal()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        var journal = files.File("collection");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", journal);
        try
        {
            var session = new VideoPreparationSession(files.File("recovery"));
            session.Project.Title = "A user project"; session.Changed(); session.Checkpoint(false);
            Assert.IsEmpty(CollectionJournal.Read(journal));
            var archive = files.File("work.lopataAIVid"); session.Save(archive);
            Assert.AreEqual(archive, CollectionJournal.Read(journal).Single().Path);

            var project = VideoPreparationFinishTests.ReadyProject();
            var attachment = files.File("imported.txt"); await File.WriteAllTextAsync(attachment, "Original user input");
            project.Attachments.Add(new(attachment, "reference"));
            var destination = files.File("transfer");
            await VideoPreparationExport.ExportAsync(project, destination, default);
            var entries = CollectionJournal.Read(journal).ToArray();
            Assert.IsTrue(entries.Any(e => e.Path == Path.Combine(destination, "timeline.csv")));
            Assert.IsTrue(entries.Any(e => e.Path == Path.Combine(destination, "project.lopataAIVid")));
            Assert.IsFalse(entries.Any(e => e.Path.StartsWith(Path.Combine(destination, "materials"), StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(entries.Any(e => e.Path.Contains(".lopata-export-", StringComparison.Ordinal)));
            Assert.AreEqual("Original user input", await File.ReadAllTextAsync(attachment));
        }
        finally { Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", previous); }
    }

    [TestMethod]
    public async Task CompletedWaveAndDocumentExportsEnterOnlyTheIsolatedJournal()
    {
        var root = MusicOutputTests.Temporary();
        var previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        var journal = Path.Combine(root, "collection");
        Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", journal);
        try
        {
            var source = Path.Combine(root, "source.wav");
            MusicOutputTests.WriteWave(source);
            var original = File.ReadAllBytes(source);
            var wave = Path.Combine(root, "spoken.wav");
            var facts = new OutputProvenance { Scenario = "office/reader-speech" };
            await OutputWaveMetadata.ExportAsync(source, wave, facts);
            var document = Path.Combine(root, "story.docx");
            LiteraryDocxExporter.Export([new(1, "Глава", ["Текст произведения"])], document,
                new OutputProvenance { Scenario = "literature/export" });

            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            var records = CollectionJournal.Read(journal).ToArray();
            CollectionAssert.AreEquivalent(new[] { wave, document }, records.Select(r => r.Path).ToArray());
            Assert.AreEqual("office/reader-speech", CollectionOriginReader.Read(wave)?.Scenario);
            Assert.AreEqual("literature/export", CollectionOriginReader.Read(document)?.Scenario);
            Assert.IsFalse(records.Any(r => r.Path.Contains(".tmp", StringComparison.Ordinal)));

            var blocked = Path.Combine(root, "missing", "failed.wav");
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => OutputWaveMetadata.ExportAsync(source, blocked, facts));
            Assert.HasCount(2, CollectionJournal.Read(journal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", previous);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Mp3EncoderIdentityUsesTheNativeFrameWhilePrivateTagsRetainOrigin()
    {
        var tags = new Dictionary<string, string>
        {
            ["encoded_by"] = "LOPATA", ["LOPATA_JOB"] = "test-job", ["LOPATA_VERSION"] = "0.7.65-dev",
            ["LOPATA_MODEL"] = "test-model", ["title"] = "A title which is not the origin marker"
        };
        var bytes = AudioConversionMetadata.Id3(tags, []);
        var frames = new Dictionary<string, byte[]>();
        for (var offset = 10; offset + 10 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var n = bytes.AsSpan(offset + 4, 4);
            var length = n[0] << 21 | n[1] << 14 | n[2] << 7 | n[3];
            frames[id] = bytes.AsSpan(offset + 10, length).ToArray();
            offset += 10 + length;
        }
        Assert.AreEqual("LOPATA", Encoding.UTF8.GetString(frames["TENC"].AsSpan(1)));
        var root = MusicOutputTests.Temporary();
        try
        {
            var path = Path.Combine(root, "neutral.mp3");
            File.WriteAllBytes(path, bytes);
            Assert.AreEqual("music/generate", CollectionOriginReader.Read(path)?.Scenario);
            File.WriteAllBytes(path, AudioConversionMetadata.Id3(new Dictionary<string, string> { ["title"] = "LOPATA" }, []));
            Assert.IsNull(CollectionOriginReader.Read(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
