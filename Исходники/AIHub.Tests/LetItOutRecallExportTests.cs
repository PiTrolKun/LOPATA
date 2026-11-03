using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Data.Sqlite;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutRecallExportTests
{
    private static string Root() { var root = Path.Combine(Path.GetTempPath(), "lopata-recall-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static string Topic(LetItOutConversationStore store, string title, params LetItOutSavedMessage[] messages)
    { var id = Guid.NewGuid().ToString("N"); store.Create(id); store.SetManualTitle(id, title); store.SaveContent(id, messages, "private unsent draft"); return id; }

    [TestMethod]
    public void ExistingSourcesAreIndexedAndLaterCorrectionsAndDeletionAreRespected()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "private", "history.db"); var store = new LetItOutConversationStore(path);
            var old = Topic(store, "Керамика", new("user", "Керамика 18 декабря в Тихой глине. Код ZETA-4827."), new("assistant", "Подтверждаешь время?"),
                new("user", "Поправка: керамику перенесли на 22 декабря, мастерская Север."), new("assistant", "Значит прежняя дата устарела."));
            var current = Topic(store, "Сегодня", new LetItOutSavedMessage("user", "Хочу вспомнить керамику."));
            // Simulate an older installation with recorded pairs but without the new derived index.
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TRIGGER recall_insert; DROP TRIGGER recall_delete; DROP TRIGGER recall_heading; DROP TABLE conversation_search; DELETE FROM metadata WHERE key='recall_schema'";
                cmd.ExecuteNonQuery();
            }
            store = new(path);
            var hits = store.SearchMemories(current, "ZETA 4827", default);
            Assert.IsTrue(hits.Any(h => h.ConversationId == old && h.Number == 1));
            Assert.IsFalse(hits.Any(h => h.ConversationId == current));
            var source = store.RecallSource(current, hits.First(), "керамика", default)!;
            StringAssert.Contains(source.Original.User, "18 декабря");
            StringAssert.Contains(source.Later.Single().User, "22 декабря");
            store.SaveRecallLinks(current, 1, [hits.First()]); Assert.HasCount(1, store.RecallLinks(current, 1));
            var pair = store.MemoryPairs(old)[0]; Assert.IsTrue(store.TryClaimPair(old, 1));
            Assert.IsTrue(store.SetPairHeading(old, pair, "Уникальнаяфразасводки"));
            Assert.IsTrue(store.SearchMemories(current, "Уникальнаяфразасводки", default).Any());
            Assert.HasCount(0, store.SearchMemories(current, "для это меня", default));
            Assert.HasCount(0, store.SearchMemories(current, "несуществующийкосмодром", default));
            Assert.IsTrue(store.Delete(old));
            Assert.HasCount(0, store.SearchMemories(current, "ZETA 4827", default));
            Assert.HasCount(0, store.RecallLinks(current, 1)); Assert.IsNull(store.RecallSource(current, hits.First(), "керамика", default));
            Assert.HasCount(0, new LetItOutConversationStore(path).SearchMemories(current, "Уникальнаяфразасводки", default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task HistoricalAudioExportsWithItsRecordedRevisionAndWithoutAnInstalledRecognizer()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "private", "history.db"); var store = new LetItOutConversationStore(path);
            var id = Topic(store, "Запись"); var input = Path.Combine(root, "recording.wav"); await File.WriteAllTextAsync(input, "fixture original");
            var file = await store.ImportAttachmentAsync(id, input, default);
            store.SaveContent(id, [new("user", "", Attachments: [file.Id]), new("assistant", "Речь прочитана.")], "");
            await LetItOutAudioTranscription.TranscribeAsync(store, id, file, 2, (_, _, _) => Task.FromResult("Старая речь"), default);
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE attachment_audio SET state=json_set(state,'$.Revision','historical-asr-revision')"; cmd.ExecuteNonQuery();
            }
            var output = Path.Combine(root, "audio.zip"); await LetItOutConversationExport.WriteAsync(store, new(id, output, "ru"), default);
            using var zip = ZipFile.OpenRead(output); using var data = JsonDocument.Parse(await new StreamReader(zip.GetEntry("conversation.json")!.Open()).ReadToEndAsync());
            Assert.AreEqual("historical-asr-revision", data.RootElement.GetProperty("attachments")[0].GetProperty("transcript").GetProperty("Revision").GetString());
            Assert.AreEqual("fixture original", await new StreamReader(zip.GetEntry("Attachments/" + file.Id + "/original.wav")!.Open()).ReadToEndAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedExportReleasesTheCommonOperationAndDoesNotRetireAModel()
    {
        var root = Root(); var previous = ApplicationBackgroundOperations.Current;
        try
        {
            var controller = new BackgroundOperationController(new(Path.Combine(root, "operations", "operation.json")));
            controller.Load(); ApplicationBackgroundOperations.Current = controller;
            var resident = new object(); var retirements = 0;
            ApplicationBackgroundOperations.RegisterModel(resident, _ => { retirements++; return Task.CompletedTask; });
            var store = new LetItOutConversationStore(Path.Combine(root, "private", "history.db"));
            var id = Topic(store, "Пустая карта", new LetItOutSavedMessage("user", "Только начало"));
            await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => LetItOutConversationExport.RunAsync(store,
                new(id, Path.Combine(root, "missing", "cannot-write.zip"), "ru"), default));
            Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase); Assert.IsFalse(controller.BlocksNewWork);
            await LetItOutConversationExport.RunAsync(store, new(id, Path.Combine(root, "ready.zip"), "en"), default);
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase); Assert.IsFalse(controller.BlocksNewWork);
            Assert.IsNotNull(store.Load(id)); Assert.AreEqual(0, retirements); GC.KeepAlive(resident);
        }
        finally { ApplicationBackgroundOperations.Current = previous; Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ArchivePreservesSourcesAttachmentsMetadataAndExistingDestinationOnFailureOrCancel()
    {
        var root = Root();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "private", "history.db"));
            var id = Topic(store, "<script>нельзя</script>", new("user", "Привет <script>alert(1)</script>"), new("assistant", "Привет & как дела?"));
            var text = Path.Combine(root, "Записка.txt"); await File.WriteAllTextAsync(text, "Не менять оригинал. ZETA-4827");
            var file = await store.ImportAttachmentAsync(id, text, default);
            var history = store.Load(id)!.Messages.ToList(); history.Add(new("user", "Вот записка", Attachments: [file.Id]));
            history.Add(new("assistant", "Вижу записку.")); store.SaveContent(id, history, "SECRET_DRAFT_MUST_NOT_EXPORT");
            var pairs = store.MemoryPairs(id);
            Assert.IsTrue(store.PublishMemorySnapshot(id, pairs.Count, "Кратко", "Расширенный автоматический итог", pairs));
            store.SetTreeEnabled(id, true);
            Assert.IsTrue(store.PublishTree(new("LOPATA", "old-version", "let_it_out", "recorded-tree-model", id, 1, pairs.Count,
                DateTimeOffset.UtcNow, "Смысл", [new("root", "", "Главное", "Исходные слова", "theme", "reported", 5, [1])]), 0, pairs));
            var output = Path.Combine(root, "тема.zip"); await LetItOutConversationExport.WriteAsync(store, new(id, output, "ru"), default);
            using (var zip = ZipFile.OpenRead(output))
            {
                var json = await new StreamReader(zip.GetEntry("conversation.json")!.Open()).ReadToEndAsync();
                using var parsed = JsonDocument.Parse(json); var data = parsed.RootElement;
                Assert.AreEqual("LOPATA", data.GetProperty("provenance").GetProperty("Program").GetString());
                Assert.AreEqual("let_it_out/export", data.GetProperty("provenance").GetProperty("Scenario").GetString());
                Assert.IsTrue(data.GetProperty("provenance").GetProperty("Models").EnumerateArray().Any(m => m.GetProperty("Id").GetString() == "recorded-tree-model"));
                Assert.AreEqual(history[0].Content, data.GetProperty("conversation").GetProperty("messages")[0].GetProperty("Content").GetString());
                Assert.IsFalse(json.Contains("SECRET_DRAFT_MUST_NOT_EXPORT")); Assert.IsFalse(json.Contains(root.Replace("\\", "\\\\")));
                var html = await new StreamReader(zip.GetEntry("conversation.html")!.Open()).ReadToEndAsync();
                StringAssert.Contains(html, "LOPATA.Provenance"); StringAssert.Contains(html, "&lt;script&gt;");
                Assert.IsFalse(html.Contains("<script>")); StringAssert.Contains(html, "href=\"#m0\"");
                var entry = zip.GetEntry("Attachments/" + file.Id + "/original.txt")!;
                Assert.AreEqual(await File.ReadAllTextAsync(text), await new StreamReader(entry.Open()).ReadToEndAsync());
            }
            var original = await File.ReadAllBytesAsync(output);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => LetItOutConversationExport.WriteAsync(store, new(id, output, "en"), cancel.Token));
            }
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(output));
            await File.WriteAllTextAsync(store.AttachmentPath(id, file), "changed private copy");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LetItOutConversationExport.WriteAsync(store, new(id, output, "en"), default));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(output)); Assert.HasCount(0, Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }
}
