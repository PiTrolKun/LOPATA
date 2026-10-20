using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.Books;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class BookProvenanceTests
{
    private static OutputProvenance Facts() => new() { ProgramVersion = "test-0.5.80", Scenario = "books/convert", ModelHistory = "not-applicable", ExportedUtc = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero) };
    private static string Temp() { var folder = Path.Combine(Path.GetTempPath(), "LOPATA-BookMetadataTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); return folder; }

    [TestMethod]
    public async Task Epub2MetadataRetainsVersionAndAuthorAndFirstUncompressedEntry()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "book.epub");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Entry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                Entry(zip, "META-INF/container.xml", "<container><rootfile full-path='content.opf'/></container>");
                Entry(zip, "content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:creator>Автор книги</dc:creator></metadata></package>");
            }
            await BookOutputMetadata.WriteAndVerifyAsync(path, "epub", Facts(), default);
            using var output = ZipFile.OpenRead(path);
            Assert.AreEqual("mimetype", output.Entries[0].FullName);
            Assert.AreEqual(output.Entries[0].Length, output.Entries[0].CompressedLength);
            using var xmlStream = output.GetEntry("content.opf")!.Open(); var doc = XDocument.Load(xmlStream);
            Assert.AreEqual("2.0", doc.Root!.Attribute("version")!.Value);
            Assert.AreEqual("Автор книги", doc.Descendants().Single(e => e.Name.LocalName == "creator").Value);
            var meta = doc.Descendants().Single(e => (string?)e.Attribute("name") == "LOPATA.Provenance");
            using var json = JsonDocument.Parse(meta.Attribute("content")!.Value);
            Assert.AreEqual("test-0.5.80", json.RootElement.GetProperty("ProgramVersion").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task Epub3WritesOneUtcModifiedAndPreservesForeignMetadata()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "book.epub");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Entry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                Entry(zip, "META-INF/container.xml", "<container><rootfile full-path='book.opf'/></container>");
                Entry(zip, "book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0'><metadata><meta property='dcterms:modified'>2000-01-01T00:00:00Z</meta><meta property='source:history'>old history</meta></metadata><manifest><item href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest></package>");
                Entry(zip, "nav.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><script src='reader.js'></script></head><body>Reader navigation</body></html>");
            }
            await BookOutputMetadata.WriteAndVerifyAsync(path, "epub", Facts(), default);
            using var zipOut = ZipFile.OpenRead(path); using var stream = zipOut.GetEntry("book.opf")!.Open(); var doc = XDocument.Load(stream);
            Assert.AreEqual("old history", doc.Descendants().Single(e => (string?)e.Attribute("property") == "source:history").Value);
            Assert.AreEqual("2026-10-19T10:00:00Z", doc.Descendants().Single(e => (string?)e.Attribute("property") == "dcterms:modified").Value);
            Assert.AreEqual("nav scripted", doc.Descendants().Single(e => e.Name.LocalName == "item").Attribute("properties")!.Value);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task PlainTextProvenanceDoesNotChangeAuthorText()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "song.txt"); var original = "Первый абзац.\n\nВторой — с ё и 日本語.\n";
            await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
            await BookOutputMetadata.WriteAndVerifyAsync(path, "txt", Facts(), default);
            var output = await File.ReadAllTextAsync(path);
            Assert.IsTrue(output.EndsWith(original, StringComparison.Ordinal));
            using var json = JsonDocument.Parse(output["[LOPATA.Provenance=".Length..output.IndexOf("]\n\n", StringComparison.Ordinal)]);
            Assert.AreEqual("not-applicable", json.RootElement.GetProperty("ModelHistory").GetString());
            Assert.AreEqual(0, json.RootElement.GetProperty("Models").GetArrayLength());
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task ComicProvenancePreservesPageBytesAndOriginalNotes()
    {
        var folder = Temp();
        try
        {
            var path = Path.Combine(folder, "comic.cbz"); var image = new byte[] { 1, 2, 3, 4, 5 };
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            { using (var entry = zip.CreateEntry("001.png").Open()) entry.Write(image); Entry(zip, "ComicInfo.xml", "<ComicInfo><Title>Комикс</Title><Notes>Original production history</Notes></ComicInfo>"); }
            await BookOutputMetadata.WriteAndVerifyAsync(path, "cbz", Facts(), default);
            using var result = ZipFile.OpenRead(path);
            using var input = result.GetEntry("001.png")!.Open(); using var memory = new MemoryStream(); await input.CopyToAsync(memory);
            CollectionAssert.AreEqual(SHA256.HashData(image), SHA256.HashData(memory.ToArray()));
            using var xmlStream = result.GetEntry("ComicInfo.xml")!.Open(); var xml = XDocument.Load(xmlStream);
            Assert.IsTrue(xml.Root!.Element("Notes")!.Value.StartsWith("Original production history", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(result.GetEntry("LOPATA.Provenance.json")!.Open());
            Assert.AreEqual("LOPATA", json.RootElement.GetProperty("Program").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Entry(ZipArchive zip, string name, string value, CompressionLevel level = CompressionLevel.Optimal)
    { using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false)); writer.Write(value); }
}
