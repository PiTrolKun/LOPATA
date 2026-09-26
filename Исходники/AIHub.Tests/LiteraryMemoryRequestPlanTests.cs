using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemoryRequestPlanTests
{
    [TestMethod]
    public void AuthorRulesRemainVerbatimEvenWhenResearchHasNoFindings()
    {
        var materials = new[]
        {
            new ParagraphMaterial("anchor@a", "author_anchor", "a", new { text = "Do not disclose the ending." }),
            new ParagraphMaterial("intent@b", "confirmed_creation_intent", "b", new { text = "Maintain uncertainty." }),
            new ParagraphMaterial("route@c", "future_route", "c", new { text = "Planned, not occurred." }),
            new ParagraphMaterial("chapter@d", "completed_project_part", "d", new { text = "Unrelated material." })
        };
        var receipts = new[] { new ParagraphReceipt("group", "Chosen", "found", "", materials.Select(m => m.Id).ToArray(), "") };
        var plan = new LiteraryMemoryRequestPlan(new(materials, receipts));
        CollectionAssert.AreEqual(new[] { "chapter@d" }, plan.SearchEvidence.Materials.Select(m => m.Id).ToArray());
        var baseline = plan.Combine(new([], []));
        Assert.HasCount(3, baseline.Materials);
        for (var index = 0; index < 3; index++) Assert.AreSame(materials[index], baseline.Materials[index]);
        CollectionAssert.AreEqual(new[] { "anchor@a", "intent@b", "route@c" }, baseline.Receipts.Single().Materials);
        var summary = new ParagraphMaterial("summary@e", "memory_search_findings", "e", new { findings = 0 });
        var final = plan.Combine(new([summary], [receipts[0] with { Materials = [summary.Id], Status = "partial", Detail = "Ranked only" }]));
        Assert.HasCount(4, final.Materials);
        Assert.AreEqual("partial", final.Receipts[0].Status);
        Assert.AreEqual("Ranked only", final.Receipts[0].Detail);
        Assert.IsTrue(final.Receipts[0].Materials.All(id => final.Materials.Any(m => m.Id == id)));
        Assert.IsFalse(ParagraphJson.Encode(final).Contains("Unrelated material."));
    }
}
