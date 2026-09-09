using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryReadingTests
{
    private string _root = "";
    private LiteraryChapterStore _store = null!;
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-reading-" + Guid.NewGuid().ToString("N"));
        _store = new(_root); _store.Open(); _store.Save("В прошлом ключ был медным. Код САПФИР-619."); _store.Finish();
        _store.Save("На диске ключ синий.");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private LiteraryEditorSnapshot Capture(string text = "В окне ключ зелёный.") => LiteraryEditorSnapshot.Capture("project", _root, _store.Index, text, true);
    private static ImageAnalysisHiddenMessage[] Baseline(string text = "draft") =>
        [new() { Role = "system", Content = text }, new() { Role = "user", Content = "question" }];

    [TestMethod] public void DraftAlwaysWinsOverDiskAndRevisionChangesWithoutSaving()
    {
        var first = Capture(); var second = Capture("Ключ красный.");
        Assert.AreNotEqual(first.Revision, second.Revision);
        var result = new LiteraryProjectReader(first).Execute(new("read", "002"), default);
        using var json = JsonDocument.Parse(result.Json);
        Assert.AreEqual(first.Text, json.RootElement.GetProperty("text").GetString());
        Assert.AreEqual("working_draft", json.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("На диске ключ синий.", _store.Load());
        Assert.AreNotEqual(Capture("").Revision, first.Revision);
    }

    [TestMethod] public void HistoryReadsActualFilesAndSearchReturnsEvidence()
    {
        var reader = new LiteraryProjectReader(Capture());
        StringAssert.Contains(reader.Execute(new("read", "001"), default).Json, "САПФИР-619");
        StringAssert.Contains(reader.Execute(new("search", Query: "САПФИР"), default).Json, "001");
        Assert.IsFalse(reader.Execute(new("read", "../project.json"), default).Json.Contains("САПФИР"));
        File.Delete(Path.Combine(_root, "chapters", _store.Index.Parts[0].FileName));
        StringAssert.Contains(reader.Execute(new("read", "001"), default).Json, "error");
    }

    [TestMethod] public void ReaderDoesNotMutateAnyProjectFilesAndDetectsStructureChange()
    {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var reader = new LiteraryProjectReader(Capture());
        reader.Execute(new("read", "001"), default); reader.Execute(new("list"), default);
        foreach (var file in before) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key));
        Assert.AreEqual(before.Count, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        _store.Finish();
        Assert.Throws<IOException>(() => reader.Execute(new("read", "001"), default));
    }

    [TestMethod] public void FragmentsCanBeReassembledAndCorruptionIsNotEmptyText()
    {
        var path = Path.Combine(_root, "chapters", _store.Index.Parts[0].FileName);
        var text = new string('я', 5100); File.WriteAllText(path, text);
        var reader = new LiteraryProjectReader(Capture()); var rebuilt = ""; int? offset = 0;
        while (offset is not null)
        {
            using var json = JsonDocument.Parse(reader.Execute(new("read", "001", offset.Value), default).Json);
            rebuilt += json.RootElement.GetProperty("text").GetString();
            offset = json.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : json.RootElement.GetProperty("nextOffset").GetInt32();
        }
        Assert.AreEqual(text, rebuilt);
        File.WriteAllBytes(path, [0xff, 0xfe]);
        StringAssert.Contains(reader.Execute(new("read", "001"), default).Json, "error");
    }

    [TestMethod] public void CancellationAndInvalidActionsAreRejected()
    {
        var reader = new LiteraryProjectReader(Capture());
        Assert.Throws<OperationCanceledException>(() => reader.Execute(new("search", Query: "x"), new CancellationToken(true)));
        Assert.Throws<System.IO.InvalidDataException>(() => reader.Execute(new("list", Offset: -1), default));
        Assert.Throws<JsonException>(() => LiteraryReadingSession.Parse("{\"action\":\"write\",\"number\":\"001\",\"offset\":0,\"query\":\"\"}"));
    }

    [TestMethod] public async Task SessionIncludesOnlyLatestDraftAndStopsRepeatedActions()
    {
        var snapshot = Capture(); var session = new LiteraryReadingSession(new(snapshot), (_, _) => Task.FromResult(true), (_, _) => { });
        var messages = await session.PrepareAsync(Baseline(), (_, _) => Task.FromResult("{\"action\":\"read\",\"number\":\"001\",\"offset\":0,\"query\":\"\"}"), null, default);
        Assert.IsFalse(session.Limited); // A repeated read of an already complete part needs no new operation.
        StringAssert.Contains(messages[^1].Content, "САПФИР-619");
        Assert.AreEqual(1, string.Join("\n", messages.Select(m => m.Content)).Split(snapshot.Text).Length - 1);
        Assert.IsFalse(messages[^1].Content.Contains("На диске ключ синий."));
    }

    [TestMethod] public async Task MandatoryContextIsNeverSilentlyTruncated()
    {
        var session = new LiteraryReadingSession(new(Capture()), (_, _) => Task.FromResult(false), (_, _) => { });
        await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => session.PrepareAsync(Baseline(),
            (_, _) => throw new AssertFailedException("Inference must not start when the mandatory context does not fit."), null, default));
    }

    [TestMethod] public void ContinuationIsHistoryEvenBeforeChapterFinished()
    {
        _store.Continue(["продолжение"]);
        var reader = new LiteraryProjectReader(Capture("новое"));
        using var json = JsonDocument.Parse(reader.Execute(new("read", "002"), default).Json);
        Assert.AreEqual("history", json.RootElement.GetProperty("state").GetString());
        Assert.IsFalse(_store.Index.Parts.Single(p => p.Chapter == 2 && p.Part == 1).Finished);
    }

    [TestMethod] public void SearchPagesDoNotLoseMatchesAndRussianEndingsAreMarkedApproximate()
    {
        _store.Save("Хранителя зовут Игнат.");
        for (var i = 0; i < 16; i++) _store.Continue(["Хранителя зовут Игнат."]);
        var reader = new LiteraryProjectReader(Capture("Хранителя зовут Игнат."));
        var found = new HashSet<string>(); int? offset = 0;
        while (offset is not null)
        {
            using var json = JsonDocument.Parse(reader.Execute(new("search", Offset: offset.Value, Query: "хранитель"), default).Json);
            foreach (var match in json.RootElement.GetProperty("matches").EnumerateArray())
            { Assert.IsTrue(match.GetProperty("approximate").GetBoolean()); found.Add(match.GetProperty("number").GetString()!); }
            offset = json.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : json.RootElement.GetProperty("nextOffset").GetInt32();
        }
        Assert.AreEqual(17, found.Count);
        using var catalog = JsonDocument.Parse(reader.Execute(new("list"), default).Json);
        Assert.AreEqual(12, catalog.RootElement.GetProperty("parts").GetArrayLength());
        Assert.AreEqual(12, catalog.RootElement.GetProperty("nextOffset").GetInt32());
    }

    [TestMethod] public void ForeignProjectAndUnregisteredFilesCannotBeRead()
    {
        var foreignRoot = Path.Combine(_root, "foreign"); var foreign = new LiteraryChapterStore(foreignRoot); foreign.Open(); foreign.Save("SECRET-872");
        var first = Capture();
        var mismatched = first with { Directory = foreignRoot };
        Assert.Throws<LiterarySourceException>(() => new LiteraryProjectReader(mismatched).Execute(new("read", "001"), default));
        File.WriteAllText(Path.Combine(_root, "chapters", "unregistered.txt"), "SECRET-993");
        var reader = new LiteraryProjectReader(first);
        Assert.IsFalse(reader.Execute(new("search", Query: "SECRET"), default).Json.Contains("SECRET-993"));
    }

    [TestMethod] public async Task ContextEvictionRetainsMandatorySnapshotAndTask()
    {
        var snapshot = Capture(); var decisions = 0;
        var session = new LiteraryReadingSession(new(snapshot), (messages, _) =>
            Task.FromResult(!messages.Any(m => m.Content.Contains("catalog"))), (_, _) => { });
        var result = await session.PrepareAsync(Baseline(), (_, _) =>
        { decisions++; return Task.FromResult("{\"action\":\"answer\",\"number\":\"\",\"offset\":0,\"query\":\"\"}"); }, null, default);
        Assert.AreEqual(1, decisions); Assert.IsTrue(session.Limited);
        StringAssert.Contains(result[^1].Content, snapshot.Text);
        StringAssert.Contains(result[^1].Content, "question");
    }

    [TestMethod] public async Task PartIsReadToEndWithoutAskingModelToCalculateOffsets()
    {
        File.WriteAllText(Path.Combine(_root,"chapters",_store.Index.Parts[0].FileName), new string('я', 5800) + "КОНЕЦ-947");
        var reads = 0;
        var session = new LiteraryReadingSession(new(Capture()), (_, _) => Task.FromResult(true), (kind, _) => { if (kind == "source_read") reads++; });
        var result = await session.PrepareAsync(Baseline(), (_, _) => Task.FromResult("{\"action\":\"read\",\"number\":\"001\",\"offset\":0,\"query\":\"\"}"), null, default);
        Assert.AreEqual(3, reads); Assert.IsFalse(session.Limited);
        StringAssert.Contains(result[^1].Content,"КОНЕЦ-947");
    }

    [TestMethod]
    [DataRow(LiteraryChatProfile.Writer)]
    [DataRow(LiteraryChatProfile.Advisor)]
    public async Task ReadingPipelinePreservesRoleContractAndNumbering(LiteraryChatProfile role)
    {
        var snapshot = Capture();
        var baseline = LiteraryModelPolicy.Messages(role,
            [new() { Role = "user", Content = "Продолжи сцену." }], snapshot.Text, new(), includeDraft: false);
        ImageAnalysisHiddenMessage[]? planning = null;
        var session = new LiteraryReadingSession(new(snapshot), (_, _) => Task.FromResult(true), (_, _) => { }, role);
        var final = await session.PrepareAsync(baseline, (messages, _) =>
        {
            planning = messages.ToArray();
            return Task.FromResult("{\"action\":\"answer\",\"number\":\"\",\"offset\":0,\"query\":\"\"}");
        }, null, default);
        Assert.IsNotNull(planning);
        StringAssert.Contains(planning[0].Content, "Ты диспетчер чтения файлов");
        StringAssert.Contains(planning[0].Content, "не альтернативные версии");
        StringAssert.Contains(final[0].Content, "[002.10] идёт после [002.9]");
        StringAssert.Contains(final[^1].Content, snapshot.Text);
        if (role == LiteraryChatProfile.Writer)
        {
            StringAssert.Contains(final[0].Content, "только текст произведения");
            Assert.IsFalse(final[0].Content.Contains("При обсуждении истории указывай номер части"));
            Assert.IsFalse(final[0].Content.Contains("Если задан вопрос о тексте — отвечай"));
        }
        else
        {
            StringAssert.Contains(final[0].Content, "давай обоснованные советы");
            StringAssert.Contains(final[0].Content, "При обсуждении истории указывай номер части");
            Assert.IsFalse(final[0].Content.Contains("Выдавай только текст произведения"));
        }
    }
}
