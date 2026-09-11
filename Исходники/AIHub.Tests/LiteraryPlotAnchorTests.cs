using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryPlotAnchorTests
{
    private string _temp = "", _root = "";
    private LiteraryProjectLayout _layout = null!;
    [TestInitialize] public void Setup()
    {
        _temp = Path.Combine(Path.GetTempPath(), "lopata-plot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_temp);
        var entry = new LiteraryProjectStore(Path.Combine(_temp, "registry.json")).Create(_temp, new() { ProjectName = "Plot", Genres = ["fantasy"] }, []);
        _root = entry.ProjectPath; _layout = new(_root);
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
    [TestMethod] public void RolesHaveIndependentPersistentAnchorsAndBackups()
    {
        var writer = new LiteraryPlotAnchorStore(_layout, LiteraryChatProfile.Writer); var advisor = new LiteraryPlotAnchorStore(_layout, LiteraryChatProfile.Advisor);
        Assert.AreEqual("", writer.Load().Text); Assert.AreEqual("", advisor.Load().Text);
        var change = writer.Save("Появление кота", writer.Load().Revision);
        advisor.Save("Проверка непрерывности", advisor.Load().Revision);
        Assert.AreEqual("", change.Before.Text); Assert.AreEqual("Появление кота", change.After.Text);
        Assert.AreEqual("Появление кота", new LiteraryPlotAnchorStore(new(_root), LiteraryChatProfile.Writer).Load().Text);
        writer.Save("", change.After.Revision);
        Assert.AreEqual("", writer.Load().Text); Assert.AreEqual("Проверка непрерывности", advisor.Load().Text);
        StringAssert.Contains(File.ReadAllText(writer.FilePath + ".bak"), change.After.Revision);
        Assert.IsTrue(writer.FilePath.StartsWith(_root + Path.DirectorySeparatorChar));
    }
    [TestMethod] public void InvalidForeignAndStaleAnchorCannotSilentlyReplacePlan()
    {
        var writer = new LiteraryPlotAnchorStore(_layout, LiteraryChatProfile.Writer);
        var before = writer.Load(); writer.Save("Новый план", before.Revision);
        Assert.Throws<IOException>(() => writer.Save("Чужая правка", before.Revision));
        Assert.Throws<InvalidDataException>(() => writer.Save(new string('я', LiteraryPlotAnchorStore.MaxCharacters + 1), writer.Load().Revision));
        var advisor = new LiteraryPlotAnchorStore(_layout, LiteraryChatProfile.Advisor);
        File.Copy(writer.FilePath, advisor.FilePath);
        Assert.Throws<InvalidDataException>(() => advisor.Load());
        File.WriteAllText(writer.FilePath, "broken"); Assert.Throws<JsonException>(() => writer.Load());
        File.WriteAllBytes(writer.FilePath, [0xff, 0xfe]); Assert.Throws<InvalidDataException>(() => writer.Load());
        Assert.Throws<InvalidDataException>(() => writer.Save("\uD800", before.Revision));
    }
    [TestMethod] public void DialogueRestoreKeepsCompletedWriterExchangesOnly()
    {
        var store = new LiteraryDialogueStore(_layout, LiteraryChatProfile.Writer);
        var dialog = store.Load();
        dialog.Messages = [new(true, "Кот"), new(false, "Вариант"), new(true, "Отменённая просьба"), new(false, "Обрыв", false), new(true, "Продолжением"), new(false, "Продолжение")];
        store.Save(dialog);
        var context = LiteraryDialogueStore.Context(store.Load()).ToArray();
        Assert.AreEqual(4, context.Length); Assert.AreEqual("Кот", context[0].Content); Assert.AreEqual("Продолжением", context[2].Content);
        var anchor = new LiteraryPlotAnchorStore(_layout, LiteraryChatProfile.Writer); anchor.Save("Каркас", anchor.Load().Revision);
        dialog.Messages.Clear(); store.Save(dialog);
        Assert.AreEqual(0, LiteraryDialogueStore.Context(store.Load()).Count()); Assert.AreEqual("Каркас", anchor.Load().Text);
    }
    [TestMethod] [DataRow(LiteraryChatProfile.Writer)] [DataRow(LiteraryChatProfile.Advisor)]
    public async Task PruningRetainsOwnAnchorDraftAndFollowup(LiteraryChatProfile role)
    {
        var chapters = new LiteraryChapterStore(_root); chapters.Open();
        var snapshot = LiteraryEditorSnapshot.Capture(_layout.ProjectId, _root, chapters.Index, "Набросок сейчас", true);
        var plan = "СЮЖЕТ-726: кот появляется после молнии";
        ImageAnalysisHiddenMessage[] conversation = [new() { Role = "user", Content = "Старое обсуждение OLD" }, new() { Role = "assistant", Content = "OLD" },
            new() { Role = "user", Content = "Кот" }, new() { Role = "assistant", Content = "Предложение" }, new() { Role = "user", Content = "Продолжением" }];
        var baseline = LiteraryModelPolicy.Messages(role, conversation, snapshot.Text, new(), includeDraft: false, plotAnchor: plan);
        var reader = new LiteraryReadingSession(new(snapshot), (messages, _) => Task.FromResult(!messages.Any(m => m.Content.Contains("OLD"))), (_, _) => { }, role, plotAnchor: plan);
        var final = await reader.PrepareAsync(baseline, (_, _) => throw new AssertFailedException("No source planning for local task"), null, default,
            (messages, _) => { StringAssert.Contains(messages[^1].Content, plan); StringAssert.Contains(messages[^1].Content, "Кот");
                return Task.FromResult("{\"scope\":\"editor\",\"projectQuery\":\"\",\"referenceQuery\":\"\",\"partNumber\":\"\"}"); });
        StringAssert.Contains(final[0].Content, plan); StringAssert.Contains(final[^1].Content, snapshot.Text);
        Assert.IsTrue(final[^1].Content.EndsWith("Продолжением")); Assert.AreEqual("Кот", final[1].Content);
        Assert.IsFalse(final.Any(m => m.Content.Contains("OLD")));
    }
}
