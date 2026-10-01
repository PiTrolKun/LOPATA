using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class FinancialExpertAnalysisTests
{
    private static FinancialAnalysisStage Stage(string id) => FinancialAnalysisPlan.Stages.Single(s => s.Id == id);
    private static JsonDocument Data(string prompt) => JsonDocument.Parse(prompt[(prompt.IndexOf("DATA:\n", StringComparison.Ordinal) + 6)..prompt.IndexOf(FinancialAnalysisPrompts.DataEnd, StringComparison.Ordinal)]);

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void SeniorReceivesEveryFullSpecialistIncludingLateCaveatsButNoReportDrafts(string language)
    {
        var input = FinancialScenarioTests.CompleteInput() with { Language = language };
        var conclusions = FinancialAnalysisPlan.Stages.Where(s => !s.Report).Select(s =>
            new FinancialStageResult(s.Id, "revision", "test", "Fictional model", new string('x', 450) + " END: " + s.Id + " disputed claim / caveat", 1, DateTimeOffset.UtcNow)).ToList();
        conclusions.Add(new("simple", "revision", "test", "Fictional model", "DO NOT TREAT AN EDITOR AS AN INDEPENDENT ANALYST", 1, DateTimeOffset.UtcNow));
        using var data = Data(FinancialAnalysisPipeline.BuildPrompt(Stage("final"), input, FinancialCalculator.Calculate(input), conclusions));
        var supplied = data.RootElement.GetProperty("SpecialistConclusions").EnumerateArray().ToArray();
        Assert.AreEqual(17, supplied.Length);
        foreach (var result in conclusions.Take(17))
        {
            var source = supplied.Single(s => s.GetProperty("Id").GetString() == result.Id);
            Assert.AreEqual(result.Text, source.GetProperty("Conclusion").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(source.GetProperty("Role").GetString()));
        }
        Assert.AreEqual(0, data.RootElement.GetProperty("UnavailableSpecialists").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, data.RootElement.GetProperty("Presentation").ValueKind);
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void SeniorMandateRequiresPortraitEvidenceReconciliationAndNoForcedProblem(string language)
    {
        var input = FinancialScenarioTests.CompleteInput() with { Language = language };
        var prompt = FinancialAnalysisPipeline.BuildPrompt(Stage("final"), input, FinancialCalculator.Calculate(input), []);
        var system = FinancialAnalysisPipeline.SystemPrompt(Stage("final"), language);
        var expert = Stage("final").Expert;
        StringAssert.Contains(system, expert.Title(language));
        foreach (var term in language == "en" ? new[] { "Behavioral financial portrait", "Personalized recommendations", "EVERY specialist", "no material problem", "concrete item" } :
            new[] { "Поведенческий финансовый портрет", "Персональные рекомендации", "КАЖДОЕ заключение", "существенной проблемы", "конкретную статью" }) StringAssert.Contains(prompt, term);
        Assert.IsTrue(Stage("final").MaxTokens > Stage("professional").MaxTokens);
        var roles = FinancialAnalysisPlan.Stages.Select(s => s.Expert.Title(language)).ToArray();
        Assert.AreEqual(roles.Length, roles.Distinct().Count());
        StringAssert.Contains(system, language == "en" ? "single questionnaire" : "Одна анкета");
    }

    [TestMethod]
    public void MissingIncomeIsNotEvidenceOfAZeroIncomeDeficitAndKnownZerosAreSeparate()
    {
        var input = FinancialScenarioTests.CompleteInput() with { Schema = 2 };
        input.Answers.RemoveAll(a => a.Id == "stable_income");
        input.Answers[input.Answers.FindIndex(a => a.Id == "nicotine")] = new("nicotine", FinancialAnswerKind.Known, 100);
        using var data = Data(FinancialAnalysisPipeline.BuildPrompt(Stage("alignment"), input, FinancialCalculator.Calculate(input), []));
        var facts = data.RootElement.GetProperty("ProgramFacts");
        Assert.AreEqual(JsonValueKind.Null, facts.GetProperty("StableIncomeCoversKnownPersonalExpenses").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, facts.GetProperty("StableIncomeMinusPersonalExpenses").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, facts.GetProperty("TotalIncomeCoversKnownPersonalExpenses").ValueKind);
        Assert.IsTrue(facts.GetProperty("AllStatementsLimitedToKnownData").GetBoolean());
        Assert.IsTrue(data.RootElement.GetProperty("KnownZeroPersonalExpenses").GetArrayLength() > 0);
        Assert.AreEqual("nicotine", data.RootElement.GetProperty("KnownExpenses")[0].GetProperty("Id").GetString());
    }

    [TestMethod]
    public void RussianNamedEvidenceAndFullConclusionsUseReadableTextInsteadOfUnicodeEscapeInflation()
    {
        var input = FinancialScenarioTests.CompleteInput() with { Schema = 2 };
        input.Answers[input.Answers.FindIndex(a => a.Id == "nicotine")] = new("nicotine", FinancialAnswerKind.Known, 100);
        var conclusion = "Конкретная статья и её связь с личной нагрузкой. Ограничение в конце заключения.";
        var results = new[] { new FinancialStageResult("pleasures", "revision", "test", "Тестовая модель", conclusion, 1, DateTimeOffset.UtcNow) };
        var prompt = FinancialAnalysisPipeline.BuildPrompt(Stage("final"), input, FinancialCalculator.Calculate(input), results);
        var localizer = new LocalizationService(); localizer.Load("ru");
        StringAssert.Contains(prompt, localizer.T("Finance.Question.nicotine"));
        StringAssert.Contains(prompt, conclusion);
        Assert.IsFalse(prompt.Contains("\\u"));
        using var data = Data(prompt);
        Assert.AreEqual(conclusion, data.RootElement.GetProperty("SpecialistConclusions")[0].GetProperty("Conclusion").GetString());
    }

    [TestMethod]
    public void BackendReceivesFreshMessagesAndActualThinkingSwitchForProfessionalRoles()
    {
        foreach (var stage in FinancialAnalysisPlan.Stages)
        {
            var system = FinancialAnalysisPipeline.SystemPrompt(stage, "ru");
            using var request = JsonDocument.Parse(FinancialModelRuntime.RequestJson("gguf", system, "Private input", stage.MaxTokens));
            var body = request.RootElement; var messages = body.GetProperty("messages");
            Assert.AreEqual(2, messages.GetArrayLength()); Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
            Assert.AreEqual(system, messages[0].GetProperty("content").GetString());
            Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
            Assert.IsFalse(body.GetProperty("cache_prompt").GetBoolean());
            Assert.AreEqual(stage.Id is "professional" or "final", body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        }
    }

    [TestMethod]
    public async Task EveryRequestHasOwnRoleAndSpecialistsHaveNoOtherAnalystsContext()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LOPATA-finance-role-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = FinancialScenarioTests.CompleteInput();
            var store = new FinancialRunStore(folder); store.Create(input, new() { Name = "Fictional", Path = "fictional.gguf", Format = "gguf" });
            var calls = 0;
            await new FinancialAnalysisPipeline((_, system, prompt, budget, _) =>
            {
                var stage = FinancialAnalysisPlan.Stages[calls++];
                StringAssert.Contains(system, stage.Expert.RussianTitle);
                Assert.AreEqual(stage.MaxTokens, budget);
                using var data = Data(prompt);
                Assert.AreEqual(stage.Report ? 17 : 0, data.RootElement.GetProperty("SpecialistConclusions").GetArrayLength());
                Assert.IsFalse(prompt.Contains("FICTIONAL")); Assert.IsFalse(prompt.Contains("TEST LOCATION"));
                var presentation = data.RootElement.GetProperty("Presentation");
                if (stage.Id is "simple" or "practical") Assert.AreEqual(30, presentation.GetProperty("Age").GetInt32());
                else Assert.AreEqual(JsonValueKind.Null, presentation.ValueKind);
                return Task.FromResult("Вымышленное заключение данной роли. " + stage.Expert.RussianTitle);
            }).RunAsync(store, null, default);
            Assert.AreEqual(21, calls);
            Assert.IsTrue(FinancialAnalysisPlan.Stages.All(s => store.ReadCurrentStage(s.Id) is not null));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task OldContractIsRetainedButReanalyzedAndCurrentCheckpointResumesWithoutRepeating()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LOPATA-finance-upgrade-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FinancialRunStore(folder); store.Create(FinancialScenarioTests.CompleteInput(), new() { Name = "Fictional", Path = "fictional.gguf", Format = "gguf" });
            var revision = store.Load().Revision;
            foreach (var stage in FinancialAnalysisPlan.Stages)
                store.SaveStage(new(stage.Id, revision, "old.gguf", "Old model", "Прежний ответ из старого контракта.", 1, DateTimeOffset.UtcNow));
            var oldBytes = File.ReadAllBytes(store.Checked("stages/overview.json"));
            var inputBytes = File.ReadAllBytes(store.Checked("input.json")); var calculationBytes = File.ReadAllBytes(store.Checked("calculation.json"));
            Assert.IsNotNull(store.ReadStage("final")); Assert.IsNull(store.ReadCurrentStage("final"));
            var calls = 0;
            await Assert.ThrowsAsync<IOException>(() => new FinancialAnalysisPipeline((_, _, _, _, _) =>
            { if (++calls == 2) throw new IOException("Fictional interruption"); return Task.FromResult("Новое заключение после обновления роли."); }).RunAsync(store, null, default));
            Assert.IsNotNull(store.ReadCurrentStage("overview")); Assert.IsNull(store.ReadCurrentStage("required"));
            var archived = Directory.GetFiles(store.Checked("stages/previous"), "overview-*.json").Single();
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(archived));
            var remaining = 0;
            await new FinancialAnalysisPipeline((_, _, _, _, _) => { remaining++; return Task.FromResult("Новое экспертное заключение без чисел."); }).RunAsync(store, null, default);
            Assert.AreEqual(20, remaining); Assert.AreEqual(21, Directory.GetFiles(store.Checked("stages/previous")).Length);
            CollectionAssert.AreEqual(inputBytes, File.ReadAllBytes(store.Checked("input.json")));
            CollectionAssert.AreEqual(calculationBytes, File.ReadAllBytes(store.Checked("calculation.json")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
