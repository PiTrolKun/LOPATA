using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Lopata.Collection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Collection.Tests;

[TestClass]
public sealed class CollectionTests
{
    private static readonly string Origin = JsonSerializer.Serialize(new { Schema = 1, Program = "LOPATA", Scenario = "books/export" });

    [TestMethod]
    public void FilenameAndOrdinaryContentsNeverEstablishOrigin()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.File("LOPATA.txt"), "LOPATA");
        File.WriteAllText(fixture.File("story.md"), "A sentence containing LOPATA.Provenance=" + Origin);
        Assert.IsNull(CollectionOriginReader.Read(fixture.File("LOPATA.txt")));
        Assert.IsNull(CollectionOriginReader.Read(fixture.File("story.md")));
        Assert.AreEqual(0, fixture.Scan().Items.Count);
    }

    [TestMethod]
    public void EmbeddedOriginSurvivesRenameAndLackOfLocalJournal()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.File("export.txt"), "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        File.Move(fixture.File("export.txt"), fixture.File("neutral.txt"));
        var result = fixture.Scan();
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(fixture.File("neutral.txt"), result.Items[0].Path);
        Assert.AreEqual("Books", result.Items[0].Scenario);
    }

    [TestMethod]
    public void StaleJournalAddressCannotAdmitUnmarkedReplacement()
    {
        using var fixture = new Fixture();
        var path = fixture.File("result.txt");
        File.WriteAllText(path, "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "books/export", fixture.Root));
        File.Delete(path);
        File.WriteAllText(path, "Unrelated file using the former export address.");
        Assert.AreEqual(0, fixture.Scan().Items.Count);
    }

    [TestMethod]
    public void JournalAndKnownFolderReturnOneOriginal()
    {
        using var fixture = new Fixture();
        var path = fixture.File("result.md");
        File.WriteAllText(path, "<!-- LOPATA.Provenance=" + Origin + " -->\n\nHello");
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "books/export", fixture.Root));
        Assert.AreEqual(1, fixture.Scan().Items.Count);
        Assert.AreEqual(1, CollectionJournal.Read(fixture.Root).Count());
        Assert.IsTrue(CollectionJournal.RecordCompleted(path, "books/export", fixture.Root));
        Assert.AreEqual(1, CollectionJournal.Read(fixture.Root).Count());
    }

    [TestMethod]
    public void OwnProjectProvenanceIsReadWithoutOpeningEmbeddedMaterials()
    {
        using var fixture = new Fixture();
        var path = fixture.File("neutral.LOPATAdoxAI");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            output.Write("{\"Format\":\"LOPATAdoxAI\",\"Schema\":3,\"Provenance\":" + Origin + "}");
        }
        Assert.AreEqual("books/export", CollectionOriginReader.Read(path)?.Scenario);
    }

    [TestMethod]
    public void ArbitraryTextEntryInsideArchiveIsNotOrigin()
    {
        using var fixture = new Fixture();
        var path = fixture.File("ordinary.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var output = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            output.Write("<document>" + Origin + "</document>");
        }
        Assert.IsNull(CollectionOriginReader.Read(path));
    }

    [TestMethod]
    public void SnapshotIsBoundedAndPreservesUnicodeFileIdentity()
    {
        using var fixture = new Fixture();
        CollectionItem[] items = [new(fixture.File("Книга 😀.docx"), "Документы", "Книги")];
        CollectionSnapshot.Write(fixture.Root, items);
        CollectionAssert.AreEqual(items, CollectionSnapshot.Read(fixture.Root).ToArray());
        var snapshot = Path.Combine(fixture.Root, "snapshot.bin");
        var bytes = File.ReadAllBytes(snapshot); BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 16);
        File.WriteAllBytes(snapshot, bytes);
        Assert.Throws<InvalidDataException>(() => CollectionSnapshot.Read(fixture.Root));
    }

    [TestMethod]
    public void ConfigurationAtomicallyControlsNativeEnableFlag()
    {
        using var fixture = new Fixture();
        new CollectionConfig { Enabled = true, Roots = [fixture.Corpus], Language = "en" }.Save(fixture.Root);
        Assert.IsTrue(CollectionConfig.Load(fixture.Root).Enabled);
        Assert.AreEqual(1U, BitConverter.ToUInt32(File.ReadAllBytes(Path.Combine(fixture.Root, "enabled.bin"))));
        new CollectionConfig { Enabled = false }.Save(fixture.Root);
        Assert.AreEqual(0U, BitConverter.ToUInt32(File.ReadAllBytes(Path.Combine(fixture.Root, "enabled.bin"))));
    }

    [TestMethod]
    public void ExcludedLocationsDoNotLeakThroughJournal()
    {
        using var fixture = new Fixture();
        var path = fixture.File("result.txt");
        File.WriteAllText(path, "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        CollectionJournal.RecordCompleted(path, "books/export", fixture.Root);
        var config = new CollectionConfig { Roots = [fixture.Corpus], ExcludedRoots = [fixture.Corpus], SearchEnabled = false };
        Assert.AreEqual(0, new CollectionScanner().Scan(fixture.Root, config, CancellationToken.None).Items.Count);
    }

    [TestMethod]
    public void MalformedMetadataIsRejectedWithoutBreakingOtherResults()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.File("broken.svg"), "<svg><metadata id='lopata-provenance'>{ invalid json }</metadata></svg>");
        File.WriteAllText(fixture.File("good.txt"), "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        Assert.AreEqual(1, fixture.Scan().Items.Count);
    }

    [TestMethod]
    public void ConversationArchiveUsesItsDedicatedCamelCaseProvenance()
    {
        using var fixture = new Fixture();
        var path = fixture.File("conversation.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var output = new StreamWriter(zip.CreateEntry("conversation.json").Open());
            output.Write("{\"messages\":[],\"provenance\":{\"schema\":1,\"program\":\"LOPATA\",\"scenario\":\"letitout/conversation\"}}");
        }
        Assert.AreEqual("letitout/conversation", CollectionOriginReader.Read(path)?.Scenario);
    }

    [TestMethod]
    public void SimilarApplicationNameIsNotAcceptedAsOurProducer()
    {
        using var fixture = new Fixture();
        var path = fixture.File("other.txt");
        File.WriteAllText(path, "[LOPATA.Provenance={\"Schema\":1,\"Program\":\"LOPATA Studio\",\"Scenario\":\"books/export\"}]\n\nHello");
        Assert.IsNull(CollectionOriginReader.Read(path));
    }

    [TestMethod]
    public void RtfOriginIsReadOnlyFromTheReservedUserProperty()
    {
        using var fixture = new Fixture();
        var encoded = Origin.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal);
        var path = fixture.File("book.rtf");
        File.WriteAllText(path, "{\\rtf1{\\*\\userprops{\\propname LOPATA.Provenance}\\proptype30{\\staticval " + encoded + "}}Body}");
        Assert.AreEqual("books/export", CollectionOriginReader.Read(path)?.Scenario);
        File.WriteAllText(path, "{\\rtf1Body LOPATA.Provenance=" + encoded + "}");
        Assert.IsNull(CollectionOriginReader.Read(path));
    }

    [TestMethod]
    public void FilesystemRootExclusionCoversAllItsChildren()
    {
        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.IsTrue(CollectionFilePolicy.IsWithin(Path.Combine(drive, "subfolder", "result.txt"), drive));
        Assert.IsFalse(CollectionFilePolicy.IsWithin(Path.Combine(drive, "database", "result.txt"), Path.Combine(drive, "data")));
    }

    [TestMethod]
    public void Mp3OriginAfterLargeArtworkIsReadWithoutLoadingArtwork()
    {
        using var fixture = new Fixture();
        using var tag = new MemoryStream();
        WriteFrame(tag, "APIC", new byte[3 * 1024 * 1024]);
        WriteFrame(tag, "TXXX", Encoding.UTF8.GetBytes("\u0003LOPATA_PROVENANCE\0" + Origin));
        var path = fixture.File("with-artwork.mp3");
        using (var output = System.IO.File.Create(path))
        {
            var size = (int)tag.Length;
            output.Write([73, 68, 51, 3, 0, 0, (byte)((size >> 21) & 127), (byte)((size >> 14) & 127), (byte)((size >> 7) & 127), (byte)(size & 127)]);
            tag.Position = 0; tag.CopyTo(output);
        }
        Assert.AreEqual("books/export", CollectionOriginReader.Read(path)?.Scenario);
    }

    [TestMethod]
    public void IntermediateMergeRejectsAReplacedPreviousOriginal()
    {
        using var fixture = new Fixture();
        var retained = fixture.File("current.txt"); var replaced = fixture.File("previous.txt");
        File.WriteAllText(retained, "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        File.WriteAllText(replaced, "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        var modified = File.GetLastWriteTimeUtc(replaced);
        File.WriteAllText(replaced, "Ordinary unrelated text at the same saved location.");
        File.SetLastWriteTimeUtc(replaced, modified);
        CollectionItem[] current = [new(retained, "Documents", "Books")];
        CollectionItem[] previous = [new(replaced, "Documents", "Books")];
        var merged = CollectionPublication.MergeValidated(new CollectionConfig { Language = "en" }, current, previous);
        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual(retained, merged[0].Path);
    }

    [TestMethod]
    public void LateScanReplyCannotPublishAfterConfigurationChange()
    {
        using var fixture = new Fixture();
        var config = new CollectionConfig { Enabled = true, Roots = [fixture.Corpus], SearchEnabled = false };
        config.Save(fixture.Root);
        var original = CollectionPublication.Revision(CollectionConfig.Load(fixture.Root));
        CollectionItem[] items = [new(fixture.File("first.txt"), "Documents", "Books")];
        Assert.IsTrue(CollectionPublication.TryPublish(fixture.Root, original, items));
        (config with { ExcludedRoots = [fixture.Corpus] }).Save(fixture.Root);
        Assert.IsFalse(CollectionPublication.TryPublish(fixture.Root, original, [new(fixture.File("late.txt"), "Documents", "Books")]));
        var current = CollectionPublication.Revision(CollectionConfig.Load(fixture.Root));
        Assert.AreEqual(0, CollectionPublication.ReadCurrent(fixture.Root, current).Count);
        Assert.IsTrue(CollectionPublication.TryPublish(fixture.Root, current, []));
        Assert.AreEqual(0, CollectionSnapshot.Read(fixture.Root).Count);
    }

    [TestMethod]
    public void DisableThenReenableInvalidatesThePreviousScanGeneration()
    {
        using var fixture = new Fixture();
        var config = new CollectionConfig { Enabled = true, Roots = [fixture.Corpus], SearchEnabled = false };
        config.Save(fixture.Root);
        var before = CollectionPublication.Revision(CollectionConfig.Load(fixture.Root));
        (config with { Enabled = false }).Save(fixture.Root);
        config.Save(fixture.Root);
        Assert.AreNotEqual(before, CollectionPublication.Revision(CollectionConfig.Load(fixture.Root)));
        Assert.IsFalse(CollectionPublication.TryPublish(fixture.Root, before, []));
    }

    [TestMethod]
    public void BoundedLocalPassResumesUntilLaterFilesHaveBeenExamined()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 7; index++) File.WriteAllText(fixture.File(index + ".txt"), "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        using var scanner = new CollectionScanner(localBatchLimit: 2);
        var config = new CollectionConfig { Roots = [fixture.Corpus], SearchEnabled = false };
        var result = scanner.Scan(fixture.Root, config, CancellationToken.None);
        Assert.IsTrue(result.Limited);
        var count = result.Items.Count;
        for (var attempt = 0; result.Limited && attempt < 10; attempt++)
        {
            result = scanner.Scan(fixture.Root, config, CancellationToken.None);
            Assert.IsGreaterThanOrEqualTo(count, result.Items.Count);
            count = result.Items.Count;
        }
        Assert.IsFalse(result.Limited);
        Assert.AreEqual(7, result.Items.Count);
    }

    [TestMethod]
    public void ChangedKnownRootsDiscardThePreviousTraversalAndItsItems()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.File("old.txt"), "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        var other = Path.Combine(fixture.Root, "other"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "new.txt"), "[LOPATA.Provenance=" + Origin + "]\n\nHello");
        using var scanner = new CollectionScanner(localBatchLimit: 2);
        var config = new CollectionConfig { Roots = [fixture.Corpus], SearchEnabled = false };
        scanner.Scan(fixture.Root, config, CancellationToken.None);
        var result = scanner.Scan(fixture.Root, config with { Roots = [other] }, CancellationToken.None);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(Path.Combine(other, "new.txt"), result.Items[0].Path);
    }

    [TestMethod]
    public void ExpectedWorkerPathIsValidatedAndPartOfPublicationGeneration()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => new CollectionConfig { ExpectedWorkerPath = "relative.exe" }.Save(fixture.Root));
        var config = new CollectionConfig { Enabled = true, ExpectedWorkerPath = Path.Combine(fixture.Root, "one", "worker.exe") };
        config.Save(fixture.Root);
        var first = CollectionPublication.Revision(CollectionConfig.Load(fixture.Root));
        (config with { ExpectedWorkerPath = Path.Combine(fixture.Root, "two", "worker.exe") }).Save(fixture.Root);
        Assert.IsFalse(CollectionPublication.IsCurrent(fixture.Root, first));
    }

    [TestMethod]
    public void OldWorkerCannotPublishEvenIfItLoadedTheNewConfigurationRevision()
    {
        using var fixture = new Fixture();
        var expected = Path.Combine(fixture.Root, "new", "worker.exe");
        new CollectionConfig { Enabled = true, ExpectedWorkerPath = expected }.Save(fixture.Root);
        var current = CollectionPublication.Revision(CollectionConfig.Load(fixture.Root));
        Assert.IsFalse(CollectionPublication.TryPublish(fixture.Root, current, [], Path.Combine(fixture.Root, "old", "worker.exe")));
        Assert.IsTrue(CollectionPublication.TryPublish(fixture.Root, current, [], expected));
    }

    private static void WriteFrame(Stream output, string type, byte[] value)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        output.Write(length); output.Write([0, 0]); output.Write(value);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LopataCollectionTests", Guid.NewGuid().ToString("N"));
        public string Corpus => Path.Combine(Root, "corpus");
        public Fixture() => Directory.CreateDirectory(Corpus);
        public string File(string name) => Path.Combine(Corpus, name);
        public CollectionScanResult Scan() => new CollectionScanner().Scan(Root,
            new CollectionConfig { Roots = [Corpus], SearchEnabled = false, Language = "en" }, CancellationToken.None);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
