using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryImportTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "lopata-import-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (_root.StartsWith(Path.Combine(Path.GetTempPath(), "lopata-import-tests-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true); }
    private string Source(string json = "[]") { var path = Path.Combine(_root, "input.json"); File.WriteAllText(path, json, new UTF8Encoding(false)); return path; }
    private static ImportUnit Unit(string id, string text, int offset = 0) => new(id, "c", "m", "", "RESPONSE", 0, offset, text, false);
    private static string Native() => JsonSerializer.Serialize(new[] { new { id = "c", title = "Synthetic", mapping = new Dictionary<string, object>
    {
        ["last"] = new { parent = "first", message = new { inserted_at = "2026-01-02", fragments = new[] { new { type = "RESPONSE", content = "Конец -[литературная пометка]- 😀" } } } },
        ["first"] = new { parent = "", message = new { inserted_at = "2026-01-01", fragments = new[] { new { type = "REQUEST", content = "Глава 1\nНачало" }, new { type = "THINK", content = "private reasoning" } } } }
    } } });
    [TestMethod] public async Task ZipAndJsonPreserveTextBranchesAndParentOrder()
    {
        var json = Native(); var source = Source(json);
        using var plain = ImportSession.Create(_root, source, default);
        var a = await DeepSeekImportReader.ReadAsync(plain, default);
        var zip = Path.Combine(_root, "input.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("export/conversations.json").Open(), new UTF8Encoding(false)); writer.Write(json); }
        using var zipped = ImportSession.Create(_root, zip, default);
        var b = await DeepSeekImportReader.ReadAsync(zipped, default);
        CollectionAssert.AreEqual(a.Units.ToArray(), b.Units.ToArray());
        Assert.AreEqual("first", a.Units[0].Message); Assert.AreEqual("last", a.Units[^1].Message);
        Assert.IsTrue(a.Units.Any(u => u.Technical)); Assert.IsTrue(a.Units[^1].Text.Contains("-[литературная пометка]-"));
        Assert.AreEqual(json, File.ReadAllText(plain.Source));
    }
    [TestMethod] public void RejectsUnsafeWindowsNames()
    {
        foreach (var name in new[] { "../file.json", "/file.json", "C:/file.json", "dir/CON.txt", "dir/file. ", "dir/a:stream" })
            Assert.Throws<InvalidDataException>(() => DeepSeekImportReader.ValidateName(name));
    }
    [TestMethod] public async Task LinkedZipEntryAndExcessiveJsonDepthAreRejected()
    {
        var zip = Path.Combine(_root, "link.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("conversations.json").ExternalAttributes = unchecked((int)0xA1FF0000);
        using var linked = ImportSession.Create(_root, zip, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeepSeekImportReader.ReadAsync(linked, default));
        var deep = new string('[', 129) + "0" + new string(']', 129);
        using var nested = ImportSession.Create(_root, Source(deep), default);
        await Assert.ThrowsAsync<JsonException>(() => DeepSeekImportReader.ReadAsync(nested, default));
    }
    [TestMethod] public async Task DuplicateWindowsNamesAndGraphCyclesAreRejected()
    {
        var zip = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { archive.CreateEntry("a.json"); archive.CreateEntry("A.json"); }
        using var s = ImportSession.Create(_root, zip, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeepSeekImportReader.ReadAsync(s, default));
        var cyclic = Native().Replace("\"parent\":\"\"", "\"parent\":\"last\"");
        using var cycle = ImportSession.Create(_root, Source(cyclic), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeepSeekImportReader.ReadAsync(cycle, default));
    }
    [TestMethod] public void LongParagraphAndUnicodeRoundTripWithoutNewlines()
    {
        var text = new string('я', 7499) + "😀" + new string('z', 16000) + "\r\nХвост";
        var chunks = DeepSeekImportReader.SplitExact(text, 7500).ToArray();
        Assert.IsTrue(chunks.All(c => c.Length <= 7500 && !char.IsHighSurrogate(c[^1]) && !char.IsLowSurrogate(c[0])));
        Assert.AreEqual(text, string.Concat(chunks));
    }
    [TestMethod] public void ParseRejectsOmissionDuplicateAndDroppedHeading()
    {
        var units = new[] { Unit("a", "Глава 1"), Unit("b", "Конец") };
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[]}", units, true));
        string Row(int i, string kind) => JsonSerializer.Serialize(new { i, kind, project = "x", chapter = "1", reason = "synthetic" });
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[" + Row(0, "MAIN") + "," + Row(0, "MAIN") + "]}", units, true));
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[" + Row(0, "DROP") + "," + Row(1, "MAIN") + "]}", units, true));
        var retained = ImportPipeline.Parse("{\"units\":[" + Row(0, "DROP") + "," + Row(1, "MAIN") + "]}", units, true, preserveHeadings: true);
        Assert.AreEqual("DOUBT", retained[0].Kind);
    }
    [TestMethod] public void InterruptedRawResponseRecoveredAndTamperingDetected()
    {
        string folder; string artifact;
        using (var s = ImportSession.Create(_root, Source(), default))
        {
            folder = s.Root; var raw = s.BeginRaw("model/raw-response", "request-id");
            using (raw.Stream) { raw.Stream.Write(Encoding.UTF8.GetBytes("data: partial\r\n")); raw.Stream.Flush(true); }
            artifact = raw.Name;
        }
        using (var resumed = ImportSession.Open(folder))
        { Assert.AreEqual("interrupted", resumed.State.Artifacts.Single().Status); Assert.AreEqual("model/raw-response", resumed.State.Artifacts.Single().Step); }
        File.AppendAllText(Path.Combine(folder, artifact), "tampered");
        Assert.Throws<InvalidDataException>(() => ImportSession.Open(folder));
    }
    [TestMethod] public async Task CapturePersistsBytesBeforeDecoderOrCancellation()
    {
        byte[] bytes = [0xFF, 0xC3, 0x0D, 0x0A, 0x00, 0x41];
        using var input = new MemoryStream(bytes); using var saved = new MemoryStream();
        using var capture = new ImportCaptureStream(input, saved);
        var buffer = new byte[4]; Assert.AreEqual(4, await capture.ReadAsync(buffer));
        CollectionAssert.AreEqual(bytes[..4], saved.ToArray());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => Assert.AreEqual(0, await capture.ReadAsync(buffer, cancelled.Token)));
        CollectionAssert.AreEqual(bytes[..4], saved.ToArray());
        Assert.AreEqual(2, await capture.ReadAsync(buffer));
        CollectionAssert.AreEqual(bytes, saved.ToArray());
    }
    [TestMethod] public void WorkbookRecoversExactBytesAcrossCellsAndControls()
    {
        using var session = ImportSession.Create(_root, Source(), default);
        var text = "=SUM(1,2)\r\n0012\0\u0001" + string.Concat(Enumerable.Repeat("Я😀\r\n", 20000));
        session.Add("synthetic/raw-response", text); session.AddBytes("empty", []);
        var path = Path.Combine(_root, "history.xlsx"); ImportBlackBox.Export(session, path, default);
        ImportBlackBox.Verify(path, session.State.Artifacts, default);
        using var book = SpreadsheetDocument.Open(path, false);
        Assert.HasCount(0, new OpenXmlValidator().Validate(book).ToArray());
        Assert.IsFalse(book.WorkbookPart!.WorksheetParts.Any(p => p.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>().Any()));
        // Independent ZIP/XML reader: recovery uses only workbook contents, not session metadata or SDK objects.
        using var zip = ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml"))
            .SelectMany(e => { using var stream = e.Open(); return System.Xml.Linq.XDocument.Load(stream)
                .Descendants(ns + "row").Select(r => r.Elements(ns + "c").Select(c => string.Concat(c.Descendants(ns + "t").Select(t => t.Value))).ToArray()).ToArray(); }).ToArray();
        var manifest = rows.Where(r => r.Length == 7 && r[0] != "ID").ToDictionary(r => r[0]);
        foreach (var group in rows.Where(r => r.Length == 5 && r[3] == "base64").GroupBy(r => r[0]))
        {
            var bytes = group.OrderBy(r => long.Parse(r[1])).SelectMany(r => Convert.FromBase64String(r[4])).ToArray();
            Assert.AreEqual(long.Parse(manifest[group.Key][3]), bytes.LongLength);
            Assert.AreEqual(manifest[group.Key][4], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            if (manifest[group.Key][1] == "synthetic/raw-response") Assert.AreEqual(text, Encoding.UTF8.GetString(bytes));
        }
    }
    [TestMethod] public void ProjectIsIdempotentDoubtsExcludedAndDocxPreservesLongPartBoundary()
    {
        using var session = ImportSession.Create(_root, Source(), default); session.State.SelectedProject = "Synthetic";
        var text = new string('Ж', 7600) + "😀"; var doubt = "Сомнительно";
        var units = new[] { Unit("a", text), Unit("b", doubt, text.Length) };
        var input = new ImportInput([new("c", "x", 2)], units.ToList(), [], []);
        var decisions = new[] { new ImportDecision("a", "MAIN", "Synthetic", "Глава", ""), new ImportDecision("b", "DOUBT", "Synthetic", "Глава", "uncertain") };
        var store = new LiteraryProjectStore(Path.Combine(_root, "projects.json"));
        var entry = ImportProjectBuilder.Build(session, input, decisions, store, _root, "Book", "test", "ru");
        Assert.AreEqual(entry.Id, ImportProjectBuilder.Build(session, input, decisions, store, _root, "Book", "test", "ru").Id);
        Assert.HasCount(1, store.Load().Projects); Assert.IsNull(store.Load().ActiveId);
        File.Delete(Path.Combine(_root, "projects.json")); // Simulate a crash between directory commit and index registration.
        session.State.ProjectPath = "";
        Assert.AreEqual(entry.Id, ImportProjectBuilder.Build(session, input, decisions, store, _root, "Book", "test", "ru").Id);
        Assert.HasCount(1, store.Load().Projects); Assert.IsNull(store.Load().ActiveId);
        var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
        Assert.AreEqual(text + doubt, string.Concat(chapters.Snapshot()[0].Parts));
        var last = chapters.Index.Parts.Where(p => p.Finished).Last();
        var lastText = File.ReadAllText(Path.Combine(entry.ProjectPath, "chapters", last.FileName));
        Assert.IsFalse(string.Concat(ImportEligibility.Allowed(entry.ProjectPath, last.Id, lastText)).Contains(doubt));
        Assert.HasCount(0, ImportEligibility.Allowed(entry.ProjectPath, last.Id, "edited"));
        var snapshot = LiteraryEditorSnapshot.Capture(entry.Id, entry.ProjectPath, chapters.Index, chapters.Load(), false);
        var memory = new LiteraryJellyStore(new LiteraryProjectLayout(entry.ProjectPath)); memory.Initialize();
        Assert.IsTrue(File.Exists(memory.FilePath));
        var invalid = new LiteraryJellyFact { Subject = "герой", Relation = "сказал", Evidence = "absent quote" };
        var batch = new LiteraryJellyBatch(Guid.NewGuid().ToString("N"), last.Id, "1", LiteraryWorkIndex.Revision(lastText), lastText, [invalid]);
        memory.Stage(batch);
        Assert.AreEqual("pending", memory.Find(last.Id, batch.Revision)!.Status);
        Assert.Throws<InvalidDataException>(() => memory.Confirm(batch, [invalid]));
        Assert.HasCount(0, memory.Read());
        var reader = new LiteraryProjectReader(snapshot);
        var number = snapshot.Sources.Single(p => p.Id == last.Id).Number;
        using (var blocked = JsonDocument.Parse(reader.Execute(new("read", number), default).Json))
            Assert.IsTrue(blocked.RootElement.TryGetProperty("error", out _));
        var docx = Path.Combine(_root, "book.docx"); ImportBookExporter.Export(entry.ProjectPath, docx, true, default);
        using (var doc = WordprocessingDocument.Open(docx, false))
        {
            Assert.HasCount(0, new OpenXmlValidator().Validate(doc).ToArray());
            Assert.IsTrue(doc.MainDocumentPart!.Document!.InnerText.Contains(text + doubt));
            Assert.IsTrue(doc.MainDocumentPart!.Document!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Highlight>().Any());
        }
        ImportEligibility.ConfirmPart(entry.ProjectPath, last.Id, lastText);
        Assert.AreEqual(lastText, string.Concat(ImportEligibility.Allowed(entry.ProjectPath, last.Id, lastText)));
        using (var allowed = JsonDocument.Parse(reader.Execute(new("read", number), default).Json))
            Assert.IsFalse(allowed.RootElement.TryGetProperty("error", out _));
        ImportCompletion.Export(session, entry, "ru", default);
        Assert.AreEqual("partial-result", session.State.Stage);
        Assert.IsTrue(session.State.Artifacts.Any(a => a.Step.StartsWith("human-review/")));
        ImportBlackBox.Verify(Path.Combine(entry.ProjectPath, "Exports", "Import", "import-history.xlsx"), session.State.Artifacts, default);
    }
    [TestMethod] public void CompactRangesMustCoverExactlyOnce()
    {
        var units = new[] { Unit("a", "начало"), Unit("b", "середина"), Unit("c", "конец") };
        var result = ImportPipeline.Parse("{\"units\":[[0,1,\"MAIN\",\"x\",\"1\",\"\"],[2,2,\"DOUBT\",\"x\",\"1\",\"uncertain\"]]}", units, true);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, result.Select(d => d.Id).ToArray());
        var single = ImportPipeline.Parse("{\"units\":[[0,1,\"MAIN\",\"x\",\"1\",\"\"],[2,\"DOUBT\",\"x\",\"1\",\"uncertain\"]]}", units, true);
        CollectionAssert.AreEqual(result, single);
        var optional = ImportPipeline.Parse("{\"units\":[[0,1,\"MAIN\",\"x\",\"1\"],[2,\"DOUBT\",\"x\",\"1\",\"uncertain\"]]}", units, true);
        CollectionAssert.AreEqual(result, optional);
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[[0,2,\"DROP\",\"x\",\"1\"]]}", units, true));
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[[0,3,\"MAIN\",\"x\",\"1\",\"\"]]}", units, true));
        Assert.Throws<InvalidDataException>(() => ImportPipeline.Parse("{\"units\":[[0,1,\"MAIN\",\"x\",\"1\",\"\"],[1,2,\"MAIN\",\"x\",\"1\",\"\"]]}", units, true));
    }
    [TestMethod] public void GroupingChangesOnlyProjectAndSurvivesResume()
    {
        using var session = ImportSession.Create(_root, Source(), default);
        var first = new[] { new ImportDecision("a", "KEEP", "old", "chapter", "") };
        session.AddJson("pass1/synthetic", first);
        var edited = new[] { first[0] with { Project = "new" } };
        ImportGrouping.Save(session, first, edited);
        CollectionAssert.AreEqual(edited, ImportGrouping.Read(session, first));
        Assert.Throws<InvalidDataException>(() => ImportGrouping.Save(session, first, [edited[0] with { Kind = "TRASH" }]));
        session.State.PlannedPath = Path.Combine(_root, "created");
        Assert.Throws<InvalidDataException>(() => ImportGrouping.Save(session, first, first));
    }
    [TestMethod] public void QdrantNativePathsKeepLocalAndUncStorageBeyondMaxPath()
    {
        var path = Path.Combine(_root, new string('a', 100), new string('b', 100), "storage");
        var native = QdrantRuntime.NativeStoragePath(path);
        Assert.AreEqual(@"\\?\" + Path.GetFullPath(path), native);
        Assert.AreEqual(native, QdrantRuntime.NativeStoragePath(native));
        Assert.AreEqual(@"\\?\UNC\server\share\storage", QdrantRuntime.NativeStoragePath(@"\\server\share\storage"));
    }
    [TestMethod] public void ConflictsAuthorRepliesAndChatFramingRemainVisibleDoubts()
    {
        var units = new[] { Unit("a", "Проза"), Unit("b", "Рассказ автора") with { Type = "REQUEST" },
            Unit("c", "Хорошо, мы не переписываем ту главу. Новая версия ниже."), Unit("d", "— Хорошо, — сказал герой.") };
        var first = units.Select(u => new ImportDecision(u.Id, u.Id == "a" ? "DECISION" : "KEEP", "work", "chapter", "instruction")).ToArray();
        var final = first.Select(d => d with { Kind = "MAIN", Reason = "" }).ToArray();
        var result = ImportReviewRules.Apply(new([], units.ToList(), [], []), first, final);
        CollectionAssert.AreEqual(new[] { "DOUBT", "DOUBT", "DOUBT", "MAIN" }, result.Select(d => d.Kind).ToArray());
        CollectionAssert.AreEqual(units.Select(u => u.Id).ToArray(), result.Select(d => d.Id).ToArray());
    }
    [TestMethod] public async Task InvalidChapterRepliesSplitWithoutLosingUnitsAndResumeFromCache()
    {
        using var session = ImportSession.Create(_root, Source(), default);
        var units = Enumerable.Range(0, 4).Select(i => Unit("repair" + i, "text-" + i)).ToArray();
        var input = new ImportInput([new("c", "synthetic", 4)], units.ToList(), [], []);
        var calls = 0;
        var pipeline = new ImportPipeline(session, (messages, _, _, _) =>
        {
            calls++;
            var payload = messages.Last().Content.Split("\nReturn valid complete JSON only.")[0];
            using var data = JsonDocument.Parse(payload);
            var count = data.RootElement.GetProperty("units").GetArrayLength();
            if (calls is 2 or 3)
                StringAssert.Contains(messages.Last().Content, "at most 150 characters");
            var chapter = count > 2 ? new string('x', 151) : "chapter";
            return Task.FromResult(JsonSerializer.Serialize(new { units = new object[][]
                { [0, count - 1, "KEEP", "work", chapter, ""] } }));
        });
        var result = await pipeline.AnalyzeAsync(input, ["c"], new Progress<ImportProgress>(), default);
        Assert.AreEqual(5, calls);
        CollectionAssert.AreEqual(units.Select(u => u.Id).ToArray(), result.Select(d => d.Id).ToArray());
        Assert.IsTrue(session.State.Artifacts.Any(a => a.Step.EndsWith("/validation-split")));
        var resumed = new ImportPipeline(session, (_, _, _, _) => throw new AssertFailedException("Cached import invoked a model"));
        CollectionAssert.AreEqual(result, await resumed.AnalyzeAsync(input, ["c"], new Progress<ImportProgress>(), default));
    }

    [TestMethod] public async Task ContextSplitKeepsFirstPassMappingAndResumeSkipsInference()
    {
        using var session = ImportSession.Create(_root, Source(), default);
        var units = Enumerable.Range(0, 4).Select(i => Unit("u" + i, "text-" + i)).ToArray();
        var first = units.Select(u => new ImportDecision(u.Id, "KEEP", "work", u.Text, "")).ToArray();
        session.AddJson("pass1/synthetic", first);
        var input = new ImportInput([new("c", "synthetic", 4)], units.ToList(), [], []);
        var calls = 0;
        var pipeline = new ImportPipeline(session, (messages, _, _, _) =>
        {
            calls++;
            using var data = JsonDocument.Parse(messages.Last().Content);
            var batch = data.RootElement.GetProperty("units").EnumerateArray().ToArray();
            if (batch.Length == 4) throw new ImageAnalysisContextExhaustedException("Synthetic small context");
            foreach (var group in data.RootElement.GetProperty("context").GetProperty("firstPass").EnumerateArray())
                foreach (var index in group.GetProperty("indices").EnumerateArray())
                    Assert.AreEqual(batch[index.GetInt32()].GetProperty("text").GetString(), group.GetProperty("chapter").GetString());
            return Task.FromResult("{\"units\":[[0," + (batch.Length - 1) + ",\"MAIN\",\"work\",\"chapter\",\"\"]]}");
        });
        var progress = new Progress<ImportProgress>();
        var result = await pipeline.AssembleAsync(input, first, "work", progress, default);
        Assert.AreEqual(3, calls); Assert.AreEqual(4, result.Length);
        var resumed = new ImportPipeline(session, (_, _, _, _) => throw new AssertFailedException("Cached import invoked a model"));
        var replay = await resumed.AssembleAsync(input, first, "work", progress, default);
        CollectionAssert.AreEqual(result, replay);
    }
}
