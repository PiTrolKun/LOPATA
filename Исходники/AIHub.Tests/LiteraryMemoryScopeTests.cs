using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemoryScopeTests
{
    private string _root = "";
    private LiteraryProject _project = null!;
    private LiteraryChapterStore _chapters = null!;
    private readonly List<LiteraryJellyFact> _facts = [];
    private LiteraryProjectLayout Layout => new(_root);
    private LiteraryEditorSnapshot Snapshot => LiteraryEditorSnapshot.Capture(_project.Id, _root, _chapters.Index, _chapters.Load(), false);

    [TestInitialize]
    public void Create()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", "memory-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root); _project = new();
        File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(_project));
        Layout.Initialize(); _chapters = new(_root); _chapters.Open(); _facts.Clear();
        for (var n = 0; n < 2; n++)
        {
            const string text = "Мирон получил ключ. Алиса не получила ключ. Мирон планирует вернуться.";
            _chapters.Save(text); var part = _chapters.Active; _chapters.Finish();
            var facts = Enumerable.Range(0, 30).Select(i => new LiteraryJellyFact
            {
                Subject = i < 20 ? "Мирон" : "Алиса", Relation = i < 15 ? "получил" : "не получил",
                Value = $"ключ {n}-{i}", Kind = i < 25 ? "event" : "intention", Evidence = text
            }).ToArray();
            var batch = new LiteraryJellyBatch(Guid.NewGuid().ToString("N"), part.Id, (n + 1).ToString("000"), LiteraryWorkIndex.Revision(text), text, facts);
            var store = new LiteraryJellyStore(Layout); store.Stage(batch); store.Confirm(batch, facts); _facts.AddRange(facts);
        }
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private static ParagraphSelection Selected() => new() { Selected = true };
    private static string[] FactIds(ParagraphEvidence evidence) => evidence.Materials
        .Where(m => m.Kind == "confirmed_project_memory").Select(m => JsonSerializer.SerializeToElement(m.Data).GetProperty("Id").GetString()!).ToArray();

    [TestMethod]
    public async Task WholeSelectedMemoryHasEveryFactNotOnlyTwelveRankedMatches()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        var evidence = await new LiteraryParagraphSources(_project, snapshot, catalog).ReadAsync("слово без совпадений",
            new Dictionary<string, ParagraphSelection> { ["jelly"] = Selected() }, _ => { }, default);
        Assert.IsTrue(evidence.Complete); Assert.AreEqual("found", evidence.Receipts.Single().Status);
        CollectionAssert.AreEquivalent(_facts.Select(f => f.Id).ToArray(), FactIds(evidence));
        Assert.AreEqual(60, evidence.Materials.Count);
        var data = ParagraphJson.Encode(evidence.Materials);
        StringAssert.Contains(data, "не получил"); StringAssert.Contains(data, "intention");
        Assert.IsFalse(data.Contains("\"limit\":12"));
    }

    [TestMethod]
    public async Task ExplicitTypeSubjectRelationAndPartScopesDoNotLeakOtherFactsOrTruncate()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        var nodes = catalog.Nodes.Values;
        var kind = nodes.Single(n => n.Kind == "jellyKind" && n.Key == "event");
        var subject = kind.Children.Single(n => n.Label == "Мирон");
        var relation = subject.Children.Single(n => n.Label == "получил");
        var part = relation.Children.First();
        foreach (var (node, expected) in new[]
        {
            (kind, _facts.Where(f => f.Kind == "event").ToArray()),
            (subject, _facts.Where(f => f.Kind == "event" && f.Subject == "Мирон").ToArray()),
            (relation, _facts.Where(f => f.Kind == "event" && f.Subject == "Мирон" && f.Relation == "получил").ToArray()),
            (part, _facts.Where(f => f.Kind == "event" && f.Subject == "Мирон" && f.Relation == "получил" && f.Value.StartsWith(part.Label == "001" ? "ключ 0-" : "ключ 1-")).ToArray())
        })
        {
            var evidence = await new LiteraryParagraphSources(_project, snapshot, catalog).ReadAsync("проверка",
                new Dictionary<string, ParagraphSelection> { [node.Id] = Selected() }, _ => { }, default);
            CollectionAssert.AreEquivalent(expected.Select(f => f.Id).ToArray(), FactIds(evidence), node.Kind);
            Assert.IsTrue(evidence.Complete); Assert.IsTrue(expected.Length > 12);
        }
    }

    [TestMethod]
    public async Task SelectedAncestorAndDescendantsRetainReceiptsWithoutDuplicateMaterials()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        var kind = catalog.Nodes.Values.Single(n => n.Kind == "jellyKind" && n.Key == "event");
        var subject = kind.Children.Single(n => n.Label == "Мирон");
        var selection = new Dictionary<string, ParagraphSelection>
            { ["jelly"] = Selected(), [kind.Id] = Selected(), [subject.Id] = Selected(), [subject.Children.First().Id] = Selected() };
        var evidence = await new LiteraryParagraphSources(_project, snapshot, catalog).ReadAsync("проверка", selection, _ => { }, default);
        Assert.AreEqual(60, evidence.Materials.Count); Assert.AreEqual(4, evidence.Receipts.Count);
        Assert.AreEqual(60, FactIds(evidence).Distinct().Count());
        foreach (var receipt in evidence.Receipts.Skip(1)) CollectionAssert.IsSubsetOf(receipt.Materials, evidence.Receipts[0].Materials);
        Assert.IsTrue(evidence.Receipts.All(r => r.Status == "found"));
    }

    [TestMethod]
    public async Task ReusingReaderForNextRequestSeesEditsAndRechecksSourceRevision()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        var reader = new LiteraryParagraphSources(_project, snapshot, catalog);
        var selection = new Dictionary<string, ParagraphSelection> { ["jelly"] = Selected() };
        Assert.AreEqual(60, (await reader.ReadAsync("проверка", selection, _ => { }, default)).Materials.Count);
        var store = new LiteraryJellyStore(Layout); var excluded = store.Read().First();
        store.Edit([excluded], [excluded.Fact with { Accepted = false }]);
        var next = await reader.ReadAsync("проверка", selection, _ => { }, default);
        Assert.AreEqual(59, next.Materials.Count); Assert.IsFalse(FactIds(next).Contains(excluded.Id));
        File.AppendAllText(Path.Combine(_root, "chapters", _chapters.Index.Parts[0].FileName), " Изменённый текст.");
        var stale = await reader.ReadAsync("проверка", selection, _ => { }, default);
        Assert.IsFalse(stale.Complete); Assert.AreEqual("error", stale.Receipts.Single().Status); Assert.AreEqual(0, stale.Materials.Count);
    }

    [TestMethod]
    public async Task CancellingAfterFirstScopeDoesNotClaimLaterScopeWasRead()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        using var cancellation = new CancellationTokenSource(); var progress = new List<ParagraphReceipt>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new LiteraryParagraphSources(_project, snapshot, catalog).ReadAsync("проверка",
            new Dictionary<string, ParagraphSelection> { ["jelly/event"] = Selected(), ["jelly/intention"] = Selected() }, r =>
            { progress.Add(r); if (r.Id == "jelly/event" && r.Status == "found") cancellation.Cancel(); }, cancellation.Token));
        Assert.IsTrue(progress.Any(r => r.Id == "jelly/event" && r.Status == "found"));
        Assert.IsTrue(progress.Where(r => r.Id == "jelly/intention").All(r => r.Status == "not_read"));
    }

    [TestMethod]
    public async Task EmptyRagSearchReportsLimitedCoverageRatherThanCompleteReading()
    {
        var snapshot = Snapshot; var catalog = new LiteraryParagraphCatalog(_project, snapshot, k => k);
        var evidence = await new LiteraryParagraphSources(_project, snapshot, catalog).ReadAsync("проверка",
            new Dictionary<string, ParagraphSelection> { ["rag/reference"] = Selected() }, _ => { }, default);
        Assert.IsTrue(evidence.Complete); Assert.AreEqual(0, evidence.Materials.Count);
        Assert.AreEqual("partial", evidence.Receipts.Single().Status);
        StringAssert.Contains(evidence.Receipts.Single().Detail, "not read in full");
    }
}
