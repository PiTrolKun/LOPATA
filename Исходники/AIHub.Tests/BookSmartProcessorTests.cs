using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartProcessorTests
{
    private string _root = "", _source = "";
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "LOPATA-smart-processor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.pdf"); File.WriteAllText(_source, "Immutable original fixture");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [TestMethod]
    public async Task LongParagraphKeepsExactAuthorTextAndStylesWithoutBecomingAFabricatedHeading()
    {
        var text = string.Concat(Enumerable.Range(0, 2300).Select(index => "Слово" + index + " "));
        var content = new BookContent(); content.Parts.Add(new("Chapter", "<style>.author{color:#3344aa}</style><p class='author'>" + text + "</p>"));
        var models = new Models { Heading = true }; BookSmartProcessed result;
        using (var processor = new BookSmartProcessor(models, Path.Combine(_root, "cache"))) result = await processor.ProcessAsync(content, _source, CancellationToken.None);
        Assert.IsTrue(models.Disposed); StringAssert.Contains(result.Content.Parts[0].Html, ".author{color:#3344aa}");
        var dom = BookMarkup.Parse(result.Content.Parts[0].Html);
        Assert.AreEqual(text, dom.QuerySelector("p.author")!.TextContent); Assert.IsNull(dom.QuerySelector("h1"));
        Assert.IsTrue(models.Batches.Count >= 2); Assert.IsTrue(models.Batches.All(batch => batch.Sum(block => block.Text.Length) <= 10000));
        Assert.AreEqual(text, string.Concat(models.Batches.SelectMany(batch => batch).Select(block => block.Text)));
        Assert.AreEqual(result.Marks.Length, models.Batches.Sum(batch => batch.Length));
    }

    [TestMethod]
    public async Task PageStagesAreGroupedCacheRepairsBrokenImagesAndModelMetadataIncludesLayout()
    {
        var content = Pictures(); var models = new Models();
        var cache = Path.Combine(_root, "cache", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_source)))); Directory.CreateDirectory(cache);
        var original = content.Assets["one.png"]; var imagePath = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(original)) + "-analysis.png");
        await File.WriteAllTextAsync(imagePath, "Interrupted image write");
        using var processor = new BookSmartProcessor(models, Path.Combine(_root, "cache"));
        var result = await processor.ProcessAsync(content, _source, CancellationToken.None);
        using var repaired = SKBitmap.Decode(imagePath); Assert.IsNotNull(repaired);
        Assert.AreEqual(10, repaired.Width); Assert.AreEqual(SKColors.Red, repaired.GetPixel(0, 0));
        CollectionAssert.AreEqual(original, content.Assets["one.png"]);
        CollectionAssert.AreEqual(new[] { "vision", "vision", "ocr", "ocr", "structure", "structure" }, models.Events.ToArray());
        Assert.AreEqual(2, result.Marks.Length, "OCR and structure must not create two marks for one block.");
        Assert.AreEqual(4, result.Models.Length); Assert.IsTrue(result.Models.Any(model => model.Role == "layout"));
        Assert.AreEqual(2, result.Content.Parts.Sum(part => BookMarkup.Parse(part.Html).QuerySelectorAll("img").Length));
    }

    [TestMethod]
    public async Task RecognizedTablesAndCropAssetsSurviveWhileExecutableMarkupDoesNot()
    {
        var content = Pictures(1); var crop = Png(SKColors.Green);
        var models = new Models { Page = new([new(0, "AB", "table", Html: "<table onclick='bad()'><tr><td>A</td><td>B</td></tr></table><script>bad()</script>")], [])
            { Assets = [new("figure.png", crop, 0)] } };
        using var processor = new BookSmartProcessor(models, Path.Combine(_root, "cache"));
        var result = await processor.ProcessAsync(content, _source, CancellationToken.None);
        var dom = BookMarkup.Parse(result.Content.Parts[0].Html);
        Assert.AreEqual(1, dom.QuerySelectorAll("table").Length); Assert.AreEqual(2, dom.QuerySelectorAll("td").Length);
        Assert.IsNull(dom.QuerySelector("script")); Assert.IsFalse(result.Content.Parts[0].Html.Contains("onclick", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, dom.QuerySelectorAll("img").Length); Assert.AreEqual(2, result.Content.Assets.Count);
        Assert.IsTrue(result.Content.Assets.Values.Any(bytes => bytes.SequenceEqual(crop)));
        CollectionAssert.AreEqual(new[] { "A", "B" }, result.Marks.Select(mark => mark.Text).ToArray());
    }

    [TestMethod]
    public async Task UnreadablePageKeepsTheOriginalAndARedPlaceholderInsteadOfFailingTheBook()
    {
        var content = Pictures(1); var models = new Models { Page = new([], []) };
        using var processor = new BookSmartProcessor(models, Path.Combine(_root, "cache"));
        var result = await processor.ProcessAsync(content, _source, CancellationToken.None);
        StringAssert.Contains(result.Content.Parts[0].Html, "⟦…⟧"); Assert.AreEqual(1, BookMarkup.Parse(result.Content.Parts[0].Html).QuerySelectorAll("img").Length);
        Assert.AreEqual(BookReviewMarkKind.Questionable, result.Marks.Single().Kind);
        Assert.IsTrue(result.Content.Warnings.Contains("BookConverter.Smart.OcrQuestions"));
    }

    [TestMethod]
    public async Task AmbiguousRepeatedPassagesStayUnresolvedRatherThanMarkingTheWrongOccurrence()
    {
        var content = Pictures(); var models = new Models { Page = new([new(0, "Повтор текста", Questionable: true)], []) };
        using var processor = new BookSmartProcessor(models, Path.Combine(_root, "cache"));
        var result = await processor.ProcessAsync(content, _source, CancellationToken.None);
        Assert.AreEqual(2, result.Marks.Length); Assert.IsTrue(result.Marks.All(mark => mark.Occurrence == int.MaxValue));
    }

    [TestMethod]
    public async Task AlteredCheckpointPayloadIsRejectedInsteadOfBeingReused()
    {
        var called = 0;
        Task<string[]> Create() { called++; return Task.FromResult(new[] { "Original receipt" }); }
        await BookSmartProcessor.CachedAsync(_root, "model-revision-a", Create, CancellationToken.None);
        var path = Directory.GetFiles(_root, "*.json").Single(); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Payload"] = "[\"Injected data\"]"; await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => BookSmartProcessor.CachedAsync(_root, "model-revision-a", Create, CancellationToken.None));
        Assert.AreEqual(1, called);
    }

    private static byte[] Png(SKColor color)
    { using var bitmap = new SKBitmap(10, 10); bitmap.Erase(color); using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100); return image.ToArray(); }
    private static BookContent Pictures(int count = 2)
    {
        var content = new BookContent(); content.Assets["one.png"] = Png(SKColors.Red);
        content.Parts.Add(new("Page 1", "<p><img src='one.png'></p>"));
        if (count == 2) { content.Assets["two.png"] = Png(SKColors.Blue); content.Parts.Add(new("Page 2", "<p><img src='two.png'></p>")); }
        return content;
    }
    private sealed class Models : IBookSmartModels
    {
        public bool Heading { get; init; }
        public BookSmartPage? Page { get; init; }
        public bool Disposed { get; private set; }
        public List<string> Events { get; } = [];
        public List<BookSmartBlock[]> Batches { get; } = [];
        private int _recognized;
        public OutputModelUse OcrIdentity => new("ocr", "OCR", "one", "ocr");
        public OutputModelUse[] OcrDependencies => [new("layout", "Layout", "one", "layout")];
        public OutputModelUse VisionIdentity => new("vision", "Vision", "one", "vision");
        public OutputModelUse StructureIdentity => new("structure", "Structure", "one", "structure");
        public Task<BookSmartPage> RecognizeAsync(string image, CancellationToken token)
        { Events.Add("ocr"); return Task.FromResult(Page ?? new([new(0, "Page text " + ++_recognized)], [])); }
        public Task<string[]> InspectAsync(string image, CancellationToken token) { Events.Add("vision"); return Task.FromResult(Array.Empty<string>()); }
        public Task<BookSmartStructure[]> StructureAsync(BookSmartBlock[] blocks, CancellationToken token)
        { Events.Add("structure"); Batches.Add(blocks); return Task.FromResult(blocks.Select(block => new BookSmartStructure(block.Id, Heading ? "heading1" : "keep")).ToArray()); }
        public void Dispose() => Disposed = true;
    }
}
