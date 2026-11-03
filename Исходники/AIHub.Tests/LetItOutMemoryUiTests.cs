using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task MemoryCloseWaitsForTheCompletedPairAndAReopenedTopicContinuesNumbering() => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store, memory: true);
            fixture.Input.Text = "First question"; fixture.Click("LetItOut.Send");
            Pump(() => fixture.Runtime.Requests.Count == 1); var id = fixture.Control.SessionId;
            fixture.Control.SetConversationTitle(id, "Manual title"); fixture.Window.Hide();
            Assert.AreEqual(0, fixture.Runtime.MemoryCloses.Count);
            fixture.Runtime.Complete(0, "First answer");
            Pump(() => !fixture.Control.IsWorking && fixture.Runtime.MemoryCloses.Count == 1);
            Assert.AreEqual(1, fixture.Runtime.PairSummaries.Count);
            Assert.AreEqual(1, store.MemorySnapshot(id)!.ThroughPair);
            Assert.AreEqual(2, store.Load(id)!.Messages.Count);
            fixture.Window.Show(); fixture.Click("LetItOut.New");
            Assert.IsTrue(fixture.Control.OpenConversation(id));
            fixture.Input.Text = "Second question"; fixture.Click("LetItOut.Send");
            Pump(() => fixture.Runtime.Requests.Count == 2);
            Assert.AreEqual(3, fixture.Runtime.Requests[1].Messages.Count);
            Assert.IsFalse(fixture.Runtime.Requests[1].Messages.Any(m => m.Content.Contains("Automatic topic heading")));
            fixture.Runtime.Complete(1, "Second answer"); Pump(() => !fixture.Control.IsWorking);
            Assert.AreEqual(0, fixture.Runtime.TitleRequests.Count);
            var close = fixture.Control.CloseMemoryForExitAsync(); Pump(() => close.IsCompleted); close.GetAwaiter().GetResult();
            Assert.AreEqual(2, store.MemorySnapshot(id)!.ThroughPair);
            Assert.AreEqual(2, store.MemoryPairs(id).Last().Number);
            Assert.AreEqual(4, store.Load(id)!.Messages.Count);
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    public Task StoppingSessionSummaryKeepsOriginalsAndCanBeRetriedOnReopening() => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store, memory: true);
            fixture.Input.Text = "Question"; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
            fixture.Runtime.Complete(0, "Completed answer"); Pump(() => !fixture.Control.IsWorking);
            var id = fixture.Control.SessionId; fixture.Input.Text = "Next draft";
            fixture.Runtime.MemoryCloseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var close = fixture.Control.CloseMemoryForExitAsync(); Pump(() => fixture.Runtime.MemoryCloses.Count == 1);
            fixture.Click("LetItOut.Stop"); Pump(() => close.IsCompleted); close.GetAwaiter().GetResult();
            Assert.AreEqual("Completed answer", store.MemoryPairs(id).Single().Answer);
            Assert.AreEqual("Next draft", store.Load(id)!.Draft); Assert.IsNull(store.MemorySnapshot(id));
            store.BeginMemoryRun(id); Assert.IsNotNull(store.PendingMemoryRun());
            fixture.Runtime.MemoryCloseGate = null;
        }
        finally { DeleteHistoryDirectory(directory); }
    });
}
