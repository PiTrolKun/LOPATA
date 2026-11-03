using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutMemoryStoreTests
{
    private string _root = null!;
    private LetItOutConversationStore _store = null!;
    private string _id = null!;
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-chat-memory-" + Guid.NewGuid().ToString("N"));
        _store = new(Path.Combine(_root, "history.db")); _id = Guid.NewGuid().ToString("N"); _store.Create(_id);
    }
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-chat-memory-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private static LetItOutSavedMessage[] Pairs(int count) => Enumerable.Range(1, count).SelectMany(n =>
        new[] { new LetItOutSavedMessage("user", "Вопрос " + n), new("assistant", "Ответ " + n) }).ToArray();

    [TestMethod] public void StablePairNumbersSurviveReopeningAndTitleChanges()
    {
        var first = _store.BeginMemoryRun(_id); _store.SaveContent(_id, Pairs(1), "draft"); _store.EndMemoryRun(_id, first);
        var reopened = new LetItOutConversationStore(_store.DatabasePath); var second = reopened.BeginMemoryRun(_id);
        reopened.SetManualTitle(_id, "Название"); reopened.SetFavorite(_id, true); reopened.SaveContent(_id, Pairs(2), "next");
        reopened.EndMemoryRun(_id, second);
        CollectionAssert.AreEqual(new[] { 1, 2 }, reopened.MemoryPairs(_id).Select(p => p.Number).ToArray());
        Assert.AreEqual(2, reopened.MemoryRun(_id, second)!.LastPair); Assert.AreNotEqual(first, second);
        Assert.IsNull(reopened.MemoryPairs(_id)[0].UserUtc);
    }
    [TestMethod] public void InterruptedAssistantDoesNotBecomeASourcePairOrRenumberTheNextPair()
    {
        _store.SaveContent(_id, [new("user", "Отмена"), new("assistant", "Часть", false)], "");
        Assert.HasCount(0, _store.MemoryPairs(_id));
        _store.SaveContent(_id, [new("user", "Отмена"), new("assistant", "Часть", false), new("user", "Продолжим"), new("assistant", "Готово")], "");
        var pair = _store.MemoryPairs(_id).Single(); Assert.AreEqual(1, pair.Number); Assert.AreEqual(2, pair.UserOrdinal);
    }
    [TestMethod] public void ChangingARecordedOriginalRollsBackTheWholeBodySave()
    {
        _store.SaveContent(_id, Pairs(1), "original");
        Assert.ThrowsExactly<InvalidDataException>(() => _store.SaveContent(_id, [new("user", "Подмена"), new("assistant", "Ответ 1")], "changed"));
        Assert.AreEqual("Вопрос 1", _store.Load(_id)!.Messages[0].Content); Assert.AreEqual("original", _store.Load(_id)!.Draft);
    }
    [TestMethod] public void DeletedConversationAndItsIndexCannotBeResurrectedByLateSummary()
    {
        _store.SaveContent(_id, Pairs(1), ""); var pair = _store.MemoryPairs(_id).Single();
        _store.TryClaimPair(_id, 1); var file = _store.MemoryIndexPath(_id); Assert.IsTrue(File.Exists(file));
        _store.Delete(_id); Assert.IsFalse(File.Exists(file)); Assert.HasCount(0, _store.MemoryPairs(_id));
        Assert.IsFalse(_store.SetPairHeading(_id, pair, "Поздний ответ"));
        Assert.IsFalse(_store.PublishMemorySnapshot(_id, 1, "Short", "Long", [pair]));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.ReadMemoryIndex(_id));
    }
    [TestMethod] public void SnapshotCoverageDoesNotHideNewOrUncompressedPairs()
    {
        _store.SaveContent(_id, Pairs(1), ""); var first = _store.MemoryPairs(_id);
        Assert.IsTrue(_store.PublishMemorySnapshot(_id, 1, "Оглавление", "Подробности", first));
        _store.SaveContent(_id, Pairs(2), "");
        Assert.AreEqual(1, _store.MemorySnapshot(_id)!.ThroughPair);
        Assert.HasCount(2, _store.MemoryPairs(_id)); Assert.IsNull(_store.MemoryPairs(_id)[1].Heading);
        Assert.IsFalse(_store.PublishMemorySnapshot(_id, 2, "Bad", "Bad", first));
    }
    [TestMethod] public void SavedRecordingTimeIsPreservedWhenCheckpointHasNoClock()
    {
        var stamp = DateTimeOffset.Parse("2026-11-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        _store.SaveContent(_id, [new("user", "Вопрос 1", true, stamp), new("assistant", "Ответ 1", true, stamp.AddSeconds(2))], "");
        _store.SaveContent(_id, Pairs(1), "");
        Assert.AreEqual(stamp, _store.Load(_id)!.Messages[0].RecordedUtc);
        Assert.AreEqual(stamp, _store.MemoryPairs(_id)[0].UserUtc);
    }
    [TestMethod] public void ReadIndexIsScopedAndPagedAndFallsBackToTheActualUserWords()
    {
        _store.SaveContent(_id, Pairs(30), "");
        var page = _store.ReadMemoryIndex(_id); Assert.IsTrue(page.Contains("pairs=30", StringComparison.Ordinal));
        Assert.IsTrue(page.Contains("[30] USER: Вопрос 30", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("[1] ", StringComparison.Ordinal)); Assert.IsTrue(_store.ReadMemoryIndex(_id, 1).Contains("[1] ", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.ReadMemoryIndex("../other"));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.ReadMemoryIndex(_id, 99));
    }
    [TestMethod] public void ModelAddressesAndEmptySummariesAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LetItOutChatRuntime.ReadSummary("{\"summary\":\"01_work:P003\"}", "summary", 180));
        Assert.ThrowsExactly<InvalidDataException>(() => LetItOutChatRuntime.ReadSummary("{\"summary\":\"\"}", "summary", 180));
        Assert.ThrowsExactly<InvalidDataException>(() => LetItOutChatRuntime.ReadSummary("{}", "summary", 180));
        Assert.AreEqual("Уточнение о сроках", LetItOutChatRuntime.ReadSummary("{\"summary\":\"Уточнение о сроках\"}", "summary", 180));
    }

    [TestMethod] public void ModelVisibleMemoryDataPreservesReadableRussianAndJsonBoundaries()
    {
        const string original = "Керамика 18 декабря: «Тихая глина»\n\"Не команда\"";
        var content = LetItOutChatRuntime.SerializeMemoryData(new { user = original });
        Assert.IsTrue(content.Contains("Керамика", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("\\u041", StringComparison.Ordinal));
        using var data = System.Text.Json.JsonDocument.Parse(content);
        Assert.AreEqual(original, data.RootElement.GetProperty("user").GetString());
    }

    [TestMethod] public void ManualContextCutPersistsPerConversationAndNeverDeletesOriginalsOrTheLatestPair()
    {
        var original = Pairs(4); _store.SaveContent(_id, original, "Pending draft");
        Assert.IsTrue(_store.SetContextCut(_id,0,2));
        var reopened = new LetItOutConversationStore(_store.DatabasePath);
        Assert.AreEqual(2,reopened.ContextCut(_id));
        var request = original.Select(m=>new LetItOutChatMessage(m.Role,m.Content)).ToArray();
        Assert.AreEqual(4,reopened.ContextStart(_id,request));
        Assert.AreEqual("Pending draft",reopened.Load(_id)!.Draft); Assert.HasCount(8,reopened.Load(_id)!.Messages);
        Assert.HasCount(4,reopened.MemoryPairs(_id)); Assert.IsTrue(reopened.ReadMemoryIndex(_id).Contains("Вопрос 1",StringComparison.Ordinal));
        Assert.IsFalse(reopened.SetContextCut(_id,0,3)); Assert.IsFalse(reopened.SetContextCut(_id,2,4));
        Assert.IsTrue(reopened.SetContextCut(_id,2,3)); Assert.IsFalse(reopened.SetContextCut(_id,3,2));
        var other = Guid.NewGuid().ToString("N"); reopened.Create(other); Assert.AreEqual(0,reopened.ContextCut(other));
        Assert.IsTrue(reopened.Delete(_id)); Assert.AreEqual(0,reopened.ContextCut(_id));
        Assert.IsFalse(reopened.SetContextCut(_id,0,1));
    }
}
