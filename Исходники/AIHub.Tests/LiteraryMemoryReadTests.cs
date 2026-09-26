using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemoryReadTests
{
    [TestMethod]
    public void RepeatedBatchFactsKeepTheirSourceAndFreshEditsOnEachRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIHubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(new LiteraryProject()));
            var layout = new LiteraryProjectLayout(root); layout.Initialize();
            var chapters = new LiteraryChapterStore(root); chapters.Open();
            var memory = new LiteraryJellyStore(layout);
            var expected = new Dictionary<string, (string Part, string Number)>();
            for (var source = 0; source < 2; source++)
            {
                const string text = "The library opened.";
                chapters.Save(text); var part = chapters.Active;
                var number = "source-" + source;
                var facts = Enumerable.Range(0, 12).Select(i => new LiteraryJellyFact
                { Subject = "Library", Relation = "opened", Value = i.ToString(), Evidence = text }).ToArray();
                var batch = new LiteraryJellyBatch(Guid.NewGuid().ToString("N"), part.Id, number,
                    LiteraryWorkIndex.Revision(text), text, facts);
                chapters.Finish(); memory.Stage(batch); memory.Confirm(batch, facts);
                foreach (var fact in facts) expected.Add(fact.Id, (part.Id, number));
            }
            var before = memory.Read(); Assert.HasCount(24, before);
            foreach (var row in before)
            { Assert.AreEqual(expected[row.Id].Part, row.PartId); Assert.AreEqual(expected[row.Id].Number, row.Number); }
            var target = before[0];
            memory.Edit([target], [target.Fact with { Value = "Edited value" }]);
            var after = memory.Read(); var edited = after.Single(e => e.Id == target.Id);
            Assert.AreEqual("Edited value", edited.Fact.Value); Assert.AreEqual(target.Version + 1, edited.Version);
            Assert.HasCount(24, after); Assert.AreEqual(target.Number, edited.Number);
            memory.Edit([edited], [edited.Fact with { Accepted = false }]);
            Assert.HasCount(23, memory.Read());
        }
        finally { Directory.Delete(root, true); }
    }
}
