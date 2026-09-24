using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportWorkSelectionTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lopata-work-selection-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "lopata-work-selection-tests-");
        if (_root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ImportSession Session()
    {
        var source = Path.Combine(_root, "input.json"); File.WriteAllText(source, "[]");
        return ImportSession.Create(_root, source, default);
    }

    private static ImportUnit Unit(string id, string text) => new(id, "c", "m" + id, "", "RESPONSE", 0, 0, text, false);

    [TestMethod]
    public void GroupingKeepsEveryAnalyzedUnitAndOriginalTitleWithoutReclassifyingSource()
    {
        var input = new ImportInput([new("c", "Unrelated dialog", 6)],
            [Unit("1", new string('a', 800)), Unit("2", new string('b', 200)), Unit("3", new string('c', 1200)),
             Unit("4", new string('d', 9000)), Unit("5", "A short author note"), Unit("6", "Not analyzed")], [], []);
        ImportDecision[] first = [new("1", "KEEP", "Поиск понимания в собственных чувствах", "", ""),
            new("2", "DOUBT", "Поиск понимания в собственные чувствах", "", "typo"),
            new("3", "KEEP", "Тихая гавань", "", ""), new("4", "TRASH", "", "", "uncertain"),
            new("5", "DECISION", "Поиск понимания в собственных чувствах", "", "retain note")];
        var before = JsonSerializer.Serialize(first);

        var groups = ImportWorkSelection.Groups(input, first);

        Assert.AreEqual(3, groups.Count);
        Assert.AreEqual("Тихая гавань", groups[0].Name);
        Assert.AreEqual(2, groups[1].Variants.Count);
        CollectionAssert.AreEquivalent(new[] { first[0].Project, first[1].Project }, groups[1].Variants.Select(v => v.Name).ToArray());
        Assert.IsTrue(groups[^1].IsOther, "Other stays last even when larger than every named group.");
        var parts = groups.SelectMany(g => g.Variants).SelectMany(v => v.Parts).ToArray();
        CollectionAssert.AreEquivalent(first.Select(d => d.Id).ToArray(), parts.SelectMany(p => p.UnitIds).ToArray());
        Assert.AreEqual(input.Units.Take(5).Sum(u => (long)u.Text.Length), groups.Sum(g => g.Characters));
        Assert.AreEqual(before, JsonSerializer.Serialize(first));
        CollectionAssert.AreEqual(new[] { "1", "5" }, groups[1].Variants[0].Parts.SelectMany(p => p.UnitIds).ToArray());
    }

    [TestMethod]
    public async Task HybridSelectionHonorsExplicitExclusionsAndPreservesOriginalFirstPass()
    {
        using var session = Session();
        var input = new ImportInput([new("c", "Dialog", 5)],
            Enumerable.Range(1, 5).Select(i => Unit(i.ToString(), "Paragraph " + i)).ToList(), [], []);
        ImportDecision[] first = [new("1", "KEEP", "A", "", ""), new("2", "KEEP", "A", "", ""),
            new("3", "KEEP", "B", "", ""), new("4", "DECISION", "", "", "selected author instruction"),
            new("5", "DECISION", "", "", "excluded author instruction")];
        session.AddJson("pass1/test", first);
        var before = JsonSerializer.Serialize(first);
        var calls = 0;
        var pipeline = new ImportPipeline(session, (messages, _, _, _) =>
        {
            calls++;
            using var payload = JsonDocument.Parse(messages.Last().Content);
            var units = payload.RootElement.GetProperty("units");
            CollectionAssert.AreEqual(new[] { "Paragraph 1", "Paragraph 3", "Paragraph 4" },
                units.EnumerateArray().Select(u => u.GetProperty("text").GetString()).ToArray());
            var context = payload.RootElement.GetProperty("context");
            Assert.AreEqual("Combined book", context.GetProperty("project").GetString());
            StringAssert.Contains(context.GetProperty("authorDecisions").GetRawText(), "selected author instruction");
            Assert.IsFalse(context.GetProperty("authorDecisions").GetRawText().Contains("excluded author instruction"));
            return Task.FromResult("{\"units\":[[0,2,\"MAIN\",\"Combined book\",\"\",\"\"]]}");
        });

        var result = await pipeline.AssembleAsync(input, first, "Combined book", new Progress<ImportProgress>(), default,
            new HashSet<string>(["1", "3", "4"]));

        Assert.AreEqual(1, calls);
        Assert.IsTrue(result.Where(d => d.Id is "2" or "5").All(d => d.Kind == "DROP"));
        Assert.IsTrue(result.Where(d => d.Id is "1" or "3" or "4").All(d => d.Kind != "DROP"));
        Assert.AreEqual(before, JsonSerializer.Serialize(first));
        CollectionAssert.AreEqual(first, session.ReadLast<ImportDecision[]>("pass1/test"));
        CollectionAssert.AreEqual(new[] { "1", "3", "4" }, session.ReadLast<ImportAssemblySelection>("assembly-selection")!.UnitIds);
        session.State.PlannedPath = Path.Combine(_root, "built-book"); session.Save();
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.AssembleAsync(input, first, "Combined book",
            new Progress<ImportProgress>(), default, new HashSet<string>(["1", "3"])));
        Assert.AreEqual(1, calls, "Changing a built book's selection must be rejected before inference.");
    }

    [TestMethod]
    public async Task LegacySelectionStillIncludesUnassignedUnits()
    {
        using var session = Session();
        var input = new ImportInput([new("c", "Dialog", 3)], [Unit("1", "First"), Unit("2", "Unknown"), Unit("3", "Other")], [], []);
        ImportDecision[] first = [new("1", "KEEP", "A", "", ""), new("2", "KEEP", "", "", ""), new("3", "KEEP", "B", "", "")];
        session.AddJson("pass1/test", first);
        var pipeline = new ImportPipeline(session, (messages, _, _, _) =>
        {
            using var payload = JsonDocument.Parse(messages.Last().Content);
            Assert.AreEqual(2, payload.RootElement.GetProperty("units").GetArrayLength());
            return Task.FromResult("{\"units\":[[0,1,\"MAIN\",\"A\",\"\",\"\"]]}");
        });
        var result = await pipeline.AssembleAsync(input, first, "A", new Progress<ImportProgress>(), default);
        Assert.AreNotEqual("DROP", result.Single(d => d.Id == "2").Kind);
        Assert.AreEqual("DROP", result.Single(d => d.Id == "3").Kind);
    }

    [TestMethod]
    public void InvalidOrEmptySelectionCannotSilentlyAssembleSomethingElse()
    {
        ImportDecision[] first = [new("1", "KEEP", "Book", "", "")];
        Assert.Throws<InvalidDataException>(() => ImportWorkSelection.ForAssembly(first, new HashSet<string>(), "Book"));
        Assert.Throws<InvalidDataException>(() => ImportWorkSelection.ForAssembly(first, new HashSet<string>(["missing"]), "Book"));
        Assert.Throws<InvalidDataException>(() => ImportWorkSelection.ForAssembly(first, new HashSet<string>(["1"]), ""));
    }

    [TestMethod]
    public void DraftSelectionSurvivesRestartAndForgettingKeepsSourceAndSessionFiles()
    {
        var path = Path.Combine(_root, "drafts.json");
        var source = Path.Combine(_root, "source.json"); File.WriteAllText(source, "source sentinel");
        var session = Path.Combine(_root, "session"); Directory.CreateDirectory(session);
        var answers = Path.Combine(session, "preparation-answers.json"); File.WriteAllText(answers, "answers sentinel");
        var store = new LiteraryImportDraftStore(path);
        store.Remember(new LiteraryImportDraft("chosen", _root, source, "Project", "Book", session, "works", ["c"], DateTimeOffset.Now)
            { WorkSelectionKey = "key", SelectedWorkUnitIds = ["1", "3"] });
        store.Remember(new LiteraryImportDraft("other", _root, source, "Other", "Other", "", "source", [], DateTimeOffset.Now));
        var resumed = new LiteraryImportDraftStore(path).Load().Single(d => d.Id == "chosen");
        Assert.AreEqual("key", resumed.WorkSelectionKey);
        CollectionAssert.AreEqual(new[] { "1", "3" }, resumed.SelectedWorkUnitIds);
        store.Forget("chosen");
        CollectionAssert.AreEqual(new[] { "other" }, new LiteraryImportDraftStore(path).Load().Select(d => d.Id).ToArray());
        Assert.AreEqual("source sentinel", File.ReadAllText(source));
        Assert.AreEqual("answers sentinel", File.ReadAllText(answers));
    }

    [TestMethod]
    public void DraftsFromPreviousVersionLoadWithEmptyWorkSelection()
    {
        var path = Path.Combine(_root, "drafts.json");
        var old = JsonSerializer.SerializeToNode(new LiteraryImportDraft("old", _root, "source.json", "Project", "Book", "", "works", ["c"], DateTimeOffset.Now))!;
        old.AsObject().Remove("WorkSelectionKey"); old.AsObject().Remove("SelectedWorkUnitIds");
        File.WriteAllText(path, new JsonArray(old).ToJsonString());
        var restored = new LiteraryImportDraftStore(path).Load().Single();
        Assert.AreEqual("", restored.WorkSelectionKey);
        Assert.IsEmpty(restored.SelectedWorkUnitIds);
    }
}
