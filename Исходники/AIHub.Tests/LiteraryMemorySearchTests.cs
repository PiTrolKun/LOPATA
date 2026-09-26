using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemorySearchTests
{
    private string _root = "";
    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", "MemorySearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(new LiteraryProject()));
    }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public async Task AdaptiveWindowsCoverUnicodeWithoutGapsAndResumeEmptyPasses()
    {
        var text = string.Concat(Enumerable.Repeat("Слова 😀 и следующая строка.\n", 500));
        var calls = 0; const int limit = 4500;
        var search = Search(Fits(limit), (messages, _) =>
        {
            calls++; Assert.IsTrue(Size(messages) <= limit);
            using var input = JsonDocument.Parse(messages[^1].Content);
            foreach (var window in input.RootElement.GetProperty("windows").EnumerateArray())
            {
                var value = window.GetProperty("Text").GetString()!;
                Assert.IsFalse(char.IsLowSurrogate(value[0])); Assert.IsFalse(char.IsHighSurrogate(value[^1]));
            }
            return Task.FromResult("{\"findings\":[]}");
        });
        var request = Request(text);
        var result = await search.RunAsync(request, Final, null, default);
        Assert.IsGreaterThan(1, calls); var firstCalls = calls;
        var coverage = new bool[text.Length];
        foreach (var path in Passes())
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var window in saved.RootElement.GetProperty("Windows").EnumerateArray())
            {
                var from = window.GetProperty("CoveredStart").GetInt32();
                var to = window.GetProperty("End").GetInt32();
                Assert.IsFalse(window.TryGetProperty("Text", out _));
                for (var index = from; index < to; index++) { Assert.IsFalse(coverage[index]); coverage[index] = true; }
            }
        }
        Assert.IsTrue(coverage.All(value => value));
        var resumed = await search.RunAsync(request, Final, null, default);
        Assert.AreEqual(firstCalls, calls); Assert.AreEqual(ParagraphJson.Encode(result), ParagraphJson.Encode(resumed));
        Assert.AreEqual(result.Materials[0].Id, result.Receipts[0].Materials.Single());
    }

    [TestMethod]
    public async Task CancellationRetainsCompletedPassAndNextRunStartsAfterItsRange()
    {
        var request = Request(new string('x', 14000)); using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var search = Search(Fits(4500), (_, _) => { calls++; return Task.FromResult("{\"findings\":[]}"); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => search.RunAsync(request, Final,
            p => { if (p.Stage == "reading" && p.Completed > 0) cancellation.Cancel(); }, cancellation.Token));
        Assert.HasCount(1, Passes()); var before = calls;
        using var saved = JsonDocument.Parse(File.ReadAllText(Passes().Single()));
        var completed = saved.RootElement.GetProperty("End").GetProperty("Offset").GetInt32();
        var firstUnread = -1;
        search = Search(Fits(4500), (messages, _) =>
        {
            using var data = JsonDocument.Parse(messages[^1].Content);
            if (firstUnread < 0) firstUnread = data.RootElement.GetProperty("windows")[0].GetProperty("CoveredStart").GetInt32();
            calls++; return Task.FromResult("{\"findings\":[]}");
        });
        await search.RunAsync(request, Final, null, default);
        Assert.AreEqual(completed, firstUnread); Assert.IsGreaterThan(before, calls);
    }

    [TestMethod]
    public async Task NewQuestionAndChangedSourceCannotReuseOldEmptyEvidence()
    {
        var calls = 0;
        var search = Search(Fits(8000), (_, _) => { calls++; return Task.FromResult("{\"findings\":[]}"); });
        var first = Request("Original source.");
        await search.RunAsync(first, Final, null, default);
        await search.RunAsync(first with { QueryContext = "Another question." }, Final, null, default);
        await search.RunAsync(Request("Changed source."), Final, null, default);
        Assert.AreEqual(3, calls);
        Assert.HasCount(3, Directory.GetDirectories(Path.Combine(_root, "Dialogs", "MemorySearch")));
    }

    [TestMethod]
    public async Task InvalidResponsesUseExactlyThreeRetriesAndNeverBecomeEmpty()
    {
        var calls = 0;
        var search = Search(Fits(8000), (_, _) => { calls++; return Task.FromResult("{broken"); });
        await Assert.ThrowsAsync<LiteraryMemorySearchFailedException>(() => search.RunAsync(Request(new string('a', 2000)), Final, null, default));
        Assert.AreEqual(4, calls); Assert.IsEmpty(Passes());
        using var failure = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(_root, "failure.json", SearchOption.AllDirectories).Single()));
        Assert.AreEqual("failed_not_empty", failure.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(4, failure.RootElement.GetProperty("attempts").GetInt32());
    }

    [TestMethod]
    public async Task InvalidLongReplyShrinksThePortionBeforeRetryingWithoutLosingTheRest()
    {
        var sizes = new List<int>(); var first = true;
        var search = Search(Fits(8000), (messages, _) =>
        {
            using var data = JsonDocument.Parse(messages[^1].Content);
            sizes.Add(data.RootElement.GetProperty("windows")[0].GetProperty("Text").GetString()!.Length);
            if (first) { first = false; return Task.FromResult("{truncated"); }
            return Task.FromResult("{\"findings\":[]}");
        });
        await search.RunAsync(Request(new string('a', 9000)), Final, null, default);
        Assert.IsTrue(sizes[1] < sizes[0]);
        using var last = JsonDocument.Parse(File.ReadAllText(Passes().Order().Last()));
        Assert.AreEqual(1, last.RootElement.GetProperty("End").GetProperty("Source").GetInt32());
    }

    [TestMethod]
    public async Task MinimumContextStopsBeforeInference()
    {
        var calls = 0;
        var search = Search(Fits(200), (_, _) => { calls++; return Task.FromResult("{}"); });
        var error = await Assert.ThrowsAsync<LiteraryMemorySearchMinimumBudgetException>(() =>
            search.RunAsync(Request("A"), Final, null, default));
        Assert.IsNotEmpty(error.Messages); Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task ManyFindingsAreMergedInMeasuredGroupsAndMergeCacheIsReusable()
    {
        const int limit = 4500; var extractionCalls = 0; var mergeCalls = 0;
        var search = Search(Fits(limit), (messages, _) =>
        {
            Assert.IsTrue(Size(messages) <= limit);
            using var data = JsonDocument.Parse(messages[^1].Content);
            if (data.RootElement.TryGetProperty("windows", out var windows))
            {
                extractionCalls++; var window = windows[0]; var quote = window.GetProperty("Text").GetString()![..20];
                return Task.FromResult(ParagraphJson.Encode(new { findings = new[] { new { source = "s1", quote,
                    text = new string('f', 900), objects = Array.Empty<string>() } } }));
            }
            mergeCalls++; var nodes = data.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            var length = nodes.Sum(n => n.GetProperty("text").GetString()!.Length) / 4;
            return Task.FromResult(ParagraphJson.Encode(new { summary = new string('s', Math.Max(1, length)),
                covered = nodes.Select(n => n.GetProperty("id").GetString()).ToArray() }));
        });
        var request = Request(string.Join("\n", Enumerable.Range(0, 1000).Select(i => i.ToString("D6") + new string('x', 40))));
        var result = await search.RunAsync(request, Final, null, default);
        Assert.IsGreaterThan(1, extractionCalls); Assert.IsGreaterThan(0, mergeCalls);
        Assert.IsTrue(Size(Final(result)) <= limit);
        var previous = (extractionCalls, mergeCalls);
        await search.RunAsync(request, Final, null, default);
        Assert.AreEqual(previous, (extractionCalls, mergeCalls));
        Assert.HasCount(extractionCalls, Passes());
    }

    [TestMethod]
    public async Task PartialRagCoverageIsNeverPromotedToWholeLibrary()
    {
        var request = Request("Found excerpt.", kind: "completed_project_fragment", status: "partial");
        var search = Search(Fits(8000), (_, _) => Task.FromResult("{\"findings\":[]}"));
        var result = await search.RunAsync(request, Final, null, default);
        Assert.AreEqual("partial", result.Receipts[0].Status);
        using var data = JsonDocument.Parse(ParagraphJson.Encode(result.Materials[0].Data));
        Assert.IsTrue(data.RootElement.GetProperty("coverage").GetProperty("rankedOrPartialSelection").GetBoolean());
        Assert.IsFalse(ParagraphJson.Encode(result).Contains("completeUnderlyingLibrary", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RepeatedIdenticalQuotesKeepBothCandidateOccurrences()
    {
        const string quote = "Иван вошёл.";
        var search = Search(Fits(8000), (_, _) => Task.FromResult(ParagraphJson.Encode(new
        { findings = new[] { new { source = "s1", quote, text = "Иван вошёл", objects = new[] { "Иван" } } } })));
        var result = await search.RunAsync(Request(quote + " Через час: " + quote), Final, null, default);
        using var data = JsonDocument.Parse(ParagraphJson.Encode(result.Materials[0].Data));
        var objects = data.RootElement.GetProperty("objects").EnumerateArray().ToArray();
        Assert.HasCount(2, objects);
        Assert.AreNotEqual(objects[0].GetProperty("start").GetInt32(), objects[1].GetProperty("start").GetInt32());
    }

    [TestMethod]
    public async Task ConsecutiveReaderPagesKeepEvidenceAcrossTheirBoundary()
    {
        var first = new ParagraphMaterial("chapters/001/0@a", "completed_project_part", "a",
            new { number = 1, revision = "same", offset = 0, text = "Иван был " });
        var second = new ParagraphMaterial("chapters/001/9@b", "completed_project_part", "b",
            new { number = 1, revision = "same", offset = 9, text = "первым старейшиной." });
        var request = new LiteraryMemorySearchRequest(_root, "question", "Кем был Иван?", new([first, second],
            [new("chapters", "Parts", "found", "", [first.Id, second.Id], "")]));
        var calls = 0;
        var search = Search(Fits(8000), (messages, _) =>
        {
            calls++; using var input = JsonDocument.Parse(messages[^1].Content);
            Assert.HasCount(1, input.RootElement.GetProperty("windows").EnumerateArray().ToArray());
            var text = input.RootElement.GetProperty("windows")[0].GetProperty("Text").GetString();
            Assert.AreEqual("Иван был первым старейшиной.", text);
            return Task.FromResult(ParagraphJson.Encode(new { findings = new[] { new { source = "s1", quote = text,
                text = "Иван — первый старейшина.", objects = new[] { "Иван" } } } }));
        });
        await search.RunAsync(request, Final, null, default); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ManySmallMemoryFactsSharePassesInsteadOfOneInferencePerFact()
    {
        var materials = Enumerable.Range(0, 100).Select(i => new ParagraphMaterial("jelly/" + i + "@r", "confirmed_project_memory", "r",
            new { Number = i, Fact = new { Subject = "person-" + i, Value = "present" } })).ToArray();
        var request = new LiteraryMemorySearchRequest(_root, "question", "Who is present?", new(materials,
            [new("jelly", "Memory", "found", "", materials.Select(m => m.Id).ToArray(), "")]));
        var calls = 0;
        var search = Search(Fits(8000), (_, _) => { calls++; return Task.FromResult("{\"findings\":[]}"); });
        await search.RunAsync(request, Final, null, default);
        Assert.IsTrue(calls < 20, "Small facts should be batched into bounded requests.");
        var stored = string.Join("\n", Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("project.json", StringComparison.Ordinal)).Select(File.ReadAllText));
        Assert.IsFalse(stored.Contains("person-99", StringComparison.Ordinal), "Empty source texts should not be retained in checkpoints.");
    }

    [TestMethod]
    public async Task DamagedCheckpointIsNotReclassifiedAsUnreadOrEmpty()
    {
        var request = Request("Actual source."); var calls = 0;
        var search = Search(Fits(8000), (_, _) => { calls++; return Task.FromResult("{\"findings\":[]}"); });
        await search.RunAsync(request, Final, null, default);
        var path = Passes().Single(); File.WriteAllText(path, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => search.RunAsync(request, Final, null, default));
        Assert.AreEqual(1, calls); Assert.AreEqual("{broken", File.ReadAllText(path));
    }

    [TestMethod]
    [DataRow("context")]
    [DataRow("loop")]
    public async Task RuntimeFailuresAfterBudgetCheckStillHaveOnlyThreeRetries(string failure)
    {
        var calls = 0;
        var search = Search(Fits(8000), (_, _) =>
        {
            calls++;
            return Task.FromException<string>(failure == "context" ? new ImageAnalysisContextExhaustedException("Resized window")
                : new LiteraryLoopException(new("repetition", 2, 100)));
        });
        if (failure == "context") await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() =>
            search.RunAsync(Request(new string('a', 12000)), Final, null, default));
        else await Assert.ThrowsAsync<LiteraryMemorySearchFailedException>(() => search.RunAsync(Request(new string('a', 12000)), Final, null, default));
        Assert.AreEqual(4, calls); Assert.IsEmpty(Passes());
    }

    [TestMethod]
    public async Task MergeShrinksItsGroupWhenRuntimeWindowChangedAfterCheck()
    {
        var sizes = new List<int>(); var rejected = false;
        var search = Search(Fits(4500), (messages, _) =>
        {
            using var data = JsonDocument.Parse(messages[^1].Content);
            if (data.RootElement.TryGetProperty("windows", out var windows))
                return Task.FromResult(ParagraphJson.Encode(new { findings = new[] { new { source = "s1",
                    quote = windows[0].GetProperty("Text").GetString()![..20], text = new string('f', 900), objects = Array.Empty<string>() } } }));
            var nodes = data.RootElement.GetProperty("nodes").EnumerateArray().ToArray(); sizes.Add(nodes.Length);
            if (!rejected && nodes.Length > 1) { rejected = true; return Task.FromException<string>(new ImageAnalysisContextExhaustedException("Window changed")); }
            return Task.FromResult(ParagraphJson.Encode(new { summary = new string('s', nodes.Sum(n => n.GetProperty("text").GetString()!.Length) / 4),
                covered = nodes.Select(n => n.GetProperty("id").GetString()).ToArray() }));
        });
        await search.RunAsync(Request(string.Join("\n", Enumerable.Range(0, 300).Select(i => i.ToString("D6") + new string('x', 40)))), Final, null, default);
        Assert.IsTrue(rejected); Assert.IsTrue(sizes[1] < sizes[0]);
    }

    [TestMethod]
    public async Task SourceBookAndChapterRemainVisibleDuringExtractionAndInFinalEvidence()
    {
        var material = new ParagraphMaterial("rag/reference/ref:0/0@revision", "original_book_fragment", "revision",
            new { number = "ref:0", source = "book.docx", section = "Chapter 7", offset = 0, text = "Alden was appointed." });
        var request = new LiteraryMemorySearchRequest(_root, "question", "Who was appointed?", new([material],
            [new("rag", "Source book", "partial", "", [material.Id], "Ranked excerpts.")]));
        var search = Search(Fits(8000), (messages, _) =>
        {
            using var data = JsonDocument.Parse(messages[^1].Content);
            var label = data.RootElement.GetProperty("windows")[0].GetProperty("Label").GetString()!;
            Assert.IsTrue(label.Contains("book.docx") && label.Contains("Chapter 7"));
            return Task.FromResult("{\"findings\":[{\"source\":\"s1\",\"quote\":\"Alden was appointed.\",\"text\":\"Alden was appointed.\",\"objects\":[\"Alden\"]}]}");
        });
        var result = await search.RunAsync(request, Final, null, default);
        using var final = JsonDocument.Parse(ParagraphJson.Encode(result.Materials[0].Data));
        var finalLabel = final.RootElement.GetProperty("sources")[0].GetProperty("Label").GetString()!;
        Assert.IsTrue(finalLabel.Contains("book.docx") && finalLabel.Contains("Chapter 7"));
    }

    [TestMethod]
    public async Task RegistryThatCannotFitIsSavedAndReportedInsteadOfDroppingObjects()
    {
        var names = Enumerable.Range(0, 100).Select(i => "NAME" + i.ToString("D3")).ToArray();
        var text = string.Join(" ", names);
        var search = Search(Fits(4500), (_, _) => Task.FromResult(ParagraphJson.Encode(new
        { findings = new[] { new { source = "s1", quote = text, text = "Named candidates.", objects = names } } })));
        await Assert.ThrowsAsync<LiteraryMemorySearchLimitException>(() => search.RunAsync(Request(text), Final, null, default));
        using var saved = JsonDocument.Parse(File.ReadAllText(Passes().Single()));
        Assert.AreEqual(100, saved.RootElement.GetProperty("Findings")[0].GetProperty("Objects").GetArrayLength());
    }

    [TestMethod]
    public async Task EqualFindingTextAndQuoteRetainTheUnionOfGroundedObjects()
    {
        const string quote = "Иван и Анна пришли.";
        var search = Search(Fits(10000), (_, _) => Task.FromResult(ParagraphJson.Encode(new
        { findings = new[] {
            new { source = "s1", quote, text = "Пришли гости.", objects = new[] { "Иван" } },
            new { source = "s1", quote, text = "Пришли гости.", objects = new[] { "Анна" } } } })));
        var result = await search.RunAsync(Request(quote), Final, null, default);
        using var final = JsonDocument.Parse(ParagraphJson.Encode(result.Materials[0].Data));
        CollectionAssert.AreEquivalent(new[] { "Иван", "Анна" }, final.RootElement.GetProperty("objects")
            .EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToArray());
    }

    private LiteraryMemorySearchRequest Request(string text, string kind = "completed_project_part", string status = "found")
    {
        var material = new ParagraphMaterial("chapters/001/0@revision", kind, "revision", new { number = "001", revision = "book-revision", offset = 0, text });
        return new(_root, "question-fingerprint", "Find relevant facts.", new([material],
            [new("chapters", "Completed parts", status, "", [material.Id], "Selected sources only.")]));
    }
    private string[] Passes() => Directory.GetFiles(_root, "pass-*.json", SearchOption.AllDirectories);
    // These tests isolate reading/merging. Semantic review has dedicated tests with corrections and failures.
    private static LiteraryMemorySearch Search(Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<bool>> fits,
        Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<string>> execute) => new(fits, (messages, token) =>
    {
        using var input = JsonDocument.Parse(messages[^1].Content);
        if (input.RootElement.TryGetProperty("stage", out var stage) && stage.GetString() == "verify_meaning")
            return Task.FromResult(ParagraphJson.Encode(new { findings = input.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => new { reference = i.GetProperty("reference").GetString(), text = i.GetProperty("note").GetString(), objects = i.GetProperty("objects").Clone() }).ToArray() }));
        return execute(messages, token);
    });
    private static int Size(IReadOnlyList<ImageAnalysisHiddenMessage> messages) => messages.Sum(m => m.Content.Length);
    private static Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<bool>> Fits(int limit) =>
        (messages, _) => Task.FromResult(Size(messages) <= limit);
    private static IReadOnlyList<ImageAnalysisHiddenMessage> Final(ParagraphEvidence evidence) =>
        [new() { Role = "user", Content = ParagraphJson.Encode(evidence) }];
}
