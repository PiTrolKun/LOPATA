using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiterarySourceTests
{
    private string _root = null!;
    [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "AIHubRagTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }

    [TestMethod] public void EpubUsesSpineNotArchiveOrderAndKeepsParagraphBoundaries()
    {
        var file = Path.Combine(_root, "book.epub");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            Add("META-INF/container.xml", "<container><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>");
            Add("OPS/second.xhtml", "<html><body><p>Вторая глава</p></body></html>");
            Add("OPS/book.opf", "<package><manifest><item id='one' href='first.xhtml'/><item id='two' href='second.xhtml'/><item id='nav' href='nav.xhtml' properties='nav'/></manifest><spine><itemref idref='nav'/><itemref idref='one'/><itemref idref='two'/></spine></package>");
            Add("OPS/first.xhtml", "<html><body><p>Первая глава</p><p>Новый абзац</p><script>do not index</script></body></html>");
        }
        var sections = LiterarySourceReader.Read(file, "book", CancellationToken.None);
        Assert.HasCount(2, sections);
        Assert.StartsWith("Первая глава", sections[0].Text);
        StringAssert.Contains(sections[0].Text, "\nНовый абзац");
        Assert.DoesNotContain("do not index", sections[0].Text);
        Assert.AreEqual("Вторая глава", sections[1].Text);
    }
    [TestMethod] public void EmptyAndUnsupportedBooksFailExplicitly()
    {
        var file = Path.Combine(_root, "empty.txt"); File.WriteAllText(file, " \n");
        Assert.Throws<InvalidDataException>(() => LiterarySourceReader.Read(file, "book", CancellationToken.None));
        var binary = Path.Combine(_root, "book.bin"); File.WriteAllText(binary, "text");
        Assert.Throws<InvalidDataException>(() => LiterarySourceReader.Read(binary, "book", CancellationToken.None));
    }
    [TestMethod] public void CancellationStopsReaderBeforeOpening()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LiterarySourceReader.Read("missing.epub", "book", cts.Token));
    }
    [TestMethod] public void Utf8TextIsNotSilentlyDecodedAsGibberish()
    {
        var path = Path.Combine(_root, "book.txt"); File.WriteAllBytes(path, [0xff, 0x80, 0x81]);
        Assert.Throws<DecoderFallbackException>(() => LiterarySourceReader.Read(path, "book", CancellationToken.None));
    }
    [TestMethod] public async Task PinnedDigestDetectsTamperingAndSupportsGitBlobs()
    {
        var path = Path.Combine(_root, "file"); var bytes = Encoding.UTF8.GetBytes("Текст"); await File.WriteAllBytesAsync(path, bytes);
        var header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\0");
        var gitHash = Convert.ToHexString(SHA1.HashData(header.Concat(bytes).ToArray()));
        Assert.IsTrue(await LiteraryArtifactDownload.ValidAsync(path, bytes.Length, gitHash, "gitsha1", CancellationToken.None));
        await File.WriteAllTextAsync(path, "Другой");
        Assert.IsFalse(await LiteraryArtifactDownload.ValidAsync(path, bytes.Length, gitHash, "gitsha1", CancellationToken.None));
    }
    [TestMethod] public void IndexCommitFailureRollsBackNewProjectOnly()
    {
        var store = new LiteraryProjectStore(Path.Combine(_root, "index.json"));
        var project = new LiteraryProject { ProjectName = "Проект", Genres = ["fantasy"] };
        Assert.Throws<IOException>(() => store.Create(_root, project, [], folder =>
        { File.WriteAllText(Path.Combine(folder, "partial"), "test"); throw new IOException("index failed"); }));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Проект")));
        Assert.IsEmpty(store.Load().Projects);
        Assert.IsEmpty(Directory.GetDirectories(_root, ".lopata-new-*"));
    }
    [TestMethod] public void CompletedIndexBecomesPartOfProjectAtomically()
    {
        var store = new LiteraryProjectStore(Path.Combine(_root, "index.json"));
        var entry = store.Create(_root, new LiteraryProject { ProjectName = "Проект", Genres = ["fantasy"] }, [],
            folder => { Directory.CreateDirectory(Path.Combine(folder, "Rag")); File.WriteAllText(Path.Combine(folder, "Rag", "test.json"), "{}"); });
        Assert.IsTrue(File.Exists(Path.Combine(entry.ProjectPath, "Rag", "test.json")));
        Assert.HasCount(1, store.Load().Projects);
    }
    [TestMethod] public void QdrantNamespaceCannotAddressOtherCollections()
    {
        Assert.Throws<FormatException>(() => QdrantRuntime.LiteraryCollection("../collections/other"));
        var a = QdrantRuntime.LiteraryCollection(Guid.NewGuid().ToString());
        var b = QdrantRuntime.LiteraryCollection(Guid.NewGuid().ToString());
        Assert.AreNotEqual(a, b); Assert.StartsWith("lopata_source_", a);
    }
    [TestMethod] public void InvalidVectorsCannotEnterSourceIndex()
    {
        var vector = new float[1024]; vector[0] = 1;
        var valid = JsonSerializer.SerializeToElement(new { vector, payload = new { kind = "reference" } });
        LiterarySourceIndex.ValidatePoint(valid);
        var invalid = JsonSerializer.SerializeToElement(new { vector = new[] { 1f, 0f }, payload = new { kind = "reference" } });
        Assert.Throws<InvalidDataException>(() => LiterarySourceIndex.ValidatePoint(invalid));
        vector[0] = 0;
        Assert.Throws<InvalidDataException>(() => LiterarySourceIndex.ValidatePoint(JsonSerializer.SerializeToElement(new { vector, payload = new { kind = "reference" } })));
    }
}
