using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemoryReviewTests
{
    private string _root = "";
    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "MemoryReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(new LiteraryProject()));
    }
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void ParaphraseAndInflectionDoNotTriggerProgrammaticSemanticRejection()
    {
        const string source = "**Тигр вышел.**\nСекта получила нового старейшину.";
        var result = LiteraryMemorySearchPrompts.ParseExtraction(Extract("Тигр стал новым наставником", "новый старейшина"),
            [new("s1", "completed_project_part", "Глава", "source_text_utf16", 200, 200, source)]).Single();
        Assert.AreEqual("Тигр стал новым наставником", result.Text);
        CollectionAssert.AreEqual(new[] { "новый старейшина" }, result.Objects);
        Assert.AreEqual(source, result.Quote); Assert.AreEqual(200, result.Start); Assert.AreEqual(200 + source.Length, result.End);
    }

    [TestMethod]
    public void UnknownSourceStillFailsAsBrokenReference()
    {
        Assert.Throws<InvalidDataException>(() => LiteraryMemorySearchPrompts.ParseExtraction(Extract("Свободный пересказ").Replace("s1", "s9"),
            [new("s1", "part", "Глава", "source_text_utf16", 0, 0, "Исходник")]));
    }

    [TestMethod]
    public async Task ModelRechecksOriginalAfterReadingAndCanCorrectMeaningFreely()
    {
        const string source = "**Иван не стал старейшиной.**\nОн отказался.";
        var calls = new List<string>();
        var search = Search((input, _) =>
        {
            if (!Review(input)) { calls.Add("read"); return Task.FromResult(Extract("Иван стал старейшиной", "Иван")); }
            calls.Add("verify"); var item = input.GetProperty("items")[0];
            Assert.AreEqual(source, item.GetProperty("original").GetString());
            Assert.IsTrue(File.Exists(Directory.GetFiles(_root, "pass-*.json", SearchOption.AllDirectories).Single()));
            return Task.FromResult(Reviewed(item, "Он отказался от должности", "Иван — отказавшийся кандидат"));
        });
        var result = await search.RunAsync(Request(source), Final, null, default);
        CollectionAssert.AreEqual(new[] { "read", "verify" }, calls);
        var json = ParagraphJson.Encode(result);
        Assert.IsTrue(json.Contains("Он отказался от должности"));
        Assert.IsFalse(json.Contains("Иван стал старейшиной"));
        var before = calls.Count;
        await search.RunAsync(Request(source), Final, null, default);
        Assert.AreEqual(before, calls.Count);
    }

    [TestMethod]
    public async Task ModelMayRemoveUnsupportedNotesWithoutTreatingItAsFailure()
    {
        var search = Search((input, _) => Task.FromResult(Review(input) ? "{\"findings\":[]}" : Extract("Ошибочная заметка", "Нет в источнике")));
        var result = await search.RunAsync(Request("Книга о другом."), Final, null, default);
        Assert.IsFalse(ParagraphJson.Encode(result).Contains("Ошибочная заметка"));
        Assert.HasCount(1, Directory.GetFiles(_root, "review-*.json", SearchOption.AllDirectories));
        Assert.HasCount(1, Directory.GetFiles(_root, "pass-*.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ReviewTechnicalFailureHasThreeRetriesAndKeepsDraftForResume()
    {
        var reads = 0; var checks = 0;
        var request = Request("Иван пришёл.");
        var search = Search((input, _) =>
        {
            if (Review(input)) { checks++; return Task.FromResult("{broken"); }
            reads++; return Task.FromResult(Extract("Пришёл Иван", "Иван"));
        });
        var error = await Assert.ThrowsAsync<LiteraryMemorySearchFailedException>(() => search.RunAsync(request, Final, null, default));
        Assert.AreEqual("verify", error.Stage); Assert.AreEqual(4, checks); Assert.AreEqual(1, reads);
        var saved = Directory.GetFiles(_root, "pass-*.json", SearchOption.AllDirectories).Single(); var bytes = File.ReadAllBytes(saved);
        search = Search((input, _) =>
        {
            Assert.IsTrue(Review(input)); return Task.FromResult(Reviewed(input.GetProperty("items")[0], "Иван пришёл", "Иван"));
        });
        await search.RunAsync(request, Final, null, default);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(saved));
    }

    [TestMethod]
    public async Task UnknownReviewReferenceNeverBecomesSuccessfulEmptyResult()
    {
        var search = Search((input, _) => Task.FromResult(Review(input)
            ? "{\"findings\":[{\"reference\":\"invented\",\"text\":\"claim\",\"objects\":[]}]}" : Extract("Заметка")));
        await Assert.ThrowsAsync<LiteraryMemorySearchFailedException>(() => search.RunAsync(Request("Текст."), Final, null, default));
        Assert.IsEmpty(Directory.GetFiles(_root, "review-*.json", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(_root, "complete.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task CancellationPreservesReviewedGroupsAndResumesOnlyRemainingOnes()
    {
        using var cancellation = new CancellationTokenSource();
        var reviewCalls = 0;
        var request = Request("Иван пришёл.");
        var search = Search((input, _) =>
        {
            if (!Review(input)) return Task.FromResult(ParagraphJson.Encode(new { findings = Enumerable.Range(0, 9).Select(i =>
                new { source = "s1", quote = "Иван", text = "Наблюдение " + i, objects = new[] { "Иван" } }).ToArray() }));
            reviewCalls++;
            if (reviewCalls == 2) return Task.FromCanceled<string>(new CancellationToken(true));
            return Task.FromResult(Echo(input));
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => search.RunAsync(request, Final, null, cancellation.Token));
        Assert.HasCount(1, Directory.GetFiles(_root, "review-*.json", SearchOption.AllDirectories));
        var path = Directory.GetFiles(_root, "review-*.json", SearchOption.AllDirectories).Single(); var bytes = File.ReadAllBytes(path);
        var remaining = 0;
        search = Search((input, _) => { Assert.IsTrue(Review(input)); remaining++; return Task.FromResult(Echo(input)); });
        await search.RunAsync(request, Final, null, default);
        Assert.AreEqual(1, remaining); CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task TooSmallReviewBudgetDoesNotDiscardTheDraft()
    {
        var calls = 0;
        var search = new LiteraryMemorySearch((messages, _) => Task.FromResult(!messages[^1].Content.Contains("verify_meaning")),
            (_, _) => { calls++; return Task.FromResult(Extract("Пересказ")); });
        await Assert.ThrowsAsync<LiteraryMemorySearchMinimumBudgetException>(() => search.RunAsync(Request("Исходный текст"), Final, null, default));
        Assert.AreEqual(1, calls); Assert.HasCount(1, Directory.GetFiles(_root, "pass-*.json", SearchOption.AllDirectories));
    }

    private LiteraryMemorySearch Search(Func<JsonElement, CancellationToken, Task<string>> execute) => new((_, _) => Task.FromResult(true),
        async (messages, token) => { using var input = JsonDocument.Parse(messages[^1].Content); return await execute(input.RootElement, token); });
    private LiteraryMemorySearchRequest Request(string text)
    {
        var material = new ParagraphMaterial("chapter/1", "completed_project_part", "Глава", new { text, number = 1, revision = "same", offset = 0 });
        return new(_root, "fingerprint", "Кто старейшина?", new([material], [new("chapters", "Главы", "found", "", [material.Id], "")]));
    }
    private static bool Review(JsonElement input) => input.TryGetProperty("stage", out var stage) && stage.GetString() == "verify_meaning";
    private static string Extract(string text, params string[] objects) => ParagraphJson.Encode(new { findings = new[] { new { source = "s1", quote = "Свободно записанная цитата", text, objects } } });
    private static string Reviewed(JsonElement item, string text, params string[] objects) => ParagraphJson.Encode(new { findings = new[] { new { reference = item.GetProperty("reference").GetString(), text, objects } } });
    private static string Echo(JsonElement input) => ParagraphJson.Encode(new { findings = input.GetProperty("items").EnumerateArray().Select(i => new { reference = i.GetProperty("reference").GetString(), text = i.GetProperty("note").GetString(), objects = i.GetProperty("objects").Clone() }).ToArray() });
    private static IReadOnlyList<ImageAnalysisHiddenMessage> Final(ParagraphEvidence evidence) => [new() { Role = "user", Content = ParagraphJson.Encode(evidence) }];
}
