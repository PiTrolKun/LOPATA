using System.IO;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass]
public sealed class BookSmartProgressTests
{
    [TestMethod]
    public async Task StructureProgressIdentifiesCurrentSectionAndEveryBatchIncludingCacheReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "book-log-progress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "Original source");
        var models = new Model(); var events = new List<BookConversionProgress>();
        var progress = new Capture(events);
        BookContent Content()
        {
            var book = new BookContent(); book.Parts.Add(new("one", "<p>" + new string('я', 24000) + "</p>"));
            book.Parts.Add(new("two", "<p>Другой раздел</p>")); return book;
        }
        try
        {
            using var processor = new BookSmartProcessor(models, Path.Combine(root, "cache"), progress);
            await processor.ProcessAsync(Content(), source, CancellationToken.None);
            var first = events.Where(e => e.Phase == "structure").ToArray();
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 2 }, first.Select(e => e.Section).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 1 }, first.Select(e => e.Item).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 3, 3, 1 }, first.Select(e => e.ItemCount).ToArray());
            Assert.IsTrue(first.All(e => e.SectionCount == 2)); Assert.AreEqual(4, models.Calls);
            events.Clear(); await processor.ProcessAsync(Content(), source, CancellationToken.None);
            Assert.AreEqual(4, models.Calls, "Cached progress must not repeat the completed model work.");
            CollectionAssert.AreEqual(first, events.Where(e => e.Phase == "structure").ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Capture(List<BookConversionProgress> events) : IProgress<BookConversionProgress>
    { public void Report(BookConversionProgress value) => events.Add(value); }
    private sealed class Model : IBookSmartModels
    {
        public int Calls;
        public OutputModelUse OcrIdentity => new("ocr", "fixture", "one", "ocr");
        public OutputModelUse VisionIdentity => new("vision", "fixture", "one", "vision");
        public OutputModelUse StructureIdentity => new("structure", "fixture", "one", "structure");
        public Task<BookSmartPage> RecognizeAsync(string image, CancellationToken token) => throw new AssertFailedException("Text must not invoke OCR.");
        public Task<string[]> InspectAsync(string image, CancellationToken token) => throw new AssertFailedException("Text must not invoke vision.");
        public Task<BookSmartStructure[]> StructureAsync(BookSmartBlock[] blocks, CancellationToken token)
        { Calls++; return Task.FromResult(blocks.Select(block => new BookSmartStructure(block.Id, "keep")).ToArray()); }
        public void Dispose() { }
    }
}
