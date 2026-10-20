using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartContextNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task LongRussianBookFragmentFitsRealTokenizerAndReachesDocxWithoutChangingAuthorText()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_CONTEXT_NATIVE") != "1")
            Assert.Inconclusive("Explicit local-model regression opt-in required; no download.");
        var core = new ManagedModelLibraryStore().Load(ManagedModelCatalog.CoreArtifactId)!;
        Assert.IsNotNull(core);
        var corePath = Path.Combine(core.InstallDirectory, core.Files.First(f => f.Purpose == "main_model").RelativePath);
        var folder = Path.Combine(AppDataPaths.ProjectRoot!, "_tmp", "book-smart-88-native", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var actualSource = Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_REGRESSION_SOURCE");
        var before = actualSource is null ? null : SHA256.HashData(await File.ReadAllBytesAsync(actualSource));
        var author = actualSource is null ? string.Concat(Enumerable.Repeat("Точный русский текст автора: ёж, кавычки и числа 3,14. ", 500))
            : BookConversionReader.Read(actualSource, new(), CancellationToken.None).PlainText;
        Assert.IsTrue(author.Length > 24000);
        author = author[..24000]; if (char.IsHighSurrogate(author[^1])) author = author[..^1];
        var log = new List<string>();
        void Log(string value) { log.Add(value); TestContext.WriteLine(value); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var context = new UserContextService(new(), new());
        BookSmartBlock[] sample = [new(0, author[..8000])];
        using (var runtime = new LlamaServerRuntimeService(context))
        {
            await runtime.PrepareAsync(new() { Name = "Qwen3-8B", Path = corePath, Format = "gguf" }, Log, deadline.Token);
            var previous = await runtime.CountPoetryAsync(BookSmartStructureProtocol.Instructions,
                [new("user", JsonSerializer.Serialize(sample), "fixture")], deadline.Token);
            var current = await runtime.CountPoetryAsync(BookSmartStructureProtocol.Instructions,
                [new("user", BookSmartStructureProtocol.Input(sample), "fixture")], deadline.Token);
            TestContext.WriteLine($"Same 8000 author characters: escaped_input_tokens={previous}; literal_input_tokens={current}");
            Assert.IsTrue(current < previous / 2);
            var oversized = await runtime.GenerateBoundedPrivateJsonAsync(new() { Name = "Qwen3-8B", Path = corePath, Format = "gguf" },
                BookSmartStructureProtocol.Payload([new(0, string.Concat(Enumerable.Repeat(author[..8000], 10)))]),
                BookSmartStructureProtocol.OutputTokens, Log, deadline.Token);
            Assert.IsNull(oversized, "Oversized input must be returned for splitting before completion, without truncation or HTTP 400.");
        }
        // Actual production processor divides a long paragraph and exports the actual target format.
        var source = Path.Combine(folder, "long-russian.html");
        await File.WriteAllTextAsync(source, "<html><body><p>" + BookMarkup.Escape(author) + "</p></body></html>");
        using var models = new BookSmartModels(new(), new(), context, corePath,
            (_, _, _) => throw new AssertFailedException("Digital text must not invoke OCR."), Log);
        using var processor = new BookSmartProcessor(models, Path.Combine(folder, "cache"));
        var result = await new BookConversionService(null, processor).ConvertAsync(new()
        { Sources = [source], Smart = new(), OutputFormat = "docx", OutputDirectory = Path.Combine(folder, "out") }, cancellationToken: deadline.Token);
        Assert.AreEqual(0, result.Failures.Length, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.AreEqual(1, result.Outputs.Length);
        var target = BookConversionReader.Read(result.Outputs[0], new(), deadline.Token);
        StringAssert.Contains(target.PlainText, author);
        Assert.IsTrue(log.Count(line => line.StartsWith("Document context:", StringComparison.Ordinal)) >= 3);
        using var docx = System.IO.Compression.ZipFile.OpenRead(result.Outputs[0]);
        using var reader = new StreamReader(docx.GetEntry("docProps/custom.xml")!.Open());
        var metadata = await reader.ReadToEndAsync(); StringAssert.Contains(metadata, "LOPATA"); StringAssert.Contains(metadata, "Qwen");
        if (actualSource is not null) CollectionAssert.AreEqual(before!, SHA256.HashData(await File.ReadAllBytesAsync(actualSource)));
        TestContext.WriteLine("Validated DOCX: " + result.Outputs[0]);
    }
}
