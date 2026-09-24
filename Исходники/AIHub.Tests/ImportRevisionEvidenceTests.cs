using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportRevisionEvidenceTests
{
    private static ImportUnit Unit(string id, string conversation, string message, string parent,
        string type, string text, int offset = 0, bool technical = false) =>
        new(id, conversation, message, parent, type, 0, offset, text, technical);

    [TestMethod]
    public void SplitSceneGetsLaterOriginalCorrectionWithoutCrossConversationMessageCollision()
    {
        var sceneStart = Unit("start", "a", "10", "9", "RESPONSE", "Scene beginning.");
        var sceneTail = Unit("tail", "a", "10", "9", "RESPONSE", "Scene ending.", 16);
        var rows = new[]
        {
            sceneStart, sceneTail,
            Unit("foreign-request", "b", "11", "10", "REQUEST", "Foreign instruction."),
            Unit("foreign-response", "b", "12", "11", "RESPONSE", "Foreign reply."),
            Unit("instruction-a", "a", "11", "10", "REQUEST", "Change the ending:\n", 0),
            Unit("instruction-b", "a", "11", "10", "REQUEST", "keep the opening.", 19),
            Unit("thinking", "a", "12", "11", "THINK", "Internal technical text.", technical: true),
            Unit("reply-a", "a", "12", "11", "RESPONSE", "I will revise it.\n\n"),
            Unit("reply-b", "a", "12", "11", "RESPONSE", "Revised ending:\n\n", 19),
            Unit("reply-c", "a", "12", "11", "RESPONSE", "Remaining chapter.", 37)
        };
        var input = new ImportInput([], rows.ToList(), [], []);

        var fromStart = ImportRevisionEvidence.ForBatch(input, [sceneStart]);
        var fromTail = ImportRevisionEvidence.ForBatch(input, [sceneTail]);

        Assert.AreEqual(1, fromStart.Items.Length);
        Assert.AreEqual(1, fromTail.Items.Length);
        var evidence = fromStart.Items[0];
        Assert.AreEqual("a", evidence.Conversation);
        Assert.AreEqual("10", evidence.SourceMessage);
        Assert.AreEqual("11", evidence.RequestMessage);
        Assert.AreEqual(4, evidence.RequestSourceIndex);
        CollectionAssert.AreEqual(new[] { "instruction-a", "instruction-b" }, evidence.AuthorRequest.Select(p => p.UnitId).ToArray());
        Assert.AreEqual("Change the ending:\nkeep the opening.", string.Concat(evidence.AuthorRequest.Select(p => p.Text)));
        CollectionAssert.AreEqual(new[] { "reply-a", "reply-b" }, evidence.ReplyOpening.Select(p => p.UnitId).ToArray());
        CollectionAssert.AreEqual(evidence.AuthorRequest, fromTail.Items[0].AuthorRequest);
        Assert.IsFalse(fromStart.Truncated);
    }

    [TestMethod]
    public void OnlyFollowingNontechnicalDirectRepliesAreEvidence()
    {
        var old = Unit("old", "c", "old", "", "RESPONSE", "Original prose.");
        var rows = new[]
        {
            Unit("too-early", "c", "request-early", "old", "REQUEST", "Earlier branch."), old,
            Unit("technical", "c", "technical-request", "old", "REQUEST", "Technical artifact.", technical: true),
            Unit("unrelated", "c", "another-request", "another-message", "REQUEST", "Unrelated."),
            Unit("real", "c", "request", "old", "REQUEST", "An original instruction."),
            Unit("opening", "c", "response", "request", "RESPONSE", "Original reply."),
            Unit("other-response", "other", "response", "request", "RESPONSE", "Wrong conversation.")
        };
        var result = ImportRevisionEvidence.ForBatch(new([], rows.ToList(), [], []), [old]);
        Assert.AreEqual(1, result.Items.Length);
        Assert.AreEqual("real", result.Items[0].AuthorRequest.Single().UnitId);
        Assert.AreEqual("opening", result.Items[0].ReplyOpening.Single().UnitId);
    }

    [TestMethod]
    public void EvidenceBudgetsAreBoundedAndEveryOmissionIsReported()
    {
        var source = Unit("source", "c", "source", "", "RESPONSE", "Original.");
        var rows = new List<ImportUnit> { source };
        for (var i = 0; i < 10; i++)
        {
            rows.Add(Unit("request-" + i, "c", "r" + i, "source", "REQUEST", new string('a', 2500)));
            rows.Add(Unit("answer-" + i, "c", "a" + i, "r" + i, "RESPONSE", new string('b', 2500)));
        }
        var result = ImportRevisionEvidence.ForBatch(new([], rows, [], []), [source]);
        Assert.AreEqual(8000, result.TextCharacters);
        Assert.AreEqual(10, result.CandidateCount);
        Assert.AreEqual(8, result.OmittedItems);
        Assert.AreEqual(2, result.Items.Length);
        Assert.IsTrue(result.Truncated);
        foreach (var item in result.Items)
        {
            Assert.AreEqual(1000, item.OmittedCharacters);
            Assert.IsTrue(item.Truncated);
            Assert.AreEqual(2000, item.AuthorRequest.Single().Text.Length);
            Assert.AreEqual(500, item.AuthorRequest.Single().OmittedCharacters);
            Assert.AreEqual(2000, item.ReplyOpening.Single().Text.Length);
        }
        var small = rows.Select(u => u with { Text = "short" }).ToList();
        var boundedCount = ImportRevisionEvidence.ForBatch(new([], small, [], []), [small[0]]);
        Assert.AreEqual(8, boundedCount.Items.Length);
        Assert.AreEqual(2, boundedCount.OmittedItems);
    }
}
