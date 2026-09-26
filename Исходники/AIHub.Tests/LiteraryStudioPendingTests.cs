using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryStudioPendingTests
{
    private string _root = "";
    private LiteraryProjectLayout Layout => new(_root);
    [TestInitialize]
    public void Create()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", "studio-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(new LiteraryProject()));
        Layout.Initialize();
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void InterruptedSubmissionRestoresComposerFromDiskWithoutDuplicatingHistoryOrContext()
    {
        var store = new LiteraryStudioStore(Layout); var state = store.Load();
        state.Add("Advisor", "Earlier discussion");
        var quote = new StudioQuote("Chapter 3", "Exact evidence");
        var message = state.Add("User", "Pending request", quotes: [quote]);
        state.Pending = new(message.Id, message.Text, [quote], true); state.Interrupted = true;
        state.Selection["jelly/event"] = new() { Selected = true, Comment = "Scope comment" };
        store.Save(state);
        var reopened = new LiteraryStudioStore(Layout); var restored = reopened.Load();
        LiteraryStudioPending.Restore(restored);
        Assert.AreEqual("Pending request", restored.Input); CollectionAssert.AreEqual(new[] { quote }, restored.Quotes);
        Assert.IsTrue(restored.WriterComment); Assert.IsNull(restored.Pending); Assert.AreEqual(2, restored.Messages.Count);
        Assert.IsFalse(restored.Messages.Single(m => m.Id == message.Id).InContext);
        Assert.IsTrue(restored.Messages.Single(m => m.Role == "Advisor").InContext);
        Assert.IsTrue(restored.Selection["jelly/event"].Selected); Assert.AreEqual("Scope comment", restored.Selection["jelly/event"].Comment);
        reopened.Save(restored); var again = new LiteraryStudioStore(Layout).Load(); LiteraryStudioPending.Restore(again);
        Assert.AreEqual("Pending request", again.Input); Assert.AreEqual(2, again.Messages.Count); Assert.AreEqual(1, again.Quotes.Count);
    }

    [TestMethod]
    public void ExistingComposerTextAndQuotesSurvivePendingRestore()
    {
        var store = new LiteraryStudioStore(Layout); var state = store.Load();
        var quote = new StudioQuote("Book", "Already attached");
        var pending = state.Add("User", "Older pending request");
        state.Input = "Newer unsent edit"; state.Quotes.Add(quote);
        state.Pending = new(pending.Id, pending.Text, [quote, new("Other", "Second quote")], false);
        store.Save(state);
        var loaded = new LiteraryStudioStore(Layout).Load(); LiteraryStudioPending.Restore(loaded); LiteraryStudioPending.Restore(loaded);
        Assert.AreEqual("Newer unsent edit", loaded.Input); Assert.AreEqual(2, loaded.Quotes.Count);
        Assert.AreEqual(1, loaded.Messages.Count); Assert.IsFalse(loaded.Messages[0].InContext); Assert.IsNull(loaded.Pending);
    }

    [TestMethod]
    public void QuoteOnlyPendingRestoresWithoutInventingMessagesAndClearDiscardsIt()
    {
        var store = new LiteraryStudioStore(Layout); var state = store.Load();
        state.Pending = new("", "", [new("Source", "Quote only")], false); store.Save(state);
        var loaded = new LiteraryStudioStore(Layout).Load(); LiteraryStudioPending.Restore(loaded);
        Assert.AreEqual("", loaded.Input); Assert.AreEqual(0, loaded.Messages.Count); Assert.AreEqual(1, loaded.Quotes.Count);
        loaded.Pending = new("", "Do not restore after clear", [new("Source", "Discarded")], true);
        loaded.Clear(); LiteraryStudioPending.Restore(loaded);
        Assert.IsNull(loaded.Pending); Assert.AreEqual("", loaded.Input); Assert.AreEqual(0, loaded.Quotes.Count);
    }

    [TestMethod]
    public void InvalidPendingRecordIsRejectedWithoutOverwritingSession()
    {
        var store = new LiteraryStudioStore(Layout); var state = store.Load();
        state.Pending = new("", "Preserve malformed file", [], false); store.Save(state);
        var text = File.ReadAllText(store.FilePath);
        using var json = JsonDocument.Parse(text);
        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        node["Pending"]!["Quotes"] = null;
        var invalid = node.ToJsonString(); File.WriteAllText(store.FilePath, invalid);
        Assert.Throws<InvalidDataException>(() => new LiteraryStudioStore(Layout).Load());
        Assert.AreEqual(invalid, File.ReadAllText(store.FilePath));
    }
}
