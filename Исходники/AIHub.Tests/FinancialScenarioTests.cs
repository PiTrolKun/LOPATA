using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using DocumentFormat.OpenXml.Packaging;
using AIHub.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class FinancialScenarioTests
{
    private string _folder = "";
    [TestInitialize] public void Setup() => _folder = Path.Combine(Path.GetTempPath(), "LOPATA-finance-test-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = default; if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
    public static FinancialInput CompleteInput() => new() { Unit = "условные", Person = new("FICTIONAL", 30, "TEST LOCATION", "employed"), Answers = FinancialQuestions.All.Select(q => new FinancialAnswer(q.Id, FinancialAnswerKind.Known)).ToList() };
    private static FinancialInput With(params FinancialAnswer[] changes)
    { var input = CompleteInput(); foreach (var a in changes) input.Answers[input.Answers.FindIndex(old => old.Id == a.Id)] = a; return input; }
    private static DebugModelInfo FakeModel(string name = "fake") => new() { Name = name, Path = name + ".gguf", Format = "gguf", IsRunnable = true };
    private FinancialRunStore Store(FinancialInput? input = null) { var store = new FinancialRunStore(Path.Combine(_folder, "private")); store.Create(input ?? CompleteInput(), FakeModel()); return store; }
    [TestMethod]
    public void DecimalPeriodsCoverageBalanceAndCategoryConservation()
    {
        var c = FinancialCalculator.Calculate(With(new("food", FinancialAnswerKind.Known, 100, FinancialPeriod.Day),
            new("housing", FinancialAnswerKind.Known, 1400, FinancialPeriod.Week, FinancialCoverage.Partial, 700),
            new("phone", FinancialAnswerKind.Known, 200, Coverage: FinancialCoverage.External),
            new("stable_income", FinancialAnswerKind.Known, 9000)));
        Assert.AreEqual(9200m, c.Expenses); Assert.AreEqual(3200m, c.External); Assert.AreEqual(6000m, c.Personal); Assert.AreEqual(3000m, c.Balance);
        Assert.AreEqual(c.Expenses, c.Categories.Sum(g => g.Total)); Assert.AreEqual(c.Personal, c.Categories.Sum(g => g.Personal));
        Assert.AreEqual(100m, decimal.Round(c.Categories.Sum(g => g.Share!.Value), 8)); Assert.AreEqual(1.5m, c.StableCoverageRatio);
        Assert.AreEqual(1400m, FinancialCalculator.ForPeriod(FinancialCalculator.Monthly(1400, FinancialPeriod.Week), FinancialPeriod.Week));
    }
    [TestMethod]
    public void UnknownSkippedAndAbsentAreNotZero()
    {
        var input = With(new("food", FinancialAnswerKind.Unknown), new("electricity", FinancialAnswerKind.Skipped));
        input.Answers.RemoveAll(a => a.Id == "housing"); var c = FinancialCalculator.Calculate(input);
        Assert.IsFalse(c.IsComplete); CollectionAssert.AreEquivalent(new[] { "food", "electricity", "housing" }, c.Missing.ToArray());
        Assert.IsTrue(c.Categories.All(g => g.Share is null)); Assert.IsNull(c.StableCoverageRatio);
    }
    [TestMethod]
    public void ZeroExpenseAndZeroIncomeDoNotDivideByZero()
    { var c = FinancialCalculator.Calculate(CompleteInput()); Assert.IsTrue(c.IsComplete); Assert.IsNull(c.StableCoverageRatio); Assert.IsTrue(c.Categories.All(g => g.Share is null)); }
    [TestMethod]
    public void InvalidCoverageNegativeIncomeDuplicateAnswersRejected()
    {
        Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(With(new FinancialAnswer("food", FinancialAnswerKind.Known, 20, Coverage: FinancialCoverage.Partial, ExternalAmount: 21))));
        Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(With(new FinancialAnswer("stable_income", FinancialAnswerKind.Known, -1))));
        Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(With(new FinancialAnswer("stable_income", FinancialAnswerKind.Known, 1, Coverage: FinancialCoverage.External))));
        var input = CompleteInput(); input.Answers.Add(input.Answers[0]); Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(input));
    }
    [TestMethod]
    [DataRow("1 234,56", true)] [DataRow("1234.56", true)] [DataRow("-1", false)] [DataRow("1e6", false)] [DataRow("NaN", false)] [DataRow("100 рублей", false)]
    public void AmountParsingHasNoAmbiguousSeparators(string text, bool valid) => Assert.AreEqual(valid, FinancialCalculator.TryAmount(text, out _));
    [TestMethod]
    public void IncomeQuestionsFollowAllExpensesAndKeysExistInBothLanguages()
    {
        Assert.IsTrue(FinancialQuestions.All.Take(FinancialQuestions.All.Count - 2).All(q => !q.Income));
        foreach (var code in new[] { "ru", "en" })
        {
            var l = new LocalizationService(); l.Load(code);
            foreach (var key in FinancialQuestions.All.Select(q => "Finance.Question." + q.Id).Concat(FinancialAnalysisPlan.Stages.Select(s => "Finance.Stage." + s.Id))) Assert.AreNotEqual(key, l.T(key));
        }
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Finance).ParentId!).ParentId);
        Assert.IsTrue(ScenarioNavigationCatalog.CloudTags.Where(t => t.TargetId == ScenarioNavigationCatalog.Finance).Count() >= 5);
    }
    [TestMethod]
    public async Task FailedStageResumesWithoutRepeatingCompletedAndReportsAreValid()
    {
        var store = Store(With(new("food", FinancialAnswerKind.Known, 100, Coverage: FinancialCoverage.Partial),
            new("housing", FinancialAnswerKind.Known, 0, Coverage: FinancialCoverage.External)) with { Schema = 2 });
        store.WriteText("final.docx", "legacy file retained");
        store.WriteText("simple.md", "legacy file retained");
        var calls = new List<string>(); var count = 0;
        await Assert.ThrowsAsync<IOException>(() => new FinancialAnalysisPipeline((_, _, prompt, _, _) =>
        { calls.Add(prompt); if (++count == 2) throw new IOException(); return Task.FromResult("Первый завершённый ответ"); }).RunAsync(store, null, default));
        Assert.IsNotNull(store.ReadStage("overview")); Assert.IsNull(store.ReadStage("required"));
        store.ChangeModel(FakeModel("replacement")); var remaining = 0;
        await new FinancialAnalysisPipeline((_, _, _, _, _) => { remaining++; return Task.FromResult("Вымышленный тестовый ответ без реальных данных."); }).RunAsync(store, null, default);
        Assert.AreEqual(FinancialAnalysisPlan.Stages.Count - 1, remaining);
        Assert.AreEqual("fake", store.ReadStage("overview")!.ModelName); Assert.AreEqual("replacement", store.ReadStage("final")!.ModelName);
        var finalFile = Directory.GetFiles(store.DirectoryPath, "*_Итоговая_сводка.docx").Single();
        using var doc = WordprocessingDocument.Open(finalFile, false); Assert.IsTrue(doc.MainDocumentPart!.Document!.InnerText.Contains("ИИ-интерпретация"));
        Assert.IsTrue(doc.MainDocumentPart!.Document!.InnerText.Contains("Учитываются только ваши расходы"));
        Assert.IsFalse(doc.MainDocumentPart!.Document!.InnerText.Contains("Внешнее покрытие:"));
        Assert.IsTrue(Directory.GetFiles(store.DirectoryPath, "*_Понятное_объяснение.md").Length == 1); Assert.IsTrue(File.Exists(store.Checked("calculation.json")));
        Assert.AreEqual("legacy file retained", File.ReadAllText(store.Checked("final.docx")));
        Assert.AreEqual("legacy file retained", File.ReadAllText(store.Checked("simple.md")));
        var reports = Directory.GetFiles(store.DirectoryPath).Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_")).Order().ToArray();
        Assert.AreEqual(5, reports.Length);
        FinancialReportExporter.Export(store, "simple", FinancialCalculator.Calculate(store.Load().Input));
        CollectionAssert.AreEqual(reports, Directory.GetFiles(store.DirectoryPath).Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_")).Order().ToArray());
    }
    [TestMethod]
    public async Task CanceledGenerationNeverCommitsAnUnfinishedStage()
    {
        var store = Store(); using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new FinancialAnalysisPipeline((_, _, _, _, _) => { cts.Cancel(); return Task.FromResult("Uncommitted"); }).RunAsync(store, null, cts.Token));
        Assert.IsNull(store.ReadStage("overview"));
    }
    [TestMethod]
    public void FreshSpecialistContextAndFullConclusionsExcludeProfileAndAgeFromProfessional()
    {
        var input = CompleteInput(); var calc = FinancialCalculator.Calculate(input);
        var results = new[] { new FinancialStageResult("overview", "revision", "model", "Model", new string('x', 10_000), 1, DateTimeOffset.UtcNow) };
        var specialist = FinancialAnalysisPipeline.BuildPrompt(FinancialAnalysisPlan.Stages[1], input, calc, results);
        Assert.IsFalse(specialist.Contains(new string('x', 20))); Assert.IsFalse(specialist.Contains("FICTIONAL")); Assert.IsFalse(specialist.Contains("TEST LOCATION"));
        var professional = FinancialAnalysisPipeline.BuildPrompt(FinancialAnalysisPlan.Stages.Single(s => s.Id == "professional"), input, calc, results);
        using var reportData = JsonDocument.Parse(professional[(professional.IndexOf("DATA:\n", StringComparison.Ordinal) + 6)..professional.IndexOf(FinancialAnalysisPrompts.DataEnd, StringComparison.Ordinal)]);
        Assert.AreEqual(results[0].Text, reportData.RootElement.GetProperty("SpecialistConclusions")[0].GetProperty("Conclusion").GetString());
        Assert.IsFalse(professional.Contains("\"Age\""));
        Assert.IsTrue(FinancialAnalysisPipeline.BuildPrompt(FinancialAnalysisPlan.Stages.Single(s => s.Id == "simple"), input, calc, results).Contains("\"Age\":30"));
    }
    [TestMethod]
    public void PrivateCompletionLeavesNoSharedRecordAndCannotLeakThroughNextTaskNotice()
    {
        var path = Path.Combine(_folder, "shared", "operation.json"); var store = new BackgroundOperationStore(path);
        var id = Guid.NewGuid().ToString("N"); var notice = new BackgroundOperationNotice(id, FinancialAnalysisPlan.BackgroundKind, "Private finance", "private-folder", true);
        var state = new BackgroundOperationState { Id = id, Kind = FinancialAnalysisPlan.BackgroundKind, Title = "Private finance", Private = true, Project = "private-folder", Input = JsonSerializer.SerializeToElement(new FinancialWorkReference("private-folder")), Notice = notice, Notices = [notice] };
        store.Save(state); Assert.IsTrue(File.Exists(path)); Assert.IsFalse(File.ReadAllText(path).Contains("Amount"));
        store.Save(state with { Phase = BackgroundOperationPhase.Completed }); Assert.IsFalse(File.Exists(path)); Assert.IsFalse(Directory.Exists(Path.Combine(_folder, "shared", "Results")));
        store.Save(state with { Id = Guid.NewGuid().ToString("N"), Kind = "public", Title = "Public task", Private = false, Project = null, Input = JsonSerializer.SerializeToElement(new { }), Phase = BackgroundOperationPhase.Completed });
        Assert.IsFalse(File.ReadAllText(path).Contains("Private finance")); Assert.IsFalse(File.ReadAllText(path).Contains("private-folder"));
    }
    [TestMethod]
    public void PrivateCompletionPreservesUnreadPublicResultAcrossRestart()
    {
        var path = Path.Combine(_folder, "shared", "operation.json"); var store = new BackgroundOperationStore(path);
        var id = Guid.NewGuid().ToString("N"); var notice = new BackgroundOperationNotice(id, "public", "Public task", "public-folder");
        var state = new BackgroundOperationState { Id = id, Kind = "public", Title = "Public task", Project = "public-folder", Input = JsonSerializer.SerializeToElement(new { }), Phase = BackgroundOperationPhase.Completed, Notice = notice, Notices = [notice], NeedsAttention = true };
        store.Save(state);
        store.Save(state with { Id = Guid.NewGuid().ToString("N"), Kind = FinancialAnalysisPlan.BackgroundKind, Title = "Private finance", Project = "private-folder", Private = true, Input = JsonSerializer.SerializeToElement(new FinancialWorkReference("private-folder")) });
        var loaded = store.Load()!; Assert.AreEqual(id, loaded.Id); Assert.IsTrue(loaded.NeedsAttention);
        Assert.IsFalse(File.ReadAllText(path).Contains("Private finance")); Assert.IsFalse(File.ReadAllText(path).Contains("private-folder"));
    }
    [TestMethod]
    public void ChangedInputAndTraversalCannotSilentlyReuseOldAnalysis()
    {
        var store = Store(); Assert.Throws<InvalidDataException>(() => store.Checked("../escape"));
        var run = store.Load(); store.Write("input.json", run with { Input = run.Input with { Unit = "changed" } });
        Assert.Throws<InvalidDataException>(() => store.Load());
    }
    [TestMethod]
    [DataRow("ru", 1200d, 1d, true)] [DataRow("en", 650d, 1.5d, false)]
    public Task SurveyAndCalculationWorkWithoutAModelAndCanSave(string language, double width, double scale, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var control = new FinancialScenarioControl();
        control.Resources["UiBodyFontSize"] = 14d * scale; control.Resources["UiSectionFontSize"] = 22d * scale;
        control.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black; control.Resources["TextSecondaryBrush"] = Brushes.Gray;
        control.Resources["PanelBrush"] = dark ? Brushes.Black : Brushes.White; control.Resources["InputBrush"] = dark ? Brushes.Black : Brushes.White;
        control.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.Black : Brushes.White; control.Resources["LineBrush"] = Brushes.Gray;
        var localization = new LocalizationService(); localization.Load(language);
        control.Configure(localization.T, language, new StorageSettings { Results = new() { Locations = [new() { Path = _folder }] } }, new UserProfile { DisplayName = "FICTIONAL" }, new UserContextService(new(), new()), []);
        void Click(string id) => ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Finance." + id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click("Begin");
        for (var index = 0; index < FinancialQuestions.All.Count; index++) Click("Next");
        control.Measure(new Size(width, 750)); control.Arrange(new Rect(0, 0, width, 750)); control.UpdateLayout();
        var output = ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBox>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Finance.CalculationOutput");
        Assert.IsTrue(output.Text.Contains(localization.T("Finance.Complete"))); Assert.IsTrue(output.ActualWidth <= width);
        Click("Save"); var file = Directory.EnumerateFiles(_folder, "*.md", SearchOption.AllDirectories).Single(); Assert.IsTrue(File.ReadAllText(file).Contains(localization.T("Finance.Total.Balance")));
        Assert.IsTrue(Path.GetFileName(file).EndsWith("_" + localization.T("Finance.File.calculation") + ".md"));
        Click("Edit"); Assert.IsTrue(File.Exists(file));
        Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Finance.Begin"));
    });
    [TestMethod]
    [DataRow("ru", "Итоговая_сводка")] [DataRow("en", "Final_summary")]
    public void OutputNamesHaveLocalDateTimeAndReadableType(string language, string label)
    {
        var timestamp = new DateTimeOffset(2026, 10, 1, 12, 34, 56, TimeSpan.Zero);
        var name = FinancialReportExporter.OutputFileName("final", language, timestamp, "docx");
        Assert.AreEqual(timestamp.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + "_" + label + ".docx", name);
        Assert.IsFalse(name.Any(c => Path.GetInvalidFileNameChars().Contains(c)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinancialReportExporter.OutputFileName("../invalid", language, timestamp, "docx"));
    }
    [TestMethod]
    public void PersonalAmountsDoNotSubtractOrEstimateOtherPeoplesMoney()
    {
        var input = With(new("food", FinancialAnswerKind.Known, 150, FinancialPeriod.Week, FinancialCoverage.Partial),
            new("housing", FinancialAnswerKind.Known, 0, Coverage: FinancialCoverage.External),
            new("stable_income", FinancialAnswerKind.Known, 1000)) with { Schema = 2 };
        var c = FinancialCalculator.Calculate(input);
        Assert.IsTrue(c.IsComplete); Assert.AreEqual(150m * 30m / 7m, c.Personal); Assert.AreEqual(1000m - c.Personal, c.Balance);
        Assert.AreEqual(0m, c.External); Assert.AreEqual(1m, c.Categories.Single(g => g.Id == "required").Share / 100m);
        foreach (var language in new[] { "ru", "en" })
        {
            var localizedInput = input with { Language = language };
            var l = new LocalizationService(); l.Load(language);
            var report = FinancialReportExporter.CalculationText(localizedInput, c, l.T);
            Assert.IsTrue(report.Contains(l.T("Finance.Coverage.External"))); Assert.IsTrue(report.Contains(l.T("Finance.Coverage.Partial")));
            Assert.IsFalse(report.Contains(l.T("Finance.Total.External") + ":"));
            var prompt = FinancialAnalysisPipeline.BuildPrompt(FinancialAnalysisPlan.Stages[0], localizedInput, c, []);
            using var data = JsonDocument.Parse(prompt[(prompt.IndexOf("DATA:\n", StringComparison.Ordinal) + 6)..prompt.IndexOf(FinancialAnalysisPrompts.DataEnd, StringComparison.Ordinal)]);
            Assert.AreEqual("personal", data.RootElement.GetProperty("AmountBasis").GetString());
            Assert.AreEqual(JsonValueKind.Null, data.RootElement.GetProperty("ExternalAmount").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, data.RootElement.GetProperty("ProgramFacts").GetProperty("BalanceIfExternalCoverageEnds").ValueKind);
            Assert.AreEqual(2, data.RootElement.GetProperty("ExternalPaymentStatuses").GetArrayLength());
        }
        Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(input with { Answers = [new("food", FinancialAnswerKind.Known, 150, Coverage: FinancialCoverage.Partial, ExternalAmount: 10)] }));
        Assert.Throws<InvalidDataException>(() => FinancialCalculator.Calculate(input with { Answers = [new("housing", FinancialAnswerKind.Known, 1, Coverage: FinancialCoverage.External)] }));
    }
    [TestMethod]
    public void LegacyPartialAmountsRemainReadableAndConvertOnlyForEditing()
    {
        var input = With(new("housing", FinancialAnswerKind.Known, 800, Coverage: FinancialCoverage.Partial, ExternalAmount: 300),
            new("phone", FinancialAnswerKind.Known, 100, Coverage: FinancialCoverage.External));
        var store = Store(input); var before = File.ReadAllText(store.Checked("input.json"));
        Assert.AreEqual(500m, FinancialCalculator.Calculate(store.Load().Input).Personal);
        var converted = input with { Schema = 2, Answers = input.Answers.Select(a => FinancialCalculator.ForPersonalInput(a, input.Schema)).ToList() };
        Assert.AreEqual(500m, FinancialCalculator.Calculate(converted).Personal);
        Assert.AreEqual(0m, converted.Answers.Single(a => a.Id == "phone").Amount);
        Assert.IsTrue(converted.Answers.All(a => a.ExternalAmount == 0));
        Assert.AreEqual(before, File.ReadAllText(store.Checked("input.json")));
    }
    [TestMethod]
    [DataRow("ru")] [DataRow("en")]
    public Task SurveyBackPeriodAndPaymentModesPreserveDrafts(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load(language);
        var control = new FinancialScenarioControl();
        control.Configure(l.T, language, new StorageSettings { Results = new() { Locations = [new() { Path = _folder }] } }, new UserProfile { DisplayName = "FICTIONAL" }, new UserContextService(new(), new()), []);
        T Find<T>(string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(control).OfType<T>().Single(v => System.Windows.Automation.AutomationProperties.GetAutomationId(v) == "Finance." + id);
        void Click(string id) => Find<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("FICTIONAL", Find<TextBox>("Name").Text);
        Find<TextBox>("Name").Text = "LOCAL NAME";
        Find<ComboBox>("Period").SelectedIndex = 1; Click("Begin");
        Assert.AreEqual("0", Find<TextBox>("Amount").Text);
        Assert.IsTrue(Find<TextBlock>("Question").Text.Contains("[" + l.T("Finance.PerPeriod.Week") + "]"));
        Assert.AreEqual(1, ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().Count());
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<ComboBox>().Any());
        Find<TextBox>("Amount").Text = "25"; Find<System.Windows.Controls.RadioButton>("Coverage.Partial").IsChecked = true;
        Assert.AreEqual(1, ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBox>().Count());
        Click("Next"); Assert.IsTrue(control.GoBack()); Assert.AreEqual("25", Find<TextBox>("Amount").Text);
        Assert.IsTrue(Find<System.Windows.Controls.RadioButton>("Coverage.Partial").IsChecked);
        Assert.IsTrue(control.GoBack()); Assert.AreEqual(1, Find<ComboBox>("Period").SelectedIndex);
        Assert.AreEqual("LOCAL NAME", Find<TextBox>("Name").Text);
        Click("Begin"); Assert.AreEqual("25", Find<TextBox>("Amount").Text);
        Find<System.Windows.Controls.RadioButton>("Coverage.External").IsChecked = true;
        Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)Find<TextBox>("Amount").Parent).Visibility);
        Assert.IsFalse(Find<System.Windows.Controls.RadioButton>("Coverage.Partial").IsChecked);
        Click("Next"); Find<TextBox>("Amount").Text = ""; Click("Next");
        Assert.IsTrue(Find<TextBlock>("Question").Text.Contains("2 /"));
        Find<TextBox>("Amount").Text = "0";
        for (var index = 1; index < FinancialQuestions.All.Count; index++) Click("Next");
        Click("Save"); var savedPath = Directory.EnumerateFiles(_folder, "input.json", SearchOption.AllDirectories).Single();
        var run = new FinancialRunStore(Path.GetDirectoryName(savedPath)!).Load();
        Assert.AreEqual(2, run.Input.Schema); Assert.IsTrue(run.Input.Answers.All(a => a.Period == FinancialPeriod.Week));
        Assert.AreEqual(FinancialCoverage.External, run.Input.Answers[0].Coverage); Assert.AreEqual(0m, run.Input.Answers[0].Amount);
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(Find<TextBlock>("Question").Text.Contains("53 /"));
        Click("Next"); Click("Edit"); Find<ComboBox>("Period").SelectedIndex = 2; Click("Begin");
        Assert.IsTrue(Find<TextBlock>("Question").Text.Contains("[" + l.T("Finance.PerPeriod.Month") + "]"));
    });
    [TestMethod]
    public void QualityChecksPreventDuplicatedModelArithmeticAndWrongLanguage()
    {
        Directory.CreateDirectory(_folder); var corrupt = Path.Combine(_folder, "corrupt.gguf"); File.WriteAllBytes(corrupt, [0, 0, 0, 0]);
        Assert.IsFalse(FinancialModelDiscovery.IsSupportedText(new DebugModelInfo { Path = corrupt, Format = "gguf", IsRunnable = true }));
        Assert.IsTrue(FinancialReplyQuality.Issues("Ваш расход 350 рублей", "ru").Count > 0);
        Assert.IsTrue(FinancialReplyQuality.Issues("An English conclusion", "ru").Count > 0);
        Assert.AreEqual(0, FinancialReplyQuality.Issues("Обязательные расходы преобладают в структуре.", "ru").Count);
        Assert.AreEqual(0, FinancialReplyQuality.Issues("1. Recurring costs dominate.\n2. Outside funding matters.", "en").Count);
        Assert.AreEqual("Тестовый ответ", FinancialModelRuntime.ParseResponse("{\"choices\":[{\"delta\":{\"content\":\"Тестовый ответ\"}}]}"));
        Assert.Throws<BackgroundOperationWaitingException>(() => FinancialModelRuntime.ParseResponse("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"Оборвано\"}}]}"));
        Assert.Throws<BackgroundOperationWaitingException>(() => FinancialModelRuntime.ParseResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{\"content\":\"Оборвано\"}}],\"usage\":{\"completion_tokens\":650}}", 650));
    }
    [TestMethod]
    public async Task InvalidDraftIsCorrectedOnceAndNeverSilentlyCommitted()
    {
        var store = Store(); var calls = 0;
        await new FinancialAnalysisPipeline((_, _, prompt, _, _) =>
        {
            calls++;
            if (calls == 2) { StringAssert.Contains(prompt, "UNTRUSTED DRAFT"); StringAssert.Contains(prompt, "У вас 100 рублей"); }
            return Task.FromResult(calls == 1 ? "У вас 100 рублей" : "Условное качественное объяснение без чисел.");
        }).RunAsync(store, null, default);
        Assert.AreEqual(FinancialAnalysisPlan.Stages.Count + 1, calls); Assert.IsFalse(store.ReadStage("overview")!.Text.Contains("100"));
        var other = new FinancialRunStore(Path.Combine(_folder, "invalid")); other.Create(CompleteInput(), FakeModel()); var invalidCalls = 0;
        await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => new FinancialAnalysisPipeline((_, _, _, _, _) => { invalidCalls++; return Task.FromResult("Wrong language 123"); }).RunAsync(other, null, default));
        Assert.AreEqual(2, invalidCalls); Assert.IsNull(other.ReadStage("overview"));
    }
    [TestMethod]
    public async Task CommonPauseReleasesModelsAndResumesOnlyUnfinishedFinanceStage()
    {
        var store = Store(); var controller = new BackgroundOperationController(new(Path.Combine(_folder, "background", "operation.json")));
        var state = new BackgroundOperationState { Kind = FinancialAnalysisPlan.BackgroundKind, Title = "Test", Private = true, Input = JsonSerializer.SerializeToElement(new FinancialWorkReference(store.DirectoryPath)) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var retired = false;
        var pipeline = new FinancialAnalysisPipeline(async (_, _, _, _, ct) =>
        { if (Interlocked.Increment(ref calls) == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } return "Вымышленный завершённый этап"; });
        var task = controller.RunAsync(state, async ct => { await pipeline.RunAsync(store, null, ct); return true; }, () => { retired = true; return Task.CompletedTask; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(retired); Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.IsNotNull(store.ReadStage("overview"));
        await controller.ResumeAsync(default); await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(FinancialAnalysisPlan.Stages.Count + 1, calls); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
    }
}
