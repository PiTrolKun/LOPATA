using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryReadGateTests
{
    private string _temp = "", _root = "", _id = "";
    private LiteraryChapterStore _store = null!;
    [TestInitialize] public void Setup()
    {
        _temp = Path.Combine(Path.GetTempPath(), "lopata-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var entry = new LiteraryProjectStore(Path.Combine(_temp, "registry.json")).Create(_temp,
            new() { ProjectName = "Gate", Genres = ["fantasy"] }, []);
        _root = entry.ProjectPath; _id = entry.Id;
        _store = new(_root); _store.Open(); _store.Save("В истории ключ медный. Код НЕФРИТ-523."); _store.Finish();
        _store.Save("Дисковый черновик.");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
    private LiteraryEditorSnapshot Snapshot() => LiteraryEditorSnapshot.Capture(_id, _root, _store.Index, "Сейчас ключ зелёный.", true);
    private static ImageAnalysisHiddenMessage[] Baseline() => [new() { Role = "system", Content = "role" }, new() { Role = "user", Content = "Задание автора о части 001 без изменений." }];
    private static string Action(string action, string number = "", string query = "") => JsonSerializer.Serialize(new { action, number, offset = 0, query });
    private static Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<string>> Route(string scope, string part = "") =>
        (_, _) => Task.FromResult(JsonSerializer.Serialize(new { scope, projectQuery = "ключ", referenceQuery = "ключ", partNumber = part }));
    private void Reference()
    {
        var layout = new LiteraryProjectLayout(_root);
        var folder = layout.EnsureFolder("Rag/Source"); layout.EnsureFolder("Materials");
        var text = "В оригинале ключ серебряный. Код ЯНТАРЬ-684.";
        File.WriteAllText(Path.Combine(_root, "Materials", "0001_book.txt"), text);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "Materials", "0001_book.txt"))));
        File.WriteAllText(Path.Combine(folder, "sources.json"), JsonSerializer.Serialize(new[] { new { file = "book.txt", sha256 = hash } }));
        File.WriteAllText(Path.Combine(folder, "text.json"), JsonSerializer.Serialize(new[] { new { source = "book.txt", section = "Chapter", text } }));
    }

    [TestMethod]
    [DataRow(LiteraryChatProfile.Writer)] [DataRow(LiteraryChatProfile.Advisor)]
    public async Task RequiredHistoryIsReadBeforeImmediateAnswer(LiteraryChatProfile role)
    {
        var reads = 0;
        var session = new LiteraryReadingSession(new(Snapshot()), (_, _) => Task.FromResult(true),
            (kind, _) => { if (kind == "source_read") reads++; }, role);
        var result = await session.PrepareAsync(Baseline(), (_, _) => Task.FromResult(Action("answer")), null, default, Route("project", "001"));
        Assert.AreEqual(1, reads); Assert.IsFalse(session.Limited);
        StringAssert.Contains(result[^1].Content, "НЕФРИТ-523");
        StringAssert.Contains(result[^1].Content, "Сейчас ключ зелёный.");
        StringAssert.Contains(result[^1].Content, "\"missing\":[]");
        Assert.IsTrue(result[^1].Content.EndsWith(Baseline()[^1].Content));
    }

    [TestMethod] public async Task LocalEditorDoesNotCallActionPlannerOrSearch()
    {
        var session = new LiteraryReadingSession(new(Snapshot()), (_, _) => Task.FromResult(true),
            (kind, _) => Assert.AreNotEqual("source_read", kind));
        var result = await session.PrepareAsync(Baseline(), (_, _) => throw new AssertFailedException("Unexpected planner"), null, default, Route("editor"));
        StringAssert.Contains(result[^1].Content, "Сейчас ключ зелёный.");
        Assert.IsFalse(result[^1].Content.Contains("НЕФРИТ-523"));
    }

    [TestMethod] public async Task MissingVectorIndexCanFallBackToRealProjectFile()
    {
        var snapshot = Snapshot(); var count = 0;
        var session = new LiteraryReadingSession(new(snapshot), (_, _) => Task.FromResult(true), (_, _) => { }, rag: new(snapshot));
        var result = await session.PrepareAsync(Baseline(), (_, _) =>
        {
            count++;
            var schema = session.StepResponseFormat().ToJsonString();
            if (count == 1) { Assert.IsFalse(schema.Contains("\"answer\"")); return Task.FromResult(Action("read", "001")); }
            return Task.FromResult(Action("answer"));
        }, null, default, Route("project"));
        Assert.AreEqual(2, count);
        StringAssert.Contains(result[^1].Content, "НЕФРИТ-523");
        StringAssert.Contains(result[^1].Content, "\"missing\":[]");
    }

    [TestMethod] public async Task CatalogAndRepeatedEmptyReadsNeverBecomeEvidence()
    {
        Reference(); var snapshot = Snapshot(); var reads = 0;
        var session = new LiteraryReadingSession(new(snapshot), (_, _) => Task.FromResult(true),
            (kind, _) => { if (kind == "source_read") reads++; }, rag: new(snapshot));
        var result = await session.PrepareAsync(Baseline(), (_, _) => Task.FromResult(
            session.StepResponseFormat().ToJsonString().Contains("\"answer\"") ? Action("answer") : Action("semantic_reference", query: "неизвестное")),
            null, default, Route("reference"));
        Assert.AreEqual(3, reads); Assert.IsTrue(session.Limited);
        StringAssert.Contains(result[^1].Content, "\"missing\":[\"reference\"]");
        Assert.IsFalse(result[^1].Content.Contains("ЯНТАРЬ-684"));
    }

    [TestMethod] public async Task BothCorporaRequireSeparateRetainedText()
    {
        Reference(); var snapshot = Snapshot(); var plan = 0;
        var session = new LiteraryReadingSession(new(snapshot), (_, _) => Task.FromResult(true), (_, _) => { }, rag: new(snapshot));
        var result = await session.PrepareAsync(Baseline(), (_, _) =>
        {
            plan++;
            if (plan == 1)
            {
                var schema = session.StepResponseFormat().ToJsonString();
                Assert.IsTrue(schema.Contains("read_reference")); Assert.IsFalse(schema.Contains("semantic_project"));
                return Task.FromResult(Action("read_reference", "ref:0"));
            }
            return Task.FromResult(Action("answer"));
        }, null, default, Route("both", "001"));
        StringAssert.Contains(result[^1].Content, "НЕФРИТ-523"); StringAssert.Contains(result[^1].Content, "ЯНТАРЬ-684");
        StringAssert.Contains(result[^1].Content, "\"missing\":[]");
    }

    [TestMethod] public async Task DisallowedAnswerDoesNotBypassMissingEvidence()
    {
        var session = new LiteraryReadingSession(new(Snapshot()), (_, _) => Task.FromResult(true), (_, _) => { });
        await Assert.ThrowsAsync<JsonException>(() => session.PrepareAsync(Baseline(), (_, _) => Task.FromResult(Action("answer")), null, default, Route("reference")));
    }

    [TestMethod] public async Task EvictedHistoryCannotRemainMarkedAsRead()
    {
        var session = new LiteraryReadingSession(new(Snapshot()), (m, _) => Task.FromResult(!m.Any(x => x.Content.Contains("НЕФРИТ-523"))), (_, _) => { });
        var result = await session.PrepareAsync(Baseline(), (_, _) => Task.FromResult(
            session.StepResponseFormat().ToJsonString().Contains("\"answer\"") ? Action("answer") : Action("read", "001")), null, default, Route("project", "001"));
        Assert.IsTrue(session.Limited); StringAssert.Contains(result[^1].Content, "\"missing\":[\"project\"]");
        Assert.IsFalse(result[^1].Content.Contains("НЕФРИТ-523"));
    }

    [TestMethod] public void ActiveDraftAndSearchSummaryAreNotHistoryEvidence()
    {
        var material = new[] { new LiteraryReadResult("x", "{\"kind\":\"fragment\",\"state\":\"working_draft\",\"text\":\"draft\"}"),
            new LiteraryReadResult("y", "{\"kind\":\"search_summary\",\"found\":5}") };
        Assert.IsFalse(LiteraryReadGate.Has(material, "project")); Assert.IsFalse(LiteraryReadGate.Has(material, "reference"));
        Assert.Throws<JsonException>(() => LiteraryReadRouting.Parse("{\"scope\":\"project\",\"partNumber\":\"002\",\"projectQuery\":\"x\",\"referenceQuery\":\"\"}", Snapshot()));
    }

    [TestMethod] public void OptionalPartCannotInventExplicitAuthorReference()
    {
        string Json(string scope, string part) => JsonSerializer.Serialize(new { scope, partNumber = part, projectQuery = "ключ", referenceQuery = "ключ" });
        Assert.AreEqual("", LiteraryReadRouting.Parse(Json("reference", "ref:0"), Snapshot(), "Прочитай оригинал.").PartNumber);
        Assert.AreEqual("", LiteraryReadRouting.Parse(Json("project", "001"), Snapshot(), "Сравни с прошлой историей.").PartNumber);
        Assert.AreEqual("001", LiteraryReadRouting.Parse(Json("project", "001"), Snapshot(), "Прочитай [001].").PartNumber);
        Assert.AreEqual("", LiteraryReadRouting.Parse(Json("project", "001"), Snapshot(), "Прочитай 001.2.").PartNumber);
        var schema = LiteraryReadRouting.ResponseFormat(Snapshot()).ToJsonString();
        StringAssert.Contains(schema, "001"); Assert.IsFalse(schema.Contains("002"));
    }

    [TestMethod] public async Task DamagedReferenceDoesNotBlockProjectSearch()
    {
        Reference(); File.AppendAllText(Path.Combine(_root, "Materials", "0001_book.txt"), "изменено");
        var reader = new LiteraryRagReader(Snapshot());
        var reference = await reader.ExecuteAsync(new("list_reference"), default);
        StringAssert.Contains(reference.Json, "Reference file changed");
        var history = await reader.ExecuteAsync(new("semantic_project", Query: "ключ"), default);
        Assert.IsFalse(history.Json.Contains("Reference file changed"));
        var direct = new LiteraryProjectReader(Snapshot()).Execute(new("read", "001"), default);
        StringAssert.Contains(direct.Json, "НЕФРИТ-523");
    }

    [TestMethod] public async Task CancellationDuringRoutingPreventsAllFurtherSteps()
    {
        using var cancel = new CancellationTokenSource();
        var session = new LiteraryReadingSession(new(Snapshot()), (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); },
            (kind, _) => Assert.AreNotEqual("source_read", kind));
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.PrepareAsync(Baseline(),
            (_, _) => throw new AssertFailedException("Planner ran after cancellation"), null, cancel.Token,
            (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(""); }));
    }
}
