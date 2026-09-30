using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class BackgroundMemoryDraftTests
{
    [TestMethod]
    public void UnconfirmedReviewEditsSurviveReloadWithoutEnteringCanonicalMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-background-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string text = "Герой открыл дверь.";
            var project = new LiteraryProject { Genres = ["comedy"] };
            File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(project));
            var layout = new LiteraryProjectLayout(root); layout.Initialize();
            var chapters = new LiteraryChapterStore(root); chapters.Open(); chapters.Save(text);
            var part = chapters.Active; chapters.Finish();
            var fact = new LiteraryJellyFact { Subject = "Герой", Relation = "открыл", Value = "дверь", Evidence = text };
            var batch = new LiteraryJellyBatch(Guid.NewGuid().ToString("N"), part.Id, "1", LiteraryWorkIndex.Revision(text), text, [fact]);
            var memory = new LiteraryJellyStore(layout); memory.Stage(batch);
            var draft = new LiteraryJellyReviewDraft(layout, batch);
            draft.Save([fact with { Accepted = false, Subject = "Ручная правка" }]);
            var restored = new LiteraryJellyReviewDraft(layout, memory.Find(part.Id, batch.Revision)!).Read();
            Assert.AreEqual("Ручная правка", restored.Single().Subject); Assert.IsFalse(restored.Single().Accepted);
            Assert.HasCount(0, memory.Read()); Assert.AreEqual("pending", memory.Find(part.Id, batch.Revision)!.Status);
            Assert.ThrowsExactly<InvalidDataException>(() => draft.Save([fact with { Id = Guid.NewGuid().ToString("N") }]));
            Assert.AreEqual("Ручная правка", draft.Read().Single().Subject);
            chapters.EditCompleted(part.Id, text, "Другой источник.");
            Assert.ThrowsExactly<IOException>(() => draft.Read());
        }
        finally
        {
            var path = Path.GetFullPath(root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("lopata-background-memory-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
}
