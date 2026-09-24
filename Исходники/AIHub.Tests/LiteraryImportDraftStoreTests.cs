using System.Text.Json;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryImportDraftStoreTests
{
    [TestMethod]
    public void KeepsThreeMostRecentlyAccessedDraftsWithoutDeletingSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LiteraryImportDraftStore(Path.Combine(root, "recent.json"));
            for (var i = 0; i < 4; i++)
            {
                var session = Path.Combine(root, "session" + i);
                Directory.CreateDirectory(session);
                store.Remember(new LiteraryImportDraft(i.ToString(), root, "source.json", "Project " + i,
                    "Book " + i, session, "dialogs", ["dialog"], DateTimeOffset.Now));
                Thread.Sleep(12);
            }
            var entries = store.Load();
            Assert.AreEqual(3, entries.Count);
            CollectionAssert.AreEqual(new[] { "3", "2", "1" }, entries.Select(e => e.Id).ToArray());
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "session0")));
            Assert.AreEqual("Book 3", entries[0].WorkTitle);
            Assert.IsTrue(entries[0].LastAccessed > entries[1].LastAccessed);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HidesFinishedSessionAndCanForgetDraft()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LiteraryImportDraftStore(Path.Combine(root, "recent.json"));
            var session = Path.Combine(root, "session"); Directory.CreateDirectory(session);
            store.Remember(new LiteraryImportDraft("id", root, "source.json", "Project", "Book",
                session, "works", [], DateTimeOffset.Now));
            Assert.AreEqual(1, store.Load().Count);
            File.WriteAllText(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new ImportSessionState { Stage = "complete" }));
            Assert.AreEqual(0, store.Load().Count);
            store.Forget("id");
            Assert.IsTrue(Directory.Exists(session));
        }
        finally { Directory.Delete(root, true); }
    }
}
