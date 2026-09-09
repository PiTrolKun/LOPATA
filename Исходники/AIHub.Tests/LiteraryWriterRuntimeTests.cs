using AIHub.Models;
using AIHub.Services;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryChatRuntimeTests
{
    [TestMethod]
    public void WriterUsesAcceptedDraftAndLatestTaskWhileCriticRetainsOwnConversation()
    {
        ImageAnalysisHiddenMessage[] history = [new() { Role = "user", Content = "старое задание" },
            new() { Role = "assistant", Content = "непринятый ответ" }, new() { Role = "user", Content = "новое задание" }];
        var project = new LiteraryProject { Premise = "Русский замысел" };
        var writer = LiteraryModelPolicy.Messages(LiteraryChatProfile.Writer, history, "Принятый текст", project);
        Assert.AreEqual(2, writer.Length);
        Assert.IsTrue(writer[0].Content.Contains("Принятый текст"));
        Assert.IsFalse(writer.Any(m => m.Content.Contains("непринятый ответ")));
        Assert.AreEqual("новое задание", writer[^1].Content);
        var critic = LiteraryModelPolicy.Messages(LiteraryChatProfile.Advisor, history, "Принятый текст", project);
        Assert.AreEqual(4, critic.Length);
        Assert.AreEqual("непринятый ответ", critic[2].Content);
    }

    [TestMethod]
    public void AdmissionReservesFullReplyAndDoesNotConfuseCharactersWithTokens()
    {
        foreach (var role in Enum.GetValues<LiteraryChatProfile>())
        {
            var maximum = LiteraryModelPolicy.ContextTokens(role) - LiteraryModelPolicy.ReplyTokens(role) - LiteraryModelPolicy.SafetyTokens;
            LiteraryModelPolicy.ValidateBudget(role, maximum, 2500);
            Assert.Throws<ImageAnalysisContextExhaustedException>(() => LiteraryModelPolicy.ValidateBudget(role, maximum + 1, 2500));
            Assert.Throws<LiteraryDraftLimitException>(() => LiteraryModelPolicy.ValidateBudget(role, 3000, 2501));
        }
        Assert.Throws<LiteraryDraftLimitException>(() => LiteraryModelPolicy.Messages(LiteraryChatProfile.Writer, [], new string('я', 7501), new()));
    }

    [TestMethod]
    public async Task LengthStopPreservesStreamedTextButIsNotSuccess()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Частичный текст\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n"));
        var received = new StringBuilder();
        try
        {
            await LiteraryRawProtocol.ReadAsync(stream, new Capture(received), _ => { }, CancellationToken.None, true);
            Assert.Fail("Incomplete reply was accepted.");
        }
        catch (ImageAnalysisContextExhaustedException error)
        { Assert.IsTrue(error.OutputTruncated); Assert.AreEqual("Частичный текст", received.ToString()); }
    }
    private sealed class Capture(StringBuilder text) : IProgress<ModelStreamChunk>
    { public void Report(ModelStreamChunk value) => text.Append(value.Text); }

    [TestMethod]
    public void DraftRoundTripDoesNotAlterProjectMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "project.json"), "metadata");
            var store = new LiteraryDraftStore(root);
            store.Save("Первая строка\nВторая строка");
            Assert.AreEqual("Первая строка\nВторая строка", new LiteraryDraftStore(root).Load());
            store.Save("Правка"); Assert.AreEqual("Правка", store.Load());
            Assert.AreEqual("metadata", File.ReadAllText(Path.Combine(root, "project.json")));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
