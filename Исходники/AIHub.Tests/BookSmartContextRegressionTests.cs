using System.IO;
using System.Text.Json;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass]
public sealed class BookSmartContextRegressionTests
{
    [TestMethod]
    public void CyrillicIsLiteralAndOnlyClassificationFieldsReachTheModel()
    {
        var text = string.Concat(Enumerable.Repeat("Автор: ёж, «кавычки», <текст>, \\ и \"цитата\".\n", 180));
        BookSmartBlock[] blocks = [new(12, text, Html: new string('x', 100000), Bounds: [0, 0, 1, 1])];
        var input = BookSmartStructureProtocol.Input(blocks);
        Assert.IsTrue(input.Length < text.Length * 2);
        StringAssert.Contains(input, "Автор"); Assert.IsFalse(input.Contains("\\u0410", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(input);
        Assert.AreEqual(text, parsed.RootElement[0].GetProperty("text").GetString());
        CollectionAssert.AreEquivalent(new[] { "id", "text", "kind" }, parsed.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray());
        using var payload = JsonDocument.Parse(BookSmartStructureProtocol.Payload(blocks));
        Assert.AreEqual(input, payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.IsFalse(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [TestMethod]
    public async Task RejectedBatchesAndLongSingleBlocksAreDividedWithoutDroppingWordsOrIds()
    {
        BookSmartBlock[] blocks = [new(7, "А😀БВ😀ГДЕЖЗ"), new(19, "Другой авторский абзац"), new(21, "Конец")];
        var accepted = new List<BookSmartBlock>(); var attempts = 0;
        var result = await BookSmartStructureProtocol.ClassifyAsync(blocks, (batch, token) =>
        {
            attempts++;
            if (batch.Length > 1 || batch[0].Text.Length > 5) return Task.FromResult<BookSmartStructure[]?>(null);
            accepted.AddRange(batch);
            Assert.IsFalse(char.IsLowSurrogate(batch[0].Text[0])); Assert.IsFalse(char.IsHighSurrogate(batch[0].Text[^1]));
            return Task.FromResult<BookSmartStructure[]?>([new(batch[0].Id, "keep")]);
        }, CancellationToken.None);
        Assert.IsTrue(attempts > blocks.Length);
        CollectionAssert.AreEqual(blocks.Select(b => b.Id).ToArray(), result.Select(b => b.Id).ToArray());
        foreach (var block in blocks)
            Assert.AreEqual(block.Text, string.Concat(accepted.Where(b => b.Id == block.Id).Select(b => b.Text)));
    }

    [TestMethod]
    public void OutputCutOffByTokenLimitIsNeverAcceptedAsACompleteClassification()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BookSmartStructureProtocol.Parse(
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}"));
    }

    [TestMethod]
    public async Task DividingHonorsCancellation()
    {
        using var cancel = new CancellationTokenSource(); var calls = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => BookSmartStructureProtocol.ClassifyAsync(
            [new(1, "some text"), new(2, "other text")], (_, _) =>
            { calls++; cancel.Cancel(); return Task.FromResult<BookSmartStructure[]?>(null); }, cancel.Token));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void PreparedSessionReusesUntouchedFilesAndInvalidatesChangedDependencies()
    {
        var root = Path.Combine(Path.GetTempPath(), "book-ready-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = Path.Combine(root, "model.bin"); File.WriteAllText(model, "verified");
        var selection = new BookSmartSelection(); var session = new BookSmartPreparedSession(root, selection, true, "002", [model]);
        try
        {
            // Even a locked payload can be reused: metadata checks do not re-read model bytes.
            using (var locked = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.None))
            { for (var i = 0; i < 3; i++) Assert.IsTrue(session.Matches(root, selection, true, "002", [model])); }
            Assert.IsTrue(session.Matches(root, selection, false, "002", [model]));
            Assert.IsFalse(session.Matches(root, new("ovis", "alpha"), true, "002", [model]));
            Assert.IsFalse(session.Matches(root, selection, true, "001", [model]));
            Assert.IsFalse(session.Matches(root + "other", selection, true, "002", [model]));
            Assert.IsFalse(session.Matches(root, selection, true, "002", null));
            File.SetLastWriteTimeUtc(model, File.GetLastWriteTimeUtc(model).AddSeconds(10));
            Assert.IsFalse(session.Matches(root, selection, true, "002", [model]));
            File.Delete(model); Assert.IsFalse(session.Matches(root, selection, true, "002", [model]));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ImagesAddedAfterTextOnlyPreparationRequireTheVisualDependency()
    {
        var file = Path.GetTempFileName();
        try
        {
            var session = new BookSmartPreparedSession(Path.GetDirectoryName(file)!, new(), false, "002", [file]);
            Assert.IsFalse(session.Matches(Path.GetDirectoryName(file)!, new(), true, "002", [file]));
        }
        finally { File.Delete(file); }
    }
}
