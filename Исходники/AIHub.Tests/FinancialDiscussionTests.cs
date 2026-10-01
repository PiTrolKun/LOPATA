using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass]
public sealed class FinancialDiscussionTests
{
    private static FinancialInput Input() => FinancialScenarioTests.CompleteInput() with
    {
        Schema = 2, Unit = "RUB", Person = new("FICTIONAL", null, "TEST", "employed"),
        Answers = FinancialQuestions.All.Select(q => new FinancialAnswer(q.Id, FinancialAnswerKind.Known,
            q.Id == "nicotine" ? 12000 : q.Id == "food" ? 18000 : q.Id == "stable_income" ? 60000 : 0)).ToList()
    };

    [TestMethod]
    public void AcceptedChangesReplaceInsteadOfDoubleCountingAndDoNotChangeCalculation()
    {
        var input = Input(); var before = FinancialCalculator.Calculate(input); var state = new FinancialDiscussionState();
        state.Pending = new("nicotine", 10000, FinancialPeriod.Month); FinancialDiscussionPlan.Accept(input, state);
        state.Pending = new("nicotine", 8000, FinancialPeriod.Month); FinancialDiscussionPlan.Accept(input, state);
        Assert.HasCount(1, state.Accepted); Assert.AreEqual(4000m, state.Accepted.Single().SavingMonthly);
        Assert.AreEqual(before.Personal, FinancialCalculator.Calculate(input).Personal);
        Assert.IsNull(state.Pending);
    }

    [TestMethod]
    public void ProgramProtectsRequiredIncomeUnknownAndRejectedItems()
    {
        var input = Input(); var state = new FinancialDiscussionState();
        foreach (var id in new[] { "food", "stable_income", "not-a-source", "phone" })
            Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Calculate(input, state, new(id, 0, FinancialPeriod.Month)));
        FinancialDiscussionPlan.Protect(state, "nicotine");
        Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Calculate(input, state, new("nicotine", 0, FinancialPeriod.Month)));
        input.Answers[input.Answers.FindIndex(a => a.Id == "nicotine")] = new("nicotine", FinancialAnswerKind.Unknown);
        Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Calculate(input, new(), new("nicotine", 0, FinancialPeriod.Month)));
    }

    [TestMethod]
    public void PeriodsAndLegacyOutsidePaymentUseOnlyPersonalShare()
    {
        var input = Input(); var state = new FinancialDiscussionState();
        var proposal = new FinancialSavingProposal("nicotine", 2100, FinancialPeriod.Week);
        Assert.AreEqual(3000m, FinancialDiscussionPlan.Calculate(input, state, proposal).SavingMonthly);
        var legacy = input with { Schema = 1 };
        legacy.Answers[legacy.Answers.FindIndex(a => a.Id == "nicotine")] = new("nicotine", FinancialAnswerKind.Known, 12000, Coverage: FinancialCoverage.Partial, ExternalAmount: 4000);
        Assert.AreEqual(2000m, FinancialDiscussionPlan.Calculate(legacy, state, new("nicotine", 6000, FinancialPeriod.Month)).SavingMonthly);
        legacy.Answers[legacy.Answers.FindIndex(a => a.Id == "nicotine")] = new("nicotine", FinancialAnswerKind.Known, 12000, Coverage: FinancialCoverage.External);
        Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Calculate(legacy, state, new("nicotine", 0, FinancialPeriod.Month)));
    }

    [TestMethod]
    public void RejectsIncreaseInvalidPeriodAndMultipleMarkersWithoutRestrictingProse()
    {
        var state = new FinancialDiscussionState();
        foreach (var proposal in new[] { new FinancialSavingProposal("nicotine", 12000, FinancialPeriod.Month),
            new("nicotine", -1, FinancialPeriod.Month), new("nicotine", 1, (FinancialPeriod)99) })
            Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Calculate(Input(), state, proposal));
        var reply = FinancialDiscussionPlan.Parse("Давай обсудим.\n[[saving:nicotine|8000|Month]]");
        Assert.AreEqual("Давай обсудим.", reply.Text); Assert.AreEqual(8000m, reply.Proposal!.NewAmount);
        Assert.IsNull(FinancialDiscussionPlan.Parse("Обычный вопрос, без схемы.").Proposal);
        Assert.IsNull(FinancialDiscussionPlan.Parse("[[saving:nicotine|1|Month]][[saving:phone|2|Month]]").Proposal);
    }

    [TestMethod]
    public void PrivateStateRoundTripsAndSnapshotSeparatesFactsExcerptsAndAge()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-discussion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = Input(); var store = new FinancialRunStore(folder); store.Create(input, new() { Path = "fictional.gguf", Name = "Fictional", Format = "gguf" });
            foreach (var s in FinancialAnalysisPlan.Stages)
                store.SaveStage(new(s.Id, store.Load().Revision, "fake", "Fictional", new string('x', 2000) + " END", 0, DateTimeOffset.UtcNow, FinancialAnalysisPlan.AnalysisVersion));
            var state = FinancialDiscussionPlan.Load(store); state.Draft = "Unsaved user question";
            state.Messages.Add(new("assistant", "Fictional opening")); state.Pending = new("nicotine", 8000, FinancialPeriod.Month);
            FinancialDiscussionPlan.Accept(input, state); store.Write(FinancialDiscussionPlan.FileName, state);
            var loaded = FinancialDiscussionPlan.Load(store); Assert.AreEqual(4000m, loaded.Accepted.Single().SavingMonthly); Assert.AreEqual(state.Draft, loaded.Draft);
            using var snapshot = JsonDocument.Parse(FinancialDiscussionPrompts.Snapshot(store, k => k));
            var data = snapshot.RootElement; Assert.AreEqual(22, data.GetProperty("Profile").GetProperty("Age").GetInt32());
            Assert.IsTrue(data.GetProperty("Profile").GetProperty("ApproximateAge").GetBoolean());
            Assert.HasCount(17, data.GetProperty("Experts").EnumerateArray().ToArray());
            Assert.IsTrue(data.GetProperty("Experts")[0].GetProperty("IsExcerpt").GetBoolean());
            Assert.IsFalse(data.GetProperty("FinalExpert").GetProperty("IsExcerpt").GetBoolean());
            var altered = state with { Revision = "wrong" }; store.Write(FinancialDiscussionPlan.FileName, altered);
            Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Load(store));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void PromptHasMotivationalRoleAgeGroupsAndNoGameInstruction(string language)
    {
        var prompt = FinancialDiscussionPrompts.System(language);
        StringAssert.Contains(prompt, language == "ru" ? "мотивационно-экономическом" : "motivational-economic");
        StringAssert.Contains(prompt, "14"); StringAssert.Contains(prompt, "21"); StringAssert.Contains(prompt, "22");
        Assert.IsFalse(prompt.Contains("игров", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(prompt.Contains("game", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(prompt, "[[saving:");
        var request = FinancialDiscussionPrompts.Conversation("{}", new() { Messages = [new("user", "Leave it")] }, null, language);
        Assert.AreEqual("Leave it", request.Messages[^1].Text);
    }

    [TestMethod]
    public void RestartRemovesOldContextAndDecisionsButPreservesAnalysisAndPrivateArchive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-discussion-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FinancialRunStore(folder);
            store.Create(Input(), new() { Path = "fictional.gguf", Name = "Fictional", Format = "gguf" });
            store.SaveStage(new("final", store.Load().Revision, "fake", "Fictional", "Original final analysis", 0,
                DateTimeOffset.UtcNow, FinancialAnalysisPlan.AnalysisVersion));
            var analysis = File.ReadAllBytes(store.Checked("stages/final.json"));
            var input = store.Load().Input;
            var old = FinancialDiscussionPlan.Load(store);
            old.Messages.Add(new("user", "Unique previous discussion")); old.Draft = "Old draft";
            old.Pending = new("nicotine", 8000, FinancialPeriod.Month); FinancialDiscussionPlan.Accept(input, old);
            FinancialDiscussionPlan.Protect(old, "phone"); old.Pending = new("nicotine", 6000, FinancialPeriod.Month);
            store.Write(FinancialDiscussionPlan.FileName, old);
            var fresh = FinancialDiscussionPlan.Restart(store, old);
            var loaded = FinancialDiscussionPlan.Load(store);
            Assert.AreEqual(old.Revision, loaded.Revision); Assert.HasCount(0, loaded.Messages);
            Assert.HasCount(0, loaded.Accepted); Assert.HasCount(0, loaded.Protected);
            Assert.IsNull(loaded.Pending); Assert.AreEqual("", loaded.Draft);
            CollectionAssert.AreEqual(analysis, File.ReadAllBytes(store.Checked("stages/final.json")));
            Assert.AreEqual(JsonSerializer.Serialize(input), JsonSerializer.Serialize(store.Load().Input));
            var archivedPath = Directory.GetFiles(Path.Combine(folder, "discussion-previous")).Single();
            var archived = store.Read<FinancialDiscussionState>(Path.GetRelativePath(folder, archivedPath));
            Assert.AreEqual(JsonSerializer.Serialize(old), JsonSerializer.Serialize(archived));
            var request = FinancialDiscussionPrompts.Conversation(FinancialDiscussionPrompts.Snapshot(store, k => k), fresh, null, "ru");
            Assert.AreEqual(1, request.Messages.Count);
            Assert.IsFalse(request.System.Contains("Unique previous discussion"));
            Assert.IsFalse(request.System.Contains("Old draft"));
            FinancialDiscussionPlan.Restart(store, fresh);
            Assert.AreEqual(2, Directory.GetFiles(Path.Combine(folder, "discussion-previous")).Length);
            Assert.Throws<InvalidDataException>(() => FinancialDiscussionPlan.Restart(store, fresh with { Revision = "different" }));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    [DataRow("gguf")]
    [DataRow("chatllm")]
    public void LatestRefusalIsARealUserTurnInBackendRequestAndHistoryRemainsIntact(string format)
    {
        var opening = "Ты готов сократить расходы на никотин?";
        var state = new FinancialDiscussionState
        {
            Messages = [new("assistant", opening), new("user", "Пока нет."), new("assistant", opening)]
        };
        var request = FinancialDiscussionPrompts.Conversation("{\"Unit\":\"RUB\"}", state, "Нет", "ru");
        using var body = JsonDocument.Parse(FinancialModelRuntime.RequestJson(format, request.System, request.Messages, 1800));
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "system", "user", "assistant", "user", "assistant", "user" }, messages.Select(m => m.GetProperty("role").GetString()!).ToArray());
        Assert.AreEqual("Пока нет.", messages[3].GetProperty("content").GetString());
        Assert.AreEqual("Нет", messages[^1].GetProperty("content").GetString());
        Assert.IsFalse(messages[0].GetProperty("content").GetString()!.Contains(opening));
        Assert.AreEqual(3, state.Messages.Count); // Building a request never commits an unconfirmed turn.
    }

    [TestMethod]
    public void DiscussionEnablesReasoningAndExplorationWhileExpertRequestsKeepTheirSampling()
    {
        var request = FinancialDiscussionPrompts.Conversation("{}", new(), null, "ru");
        using var dialog = JsonDocument.Parse(FinancialModelRuntime.RequestJson("gguf", request.System, request.Messages, 4096));
        var body = dialog.RootElement;
        Assert.IsTrue(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.AreEqual(0.6, body.GetProperty("temperature").GetDouble());
        Assert.AreEqual(0.95, body.GetProperty("top_p").GetDouble());
        Assert.AreEqual(20, body.GetProperty("top_k").GetInt32());
        using var expert = JsonDocument.Parse(FinancialModelRuntime.RequestJson("gguf", "Expert role /no_think", "Data", 1800));
        Assert.AreEqual(0.15, expert.RootElement.GetProperty("temperature").GetDouble());
        Assert.IsFalse(expert.RootElement.TryGetProperty("top_k", out _));
        var reply = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"<think>Private reasoning</think>Concrete suggestion\"}}]}";
        Assert.AreEqual("Concrete suggestion", FinancialModelRuntime.ParseResponse(reply, 4096));
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task WorkspaceOffersDiscussionOnlyAfterCurrentAnalysisAndIconsHaveNames(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-discussion-ui-" + Guid.NewGuid().ToString("N"));
        var store = new FinancialRunStore(folder); var input = Input() with { Language = language };
        var model = new DebugModelInfo { Name = "Fictional", Path = "fictional.gguf", Format = "gguf" };
        try
        {
            store.Create(input, model); var localize = new LocalizationService(); localize.Load(language);
            var context = new UserContextService(new(), new()); var control = new FinancialScenarioControl();
            control.Configure(localize.T, language, new(), new(), context, [model]); control.Restore(folder);
            Button[] Buttons() => ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().ToArray();
            Assert.IsFalse(Buttons().Any(b => AutomationProperties.GetAutomationId(b) == "Finance.Discuss"));
            foreach (var stage in FinancialAnalysisPlan.Stages)
                store.SaveStage(new(stage.Id, store.Load().Revision, "fake", "Fictional", "Fictional conclusion", 0, DateTimeOffset.UtcNow, FinancialAnalysisPlan.AnalysisVersion));
            control.Restore(folder);
            Assert.AreEqual(localize.T("Finance.Discuss"), Buttons().Single(b => AutomationProperties.GetAutomationId(b) == "Finance.Discuss").Content);
            foreach (var id in new[] { "Analyze", "Pause", "Stop", "Folder", "Save", "Copy", "Edit", "New" })
            {
                var button = Buttons().Single(b => AutomationProperties.GetAutomationId(b) == "Finance." + id);
                Assert.IsInstanceOfType<TextBlock>(button.Content); Assert.AreEqual(localize.T("Finance." + id), button.ToolTip);
                Assert.AreEqual(localize.T("Finance." + id), AutomationProperties.GetName(button));
            }
            var state = FinancialDiscussionPlan.Load(store); state.Pending = new("nicotine", 8000, FinancialPeriod.Month); store.Write(FinancialDiscussionPlan.FileName, state);
            var window = new FinancialDiscussionWindow(store, model, context, localize.T);
            var history = ScenarioNavigationTests.LogicalDescendants(window).OfType<System.Windows.Controls.TextBox>()
                .Single(t => AutomationProperties.GetAutomationId(t) == "Finance.Discussion.History");
            Assert.IsTrue(history.Text.StartsWith(localize.T("Finance.Discussion.Welcome"), StringComparison.Ordinal));
            Assert.AreEqual(0, FinancialDiscussionPlan.Load(store).Messages.Count);
            var buttons = ScenarioNavigationTests.LogicalDescendants(window).OfType<Button>().ToArray();
            var clear = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Finance.Discussion.Clear");
            Assert.AreEqual(localize.T("Finance.Discussion.Clear"), clear.Content);
            Assert.AreEqual(localize.T("Finance.Discussion.ClearHint"), clear.ToolTip);
            Assert.IsTrue(clear.IsEnabled);
            Assert.IsTrue(buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Finance.Discussion.Accept").IsEnabled);
            window.Close(); control.DisposeRuntime();
            Assert.IsNotNull(FinancialDiscussionPlan.Load(store).Pending);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    });
}
