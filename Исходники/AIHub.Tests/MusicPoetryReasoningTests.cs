using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicPoetryReasoningTests
{
    private static DebugModelInfo Model(string id)
    {
        var candidate = MusicPoetryModelCatalog.All.Single(c => c.Id == id);
        return new() { Path = candidate.FileName, Name = candidate.FileName, Format = candidate.Format, IsRunnable = true };
    }

    [TestMethod]
    public void TestedModelsDefaultOnButExplicitOffAndUnsupportedModelsNeverInheritIt()
    {
        var connected = new[] { "core", "profile-kaine", "profile-abliterated", "beta", "gamma", "executor", "profile-gemma-poetry" };
        foreach (var id in connected)
        {
            var model = Model(id);
            Assert.AreEqual(id, MusicPoetryReasoning.ModelId(model));
            var on = MusicPoetryReasoning.Resolve(model);
            var expectedBudget = id == "profile-gemma-poetry" ? 4096 : 2048;
            Assert.IsTrue(on.Enabled); Assert.AreEqual(expectedBudget, on.Budget); Assert.AreEqual(expectedBudget + 4096, on.ReplyReserve(32768));
            var off = MusicPoetryReasoning.Resolve(model, "off");
            Assert.IsFalse(off.Enabled); Assert.AreEqual(0, off.Budget); Assert.AreEqual(4096, off.ReplyReserve(32768));
            var error = Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryReasoning.Resolve(model, "future-level"));
            Assert.AreEqual("Music.Poetry.ReasoningChoiceUnavailable", error.Message);
        }
        foreach (var candidate in MusicPoetryModelCatalog.All.Where(c => !connected.Contains(c.Id)))
            Assert.AreEqual(MusicPoetryReasoningOptions.Disabled, MusicPoetryReasoning.Resolve(Model(candidate.Id), "on"));
        Assert.IsFalse(MusicPoetryReasoning.Supports(null));
    }

    [TestMethod]
    [DataRow("core")]
    [DataRow("profile-kaine")]
    [DataRow("profile-abliterated")]
    [DataRow("beta")]
    [DataRow("gamma")]
    [DataRow("executor")]
    [DataRow("profile-gemma-poetry")]
    public void BothRequestKindsReserveThoughtAndKeepTheirSchemaSamplingAndMessages(string modelId)
    {
        var model = Model(modelId);
        var context = new MusicPoetryContext("system", [new("user", "request", "turn")], 100, 32768, false);
        foreach (var assembly in new[] { false, true })
        {
            var on = MusicPoetryReasoning.Resolve(model, "on"); var off = MusicPoetryReasoning.Resolve(model, "off");
            using var a = JsonDocument.Parse(MusicPoetryGeneration.Payload(model, context, on.ReplyReserve(context.Capacity), on, assembly));
            using var b = JsonDocument.Parse(MusicPoetryGeneration.Payload(model, context, off.ReplyReserve(context.Capacity), off, assembly));
            Assert.AreEqual(modelId == "profile-gemma-poetry" ? 8192 : 6144, a.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.AreEqual(4096, b.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.IsTrue(a.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.IsFalse(b.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            foreach (var property in new[] { "temperature", "top_p", "top_k", "min_p", "repeat_penalty", "presence_penalty", "response_format", "messages" })
                Assert.AreEqual(a.RootElement.GetProperty(property).GetRawText(), b.RootElement.GetProperty(property).GetRawText());
            Assert.AreEqual(JsonSerializer.Serialize(assembly ? MusicPoetryProtocol.AssemblySchema : MusicPoetryProtocol.Schema),
                a.RootElement.GetProperty("response_format").GetRawText());
        }
    }

    [TestMethod]
    public void GemmaHasFixed4096WithoutEffortOrPreviousThoughtProjection()
    {
        const string id = "profile-gemma-poetry";
        Assert.IsFalse(MusicPoetryReasoning.Extended(id));
        var options = MusicPoetryReasoning.Resolve(Model(id), preference: new() { ModeId = "on", Budget = 4096 });
        Assert.AreEqual(4096, options.Budget); Assert.IsNull(options.Effort);
        Assert.IsFalse(options.PreserveThoughts); Assert.IsFalse(options.HistoryControl);
        CollectionAssert.AreEqual(new[] { "enable_thinking" }, options.TemplateOptions().Keys.ToArray());
        foreach (var invalid in new MusicPoetryReasoningPreference[] {
            new() { ModeId = "on", Budget = 2048 }, new() { ModeId = "off", Budget = 8192 },
            new() { ModeId = "on", Effort = "medium" }, new() { ModeId = "on", PreserveThoughts = true } })
            Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryReasoning.Resolve(Model(id), preference: invalid));
        var session = MusicPoetryProtocol.Create(new(), key => key);
        session.Messages.AddRange([new("user", "old", "old"), new("assistant", "chat", "old"), new("user", "next", "new")]);
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 1, "{\"chat\":\"chat\",\"song\":null,\"parameters\":null}", "applied", []));
        session.Thoughts.Add(new() { TurnId = "old", Attempt = 1, ModelId = id, State = MusicPoetryThought.Completed, Text = "GEMMA_PRIVATE_THOUGHT" });
        var selected = MusicPoetryReasoningHistory.Select(session, options, id);
        Assert.IsTrue(selected.All(message => message.ReasoningText is null));
        Assert.IsFalse(JsonSerializer.Serialize(MusicPoetryReasoningHistory.Messages("system", selected)).Contains("GEMMA_PRIVATE_THOUGHT"));
        Assert.AreEqual("GEMMA_PRIVATE_THOUGHT", session.Thoughts[0].Text);
    }

    [TestMethod]
    public void OnlyFinalAnswerCanReachSongOrPartialReply()
    {
        const string answer = "{\"chat\":\"hello\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}";
        string Response(string content, string finish) => JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = finish, message = new { content, reasoning_content = "private thought" } } } });
        Assert.AreEqual(answer, MusicPoetryGeneration.Answer(Response(answer, "stop")));
        Assert.AreEqual(answer, MusicPoetryGeneration.Answer(Response("<think>private thought</think>" + answer, "stop")));
        var incomplete = Assert.ThrowsExactly<MusicPoetryIncompleteReply>(() => MusicPoetryGeneration.Answer(
            Response("<think>private thought</think>" + answer, "length")));
        Assert.AreEqual(answer, incomplete.Reply);
        Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryGeneration.Answer(Response("<think>unfinished", "length")));
    }

    [TestMethod]
    public async Task TightContextRejectsInsteadOfSilentlyDisablingOrReducingThoughtBudget()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key);
        session.Messages.Add(new("user", "request", "turn"));
        var on = MusicPoetryReasoning.Resolve(Model("core"), "on");
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MusicPoetryContextWindow.BuildAsync(
            session, "guide", 1000, on.ReplyReserve(1000), (_, _) => Task.FromResult(100), false));
        Assert.AreEqual("Music.Poetry.ContextFull", error.Message); Assert.AreEqual(2048, on.Budget);
        var context = await MusicPoetryContextWindow.BuildAsync(session, "guide", 1000,
            MusicPoetryReasoningOptions.Disabled.ReplyReserve(1000), (_, _) => Task.FromResult(100), false);
        Assert.AreEqual(100, context.Tokens);
    }

    [TestMethod]
    public void AdvancedChoicesUseNativeCapabilitiesAndNeverLowerAnInvalidSavedValue()
    {
        foreach (var id in new[] { "gamma", "executor" })
        {
            var initial = MusicPoetryReasoning.Resolve(Model(id));
            Assert.IsTrue(initial.PreserveThoughts);
            Assert.AreEqual(id == "gamma" ? "medium" : null, initial.Effort);
            foreach (var budget in new[] { 2048, 4096 }) foreach (var preserve in new[] { true, false })
            {
                var preference = new MusicPoetryReasoningPreference { ModeId = "on", Budget = budget,
                    Effort = id == "gamma" ? "xhigh" : null, PreserveThoughts = preserve };
                var on = MusicPoetryReasoning.Resolve(Model(id), preference: preference);
                Assert.AreEqual(budget + 4096, on.ReplyReserve(32768));
                Assert.AreEqual(preserve, (bool)on.TemplateOptions()["preserve_thinking"]);
                Assert.AreEqual(id == "gamma", on.TemplateOptions().ContainsKey("reasoning_effort"));
                var off = MusicPoetryReasoning.Resolve(Model(id), preference: preference with { ModeId = "off" });
                Assert.AreEqual(0, off.Budget); Assert.IsFalse((bool)off.TemplateOptions()["preserve_thinking"]);
            }
            foreach (var invalid in new[] { new MusicPoetryReasoningPreference { ModeId = "on", Budget = 8192 },
                new() { ModeId = "off", Effort = "low" }, new() { ModeId = "on", Effort = "future" } })
                Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryReasoning.Resolve(Model(id), preference: invalid));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryReasoning.Resolve(Model("core"),
            preference: new() { ModeId = "on", Budget = 4096 }));
    }

    [TestMethod]
    public async Task HistoryPairsOnlyAcceptedMatchingThoughtsAndKeepsAuthenticFinalJson()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key);
        const string raw = "{\"chat\":\"accepted chat\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}";
        var user = new MusicPoetryMessage("user", "first", "old");
        var assistant = new MusicPoetryMessage("assistant", "accepted chat", "old");
        session.Messages.AddRange([user, assistant, new("user", "next", "new")]);
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 1, "rejected raw", "invalid", []));
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 2, raw, "applied", []));
        session.Thoughts.AddRange([
            new() { TurnId = "old", Attempt = 1, ModelId = "gamma", State = MusicPoetryThought.Completed, Text = "REJECTED" },
            new() { TurnId = "old", Attempt = 2, ModelId = "executor", State = MusicPoetryThought.Completed, Text = "OTHER_MODEL" },
            new() { TurnId = "old", Attempt = 2, ModelId = "gamma", State = MusicPoetryThought.Interrupted, Text = "INTERRUPTED" },
            new() { TurnId = "old", Attempt = 2, State = MusicPoetryThought.Completed, Text = "LEGACY_UNKNOWN_MODEL" },
            new() { TurnId = "old", Attempt = 2, ModelId = "gamma", State = MusicPoetryThought.Completed, Text = "REAL_THOUGHT" }]);
        var on = MusicPoetryReasoning.Resolve(Model("gamma"));
        var context = await MusicPoetryContextWindow.BuildAsync(session, "guide", 32768, on.ReplyReserve(32768),
            (_, messages) => { Assert.AreEqual("REAL_THOUGHT", messages[1].ReasoningText); return Task.FromResult(200); },
            false, reasoning: on, modelId: "gamma");
        Assert.AreEqual(raw, context.Messages[1].Text); Assert.AreEqual("accepted chat", assistant.Text);
        Assert.IsTrue(assistant.InContext);
        using var payload = JsonDocument.Parse(MusicPoetryGeneration.Payload(Model("gamma"), context, 6144, on, false));
        var rendered = payload.RootElement.GetProperty("messages")[2];
        Assert.AreEqual(raw, rendered.GetProperty("content").GetString());
        Assert.AreEqual("REAL_THOUGHT", rendered.GetProperty("reasoning_content").GetString());
        Assert.IsFalse(payload.RootElement.ToString().Contains("REJECTED"));
        Assert.IsTrue(MusicPoetryContextWindow.Estimate(context.System, context.Messages)
            > MusicPoetryContextWindow.Estimate(context.System, session.Messages));
        foreach (var options in new[] { on with { PreserveThoughts = false }, MusicPoetryReasoningOptions.Disabled })
            Assert.IsTrue(MusicPoetryReasoningHistory.Select(session, options, "gamma").All(m => m.ReasoningText is null));
        session.Thoughts.RemoveAt(session.Thoughts.Count - 1);
        Assert.IsTrue(MusicPoetryReasoningHistory.Select(session, on, "gamma").All(m => m.ReasoningText is null));
    }

    [TestMethod]
    public void EmptyChatStillReturnsAnAcceptedAuthenticPairWithoutPersistingProjection()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key);
        const string raw = "{\"chat\":\"\",\"song\":null,\"parameters\":\"[Music] Blues\",\"variation\":null,\"actions\":[\"parameters\"]}";
        session.Messages.AddRange([new("user", "blues", "old"), new("user", "next", "new")]);
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 1, raw, "applied", ["parameters"]));
        session.Thoughts.Add(new() { TurnId = "old", Attempt = 1, ModelId = "executor", State = MusicPoetryThought.Completed, Text = "THOUGHT" });
        var messages = MusicPoetryReasoningHistory.Select(session, MusicPoetryReasoning.Resolve(Model("executor")), "executor");
        Assert.AreEqual(3, messages.Count); Assert.AreEqual("assistant", messages[1].Role);
        Assert.AreEqual(raw, messages[1].Text); Assert.AreEqual("THOUGHT", messages[1].ReasoningText);
        Assert.AreEqual(2, session.Messages.Count);
        var json = JsonSerializer.Serialize(messages);
        Assert.IsFalse(json.Contains("ReasoningText")); Assert.IsFalse(json.Contains("ContextSource"));
    }

    [TestMethod]
    public async Task WholeOldTurnIsTrimmedIncludingThoughtsButLatestReserveNeverShrinks()
    {
        var session = MusicPoetryProtocol.Create(new(), key => key);
        session.Messages.AddRange([new("user", "old", "old"), new("assistant", "chat", "old"), new("user", "latest", "new")]);
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 1, "raw", "applied", []));
        session.Thoughts.Add(new() { TurnId = "old", Attempt = 1, ModelId = "gamma", State = MusicPoetryThought.Completed, Text = new('t', 5000) });
        var on = MusicPoetryReasoning.Resolve(Model("gamma"), preference: new() { ModeId = "on", Budget = 4096 });
        var context = await MusicPoetryContextWindow.BuildAsync(session, "", 10000, on.ReplyReserve(10000),
            (_, messages) => Task.FromResult(messages.Any(m => m.ReasoningText is not null) ? 7000 : 100),
            false, reasoning: on, modelId: "gamma");
        Assert.AreEqual(1, context.Messages.Length); Assert.AreEqual("new", context.Messages[0].TurnId);
        Assert.IsFalse(session.Messages[1].InContext); Assert.AreEqual(4096, on.Budget);
    }
}
