using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicPoetryLanguageTests
{
    [TestMethod]
    public async Task InterfaceLanguageReachesCountAndBothGenerationProtocolsWithoutRewritingUserIntent()
    {
        var candidate = MusicPoetryModelCatalog.All.Single(c => c.Id == "core");
        var model = new DebugModelInfo { Name = candidate.FileName, Path = candidate.FileName, Format = "gguf", IsCoreModel = true };
        var session = MusicPoetryProtocol.Create(new() { Lyrics = "[Verse]\nExisting English lyrics" }, key => key);
        session.Messages.Add(new("user", "Напиши песню на испанском языке.", "first"));
        session.Messages.Add(new("user", "Улучши рифмы.", "second"));
        foreach (var language in new[] { "ru", "en", "en-US" })
        {
            string? countedSystem = null;
            var context = await MusicPoetryContextWindow.BuildAsync(session, "Keep required [Verse] tags", 32768, 6144,
                (system, messages) => { countedSystem = system; return Task.FromResult(MusicPoetryContextWindow.Estimate(system, messages)); }, true, language);
            Assert.AreEqual(countedSystem, context.System);
            using var workspace = JsonDocument.Parse(context.System[(context.System.IndexOf('\n') + 1)..]);
            Assert.AreEqual(language.ToLowerInvariant(), workspace.RootElement.GetProperty("interfaceLanguage").GetString());
            Assert.AreEqual(session.Lyrics, workspace.RootElement.GetProperty("song").GetString());
            CollectionAssert.AreEqual(session.Messages.Select(m => m.Text).ToArray(), context.Messages.Select(m => m.Text).ToArray());
            foreach (var enabled in new[] { false, true }) foreach (var assemble in new[] { false, true })
            {
                var reasoning = MusicPoetryReasoning.Resolve(model, enabled ? "on" : "off");
                using var request = JsonDocument.Parse(MusicPoetryGeneration.Payload(model, context, reasoning.ReplyReserve(32768), reasoning, assemble, enabled));
                Assert.AreEqual(context.System, request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
                Assert.AreEqual(enabled, request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
                Assert.AreEqual(enabled ? 2048 : 0, reasoning.Budget);
            }
        }
        using var fallback = JsonDocument.Parse(MusicPoetryProtocol.System(session, "guide").Split('\n', 2)[1]);
        Assert.AreEqual("ru", fallback.RootElement.GetProperty("interfaceLanguage").GetString());
    }
}
