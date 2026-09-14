using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryQwenTests
{
    [TestMethod]
    public void StructuredReplyAcceptsOnlyWholeJsonWithOptionalFence()
    {
        Assert.AreEqual("{\"facts\":[]}", LiteraryStructuredReply.Json("```json\r\n{\"facts\":[]}\r\n```"));
        Assert.Throws<JsonException>(() => LiteraryStructuredReply.Json("Комментарий\n{\"facts\":[]}"));
        Assert.Throws<JsonException>(() => LiteraryStructuredReply.Json("```json\n{broken}\n```"));
    }

    [TestMethod]
    public async Task ThinkingRemainsInRawEvidenceButNeverEntersVisibleAnswerOrJson()
    {
        var raw = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Скрытое обдумывание\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"task\\\":\\\"Проверь ключ\\\"}\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var evidence = new StringBuilder();
        var answer = await LiteraryLoopStream.ReadAsync(stream, null, line => evidence.AppendLine(line), CancellationToken.None);
        Assert.IsTrue(evidence.ToString().Contains("Скрытое обдумывание"));
        using var json = JsonDocument.Parse(answer);
        Assert.AreEqual("Проверь ключ", json.RootElement.GetProperty("task").GetString());
        Assert.IsFalse(answer.Contains("Скрытое"));
    }

    [TestMethod]
    public void BothLogicalRolesUseOneTextOnlySlotWithoutImplicitHistoryCache()
    {
        var args = LiteraryChatRuntime.Arguments("model.gguf", 12345);
        Assert.AreEqual("1", args[Array.IndexOf(args, "-np") + 1]);
        Assert.AreEqual("0", args[Array.IndexOf(args, "-c") + 1]);
        Assert.AreEqual("on", args[Array.IndexOf(args, "--fit") + 1]);
        Assert.IsFalse(args.Any(a => a.Contains("mmproj") || a.Contains("image")));
        foreach (var role in Enum.GetValues<LiteraryChatProfile>())
        {
            using var request = JsonDocument.Parse(LiteraryModelPolicy.Request(role, []));
            Assert.AreEqual(0, request.RootElement.GetProperty("id_slot").GetInt32());
            Assert.IsFalse(request.RootElement.GetProperty("cache_prompt").GetBoolean());
            Assert.IsTrue(request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        }
        Assert.IsTrue(LiteraryPreparation.Licenses.Contains(ManagedModelCatalog.OmniGammaArtifactId));
    }
}
