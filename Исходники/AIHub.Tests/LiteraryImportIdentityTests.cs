using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryImportIdentityTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lopata-import-identity-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "lopata-import-identity-tests-");
        if (_root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private ImportSession Session()
    {
        var source = Path.Combine(_root, "input.json");
        File.WriteAllText(source, "[]");
        return ImportSession.Create(_root, source, default);
    }

    private static ImportUnit Unit(int i) => new("unit-" + i, "c", "message-" + i, "", "RESPONSE", 0, 0,
        "Manuscript paragraph " + i, false);

    [TestMethod]
    public async Task FirstPassDoesNotFeedArchiveTitlesOrEarlierGuessesIntoLaterBatches()
    {
        using var session = Session();
        const string archiveTitle = "Unrelated archive-title sentinel";
        const string earlierGuess = "Unverified earlier-guess sentinel";
        var units = Enumerable.Range(0, 81).Select(Unit).ToArray();
        var input = new ImportInput([new("c", archiveTitle, units.Length)], units.ToList(), [], []);
        var calls = 0;
        var pipeline = new ImportPipeline(session, (messages, _, _, _) =>
        {
            calls++;
            using var payload = JsonDocument.Parse(messages.Last().Content);
            var context = payload.RootElement.GetProperty("context");
            Assert.IsFalse(context.TryGetProperty("knownProjects", out _));
            Assert.IsFalse(context.GetRawText().Contains(archiveTitle, StringComparison.Ordinal));
            Assert.IsFalse(context.GetRawText().Contains(earlierGuess, StringComparison.Ordinal));
            var count = payload.RootElement.GetProperty("units").GetArrayLength();
            var rows = calls == 1
                ? new object[][] { [0, 0, "KEEP", earlierGuess, "", ""], [1, count - 1, "KEEP", "", "", ""] }
                : new object[][] { [0, count - 1, "KEEP", "A source-supported work", "", ""] };
            return Task.FromResult(JsonSerializer.Serialize(new { units = rows }));
        });

        var result = await pipeline.AnalyzeAsync(input, ["c"], new Progress<ImportProgress>(), default);

        Assert.AreEqual(2, calls);
        CollectionAssert.AreEqual(units.Select(u => u.Id).ToArray(), result.Select(d => d.Id).ToArray());
        Assert.AreEqual(79, result.Count(d => d.Project.Length == 0 && d.Kind == "KEEP"));
        Assert.AreEqual("A source-supported work", result[^1].Project);
    }

    [TestMethod]
    public async Task ObsoleteIdentityPolicyCacheIsIgnoredButCurrentResultResumesWithoutInference()
    {
        using var session = Session();
        var units = new[] { Unit(0), Unit(1) };
        var input = new ImportInput([new("c", "Unrelated archive", units.Length)], units.ToList(), [], []);
        var sourceUnits = ImportSession.Hash(string.Join("|", units.Select(u => u.Id)));
        var obsoleteKey = "pass1/" + ImportSession.Hash("deepseek-book-v4-ranges" + LiteraryModelLocation.Sha256 + "c" + sourceUnits);
        var obsolete = units.Select(u => new ImportDecision(u.Id, "KEEP", "Unrelated archive", "", "")).ToArray();
        session.AddJson(obsoleteKey, obsolete);
        var calls = 0;
        var pipeline = new ImportPipeline(session, (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult("{\"units\":[[0,1,\"KEEP\",\"\",\"\",\"\"]]}");
        });

        var result = await pipeline.AnalyzeAsync(input, ["c"], new Progress<ImportProgress>(), default);

        Assert.AreEqual(1, calls, "The previous identity policy's completed artifact must not suppress inference.");
        CollectionAssert.AreEqual(units.Select(u => u.Id).ToArray(), result.Select(d => d.Id).ToArray());
        Assert.IsTrue(result.All(d => d.Project.Length == 0 && d.Kind == "KEEP"));
        Assert.AreEqual(2, session.State.Artifacts.Count(a => a.Step.StartsWith("pass1/", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(obsolete, session.ReadLast<ImportDecision[]>(obsoleteKey));

        var resumed = new ImportPipeline(session, (_, _, _, _) => throw new AssertFailedException("Current result should resume from cache."));
        CollectionAssert.AreEqual(result, await resumed.AnalyzeAsync(input, ["c"], new Progress<ImportProgress>(), default));
    }
}
