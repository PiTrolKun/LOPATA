using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryTipDeckTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-tip-deck-" + Guid.NewGuid().ToString("N"));
    private static (string Id, string Category)[] Catalog => Enumerable.Range(0, 12)
        .Select(i => ("tip-" + i, "category-" + i % 3)).ToArray();

    [TestInitialize]
    public void CreateDirectory() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_root, true);

    [TestMethod]
    public void EveryCycleContainsEachTipExactlyOnce()
    {
        var catalog = Catalog;
        var deck = new LiteraryTipDeck(catalog);
        string? last = null;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var shown = new List<string>();
            for (var i = 0; i < catalog.Length; i++)
            {
                var next = deck.Next([]);
                Assert.IsNotNull(next);
                Assert.AreNotEqual(last, next, "The cycle boundary must not immediately repeat its last tip.");
                shown.Add(next);
                last = next;
            }
            CollectionAssert.AreEquivalent(catalog.Select(t => t.Id).ToArray(), shown);
        }
    }

    [TestMethod]
    public void BalancedCategoriesAlternateThroughoutTheCycle()
    {
        var catalog = Catalog;
        var categories = catalog.ToDictionary(t => t.Id, t => t.Category);
        var deck = new LiteraryTipDeck(catalog);
        string? previous = null;
        for (var i = 0; i < catalog.Length * 2; i++)
        {
            var next = deck.Next([]);
            Assert.IsNotNull(next);
            var category = categories[next];
            Assert.AreNotEqual(previous, category);
            previous = category;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnequalCategoriesAllAppearInTheFirstSixChoicesAndEachFollowingRound(bool reopen)
    {
        var sizes = new[] { 40, 30, 25, 20, 15, 20 };
        var catalog = sizes.SelectMany((size, category) => Enumerable.Range(0, size)
            .Select(i => (Id: $"tip-{category}-{i}", Category: "category-" + category))).ToArray();
        var categories = catalog.ToDictionary(t => t.Id, t => t.Category);
        var expected = catalog.Select(t => t.Category).Distinct(StringComparer.Ordinal).ToArray();
        var path = reopen ? Path.Combine(_root, "history.json") : null;
        var deck = new LiteraryTipDeck(catalog, path);
        string? previous = null;
        for (var round = 0; round < 3; round++)
        {
            var shownCategories = new List<string>();
            for (var i = 0; i < expected.Length; i++)
            {
                var next = deck.Next([]);
                Assert.IsNotNull(next);
                var category = categories[next];
                Assert.AreNotEqual(previous, category, "Thematic round boundaries must avoid repeating a category.");
                shownCategories.Add(category);
                previous = category;
                if (reopen) deck = new LiteraryTipDeck(catalog, path);
            }
            CollectionAssert.AreEquivalent(expected, shownCategories);
        }
    }

    [TestMethod]
    public void ReopeningAfterEverySelectionPreservesTheWholeCycle()
    {
        var catalog = Catalog;
        var path = Path.Combine(_root, "history.json");
        var shown = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        for (var i = 0; i < catalog.Length; i++)
        {
            var next = new LiteraryTipDeck(catalog, path).Next([]);
            Assert.IsNotNull(next);
            Assert.IsTrue(shown.Add(next), "A previously shown tip was repeated after reopening.");
            last = next;
        }
        CollectionAssert.AreEquivalent(catalog.Select(t => t.Id).ToArray(), shown.ToArray());
        Assert.AreNotEqual(last, new LiteraryTipDeck(catalog, path).Next([]));
    }

    [TestMethod]
    public void VisibleTipsAreExcludedAcrossMultipleCycleBoundaries()
    {
        var deck = new LiteraryTipDeck(Catalog);
        var visible = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            var next = deck.Next(visible);
            Assert.IsNotNull(next);
            Assert.IsFalse(visible.Contains(next), "A fading or held tip is still visible.");
            visible.Add(next);
            if (visible.Count > 3) visible.RemoveAt(0);
            Assert.AreEqual(visible.Count, visible.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [TestMethod]
    public void ExcludedUnseenTipIsDeferredWithoutStartingAnEarlyCycle()
    {
        var catalog = Catalog;
        var deck = new LiteraryTipDeck(catalog);
        var held = catalog[0].Id;
        var shown = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < catalog.Length - 1; i++)
        {
            var next = deck.Next([held]);
            Assert.IsNotNull(next);
            Assert.AreNotEqual(held, next);
            Assert.IsTrue(shown.Add(next));
        }
        Assert.IsNull(deck.Next([held]));
        Assert.AreEqual(held, deck.Next([]));
    }

    [TestMethod]
    public void CatalogChangesKeepSeenTipsAndIntroduceNewIdsWithoutRevivingRemovedOnes()
    {
        var catalog = Catalog;
        var path = Path.Combine(_root, "history.json");
        var original = new LiteraryTipDeck(catalog, path);
        var first = original.Next([])!;
        var second = original.Next([])!;
        var removedUnseen = catalog.First(t => t.Id != first && t.Id != second).Id;
        var changed = catalog.Where(t => t.Id != first && t.Id != removedUnseen)
            .Append((Id: "new-tip", Category: "new-category")).ToArray();
        var deck = new LiteraryTipDeck(changed, path);
        var remainder = Enumerable.Range(0, changed.Length - 1).Select(_ => deck.Next([])).ToArray();
        CollectionAssert.AreEquivalent(changed.Where(t => t.Id != second).Select(t => t.Id).ToArray(), remainder);
    }

    [TestMethod]
    [DataRow("{broken")]
    [DataRow("null")]
    [DataRow("{\"Version\":1,\"SeenIds\":null}")]
    [DataRow("{\"Version\":2,\"SeenIds\":[\"tip-0\"]}")]
    [DataRow("{\"Version\":1,\"SeenIds\":42}")]
    public void CorruptOrUnsupportedHistoryStartsAUsableFreshCycle(string contents)
    {
        var path = Path.Combine(_root, "history.json");
        File.WriteAllText(path, contents);
        var deck = new LiteraryTipDeck(Catalog, path);
        var shown = Enumerable.Range(0, Catalog.Length).Select(_ => deck.Next([])).ToArray();
        CollectionAssert.AreEquivalent(Catalog.Select(t => t.Id).ToArray(), shown);
    }

    [TestMethod]
    public void UnwritableHistoryKeepsInMemoryUniqueness()
    {
        var blockedDirectory = Path.Combine(_root, "existing-file");
        File.WriteAllText(blockedDirectory, "keep");
        var deck = new LiteraryTipDeck(Catalog, Path.Combine(blockedDirectory, "history.json"));
        var shown = Enumerable.Range(0, Catalog.Length).Select(_ => deck.Next([])).ToArray();
        CollectionAssert.AreEquivalent(Catalog.Select(t => t.Id).ToArray(), shown);
        Assert.AreEqual("keep", File.ReadAllText(blockedDirectory));
    }

    [TestMethod]
    public void LockedHistoryDoesNotInterruptTipSelection()
    {
        var path = Path.Combine(_root, "history.json");
        File.WriteAllText(path, "{\"Version\":1,\"SeenIds\":[]}");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var deck = new LiteraryTipDeck(Catalog, path);
        var shown = Enumerable.Range(0, Catalog.Length).Select(_ => deck.Next([])).ToArray();
        CollectionAssert.AreEquivalent(Catalog.Select(t => t.Id).ToArray(), shown);
    }

    [TestMethod]
    public void HistoryIsSavedOnlyWhenATipIsSelected()
    {
        var path = Path.Combine(_root, "nested", "history.json");
        var deck = new LiteraryTipDeck(Catalog, path);
        var allIds = Catalog.Select(t => t.Id).ToArray();
        Assert.IsFalse(File.Exists(path));
        Assert.IsNull(deck.Next(allIds));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.IsNotNull(deck.Next([]));
        var saved = File.ReadAllBytes(path);
        Assert.IsNull(deck.Next(allIds));
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void EmptyAndSingleTipCatalogsRespectExclusions()
    {
        Assert.IsNull(new LiteraryTipDeck([]).Next([]));
        var single = new LiteraryTipDeck([("only", "category")]);
        Assert.AreEqual("only", single.Next([]));
        Assert.IsNull(single.Next(["only"]));
        Assert.AreEqual("only", single.Next([]));
    }
}
