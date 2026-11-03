using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Data.Sqlite;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutConversationStoreTests
{
    private string _root = null!;
    private string Database => Path.Combine(_root, "history.db");
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "lopata-history-" + Guid.NewGuid().ToString("N")); }
    [TestCleanup] public void Cleanup()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("lopata-history-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
    private static LetItOutSavedMessage[] Exchange(int answers) => Enumerable.Range(0, answers)
        .SelectMany(index => new[] { new LetItOutSavedMessage("user", "Question " + index), new("assistant", "Answer " + index) }).ToArray();

    [TestMethod]
    public void BodyDraftNameAndFavoriteSurviveASecondStoreInstance()
    {
        var store = new LetItOutConversationStore(Database); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        var messages = Exchange(1).Append(new LetItOutSavedMessage("assistant", "Unfinished", false)).ToArray();
        store.SaveContent(id, messages, "A private draft\nwith two lines");
        store.SetManualTitle(id, "Мой разговор"); store.SetFavorite(id, true);
        var reopened = new LetItOutConversationStore(Database).Load(id)!;
        CollectionAssert.AreEqual(messages, reopened.Messages.ToArray());
        Assert.AreEqual("A private draft\nwith two lines", reopened.Draft);
        Assert.AreEqual("Мой разговор", reopened.Summary.Title);
        Assert.IsTrue(reopened.Summary.Favorite); Assert.IsTrue(reopened.Summary.ManualTitle);
        store.SaveContent(id, Exchange(2), "Next draft");
        Assert.AreEqual("Мой разговор", store.Load(id)!.Summary.Title);
        Assert.IsTrue(store.Load(id)!.Summary.Favorite);
    }

    [TestMethod]
    public void OnlyTwoCompleteAnswersAllowOneAtomicNamingClaim()
    {
        var store = new LetItOutConversationStore(Database); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        store.SaveContent(id, Exchange(1).Append(new("assistant", "Partial", false)).ToArray(), "");
        Assert.IsFalse(store.TryBeginAutoTitle(id));
        store.SaveContent(id, Exchange(2), "");
        var claims = new bool[2];
        Parallel.For(0, 2, index => claims[index] = new LetItOutConversationStore(Database).TryBeginAutoTitle(id));
        Assert.AreEqual(1, claims.Count(claim => claim));
        Assert.IsTrue(store.TryApplyAutoTitle(id, "Придуманное название"));
        Assert.IsFalse(store.TryBeginAutoTitle(id));
        Assert.IsFalse(store.Load(id)!.Summary.ManualTitle);
    }

    [TestMethod]
    public void ManualTitleWinsBothBeforeAndDuringNamingAndDeletionCannotBeReversedByALateResult()
    {
        var store = new LetItOutConversationStore(Database); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        store.SaveContent(id, Exchange(2), "draft");
        Assert.IsTrue(store.TryBeginAutoTitle(id));
        new LetItOutConversationStore(Database).SetManualTitle(id, "Моё название");
        Assert.IsFalse(store.TryApplyAutoTitle(id, "Late automatic name"));
        Assert.AreEqual("Моё название", store.Load(id)!.Summary.Title);
        var before = Guid.NewGuid().ToString("N"); store.Create(before); store.SaveContent(before, Exchange(2), "");
        store.SetManualTitle(before, "Named early"); Assert.IsFalse(store.TryBeginAutoTitle(before));
        Assert.IsTrue(store.Delete(id)); Assert.IsNull(store.Load(id));
        Assert.IsTrue(store.IsDeleted(id));
        Assert.ThrowsExactly<InvalidDataException>(() => new LetItOutConversationStore(Database).Create(id));
        Assert.IsFalse(store.TryApplyAutoTitle(id, "Deleted name"));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveContent(id, Exchange(2), "late draft"));
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public void ListingReadsMetadataAndDoesNotSilentlyReplaceADamagedBodyOrANewerSchema()
    {
        var store = new LetItOutConversationStore(Database); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        using (var connection = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE conversation SET messages='broken json'"; command.ExecuteNonQuery();
        }
        Assert.AreEqual(id, store.List().Single().Id);
        Assert.ThrowsExactly<JsonException>(() => store.Load(id));
        using (var connection = new SqliteConnection("Data Source=" + Database + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<InvalidDataException>(() => new LetItOutConversationStore(Database));
    }

    [TestMethod]
    public void TitlesArePlainParametersAndDoNotSplitUnicodeGraphemes()
    {
        var store = new LetItOutConversationStore(Database); var id = Guid.NewGuid().ToString("N"); store.Create(id);
        store.SetManualTitle(id, "x'; DELETE FROM conversation; --"); Assert.AreEqual(1, store.List().Count);
        var emoji = string.Concat(Enumerable.Repeat("🌿", 90));
        Assert.AreEqual(string.Concat(Enumerable.Repeat("🌿", 80)), LetItOutConversationStore.Title(emoji));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SetManualTitle(id, " \r\n "));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load("../history"));
    }

    [TestMethod]
    public void TitlePayloadHasItsOwnInstructionsAndDoesNotMutateConversation()
    {
        var messages = new[] { new LetItOutChatMessage("user", "Друг постоянно просит о помощи."),
            new("assistant", "Как ты обычно отвечаешь?"), new("user", "Соглашаюсь."), new("assistant", "Чего ты опасаешься, если откажешь?") };
        var request = new LetItOutTitleRequest(Guid.NewGuid().ToString("N"), "model.gguf", messages, "ru");
        using var title = JsonDocument.Parse(LetItOutChatRuntime.BuildTitlePayload(request));
        Assert.AreEqual(2, title.RootElement.GetProperty("messages").GetArrayLength());
        Assert.AreEqual(64, title.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.IsTrue(title.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains(messages[0].Content));
        using var ordinary = JsonDocument.Parse(LetItOutChatRuntime.BuildPayload(new(request.SessionId, Guid.NewGuid().ToString("N"),
            "model.gguf", messages.Append(new("user", "Продолжим")).ToArray(), "ru")));
        Assert.IsFalse(ordinary.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("от 2 до 6 слов"));
        Assert.AreEqual(6, ordinary.RootElement.GetProperty("messages").GetArrayLength());
        Assert.AreEqual(4, messages.Length);
    }
}
