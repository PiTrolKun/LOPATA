using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeCreativeTests
{
    [TestMethod]
    public void MissingCreativeFieldsKeepLegacyPreferencesAndCheckpointsInTheNormalMode()
    {
        using var files = new Files();
        var preferences = new MusicRhymePreferences(files.Path("preferences.json"));
        File.WriteAllText(files.Path("preferences.json"), """{"ModelId":"core","AutoAnalyze":false}""");
        Assert.IsFalse(new MusicRhymeOptions().CreativeMode);
        Assert.IsFalse(preferences.Load().CreativeMode);
        var request = Request("Старая песня\nВторая строка");
        var legacy = JsonSerializer.Deserialize<MusicRhymeRequest>(JsonSerializer.Serialize(new {
            request.Id, request.ProjectId, request.Source, request.Start, request.Length,
            request.ModelId, request.ModelPath, request.ModelsRoot, request.InterfaceLanguage
        }))!;
        Assert.IsFalse(legacy.CreativeMode);
        var checkpoint = JsonSerializer.Deserialize<MusicRhymeStep>(JsonSerializer.Serialize(new {
            Request = legacy, Original = legacy.Text, Stage = "Model", ProgramReady = true
        }))!;
        checkpoint.Validate();
        Assert.IsFalse(checkpoint.Request.CreativeMode);
        Assert.AreEqual("", checkpoint.CreativeSummary);
        Assert.AreEqual("", checkpoint.CreativeSummaryPrompt);
    }

    [TestMethod]
    public void CreativeChoiceAndSummaryRoundTripWithoutMutatingAnEarlierRequest()
    {
        using var files = new Files();
        var preferences = new MusicRhymePreferences(files.Path("preferences.json"));
        var options = new MusicRhymeOptions { CreativeMode = true, AutoAnalyze = false };
        preferences.Save(options.Snapshot());
        Assert.IsTrue(preferences.Load().CreativeMode);
        var request = Request("Исходник\nПродолжение") with { CreativeMode = preferences.Load().CreativeMode };
        preferences.Save(options with { CreativeMode = false });
        Assert.IsTrue(request.CreativeMode);
        Assert.AreNotEqual(request, request with { CreativeMode = false });
        var step = new MusicRhymeStep { Request = request, Stage = "Summary",
            CreativeSummary = "Сырой пересказ с ошибкой.", CreativeSummaryPrompt = "Сохранённое задание пересказа." };
        step.Validate();
        var restored = step.Snapshot();
        Assert.AreEqual(step.Request, restored.Request);
        Assert.AreEqual(step.Stage, restored.Stage);
        Assert.IsTrue(restored.Request.CreativeMode);
        Assert.AreEqual(step.CreativeSummary, restored.CreativeSummary);
        Assert.AreEqual(step.CreativeSummaryPrompt, restored.CreativeSummaryPrompt);
    }

    [TestMethod]
    public void SummaryReceivesOnlyTheExactComputationalScopeAndNoEditedColumnsOrReference()
    {
        const string fragment = "[Verse]\r\nКурьер несёт пакет.\r\nОн слушает зимний город.";
        const string prefix = "OUTSIDE_PREFIX_SENTINEL\r\n";
        const string suffix = "\r\nOUTSIDE_SUFFIX_SENTINEL";
        var source = prefix + fragment + suffix;
        var step = Prepared(source) with { Request = Request(source) with {
                Start = prefix.Length, Length = fragment.Length, CreativeMode = true },
            Original = "EDITED_ORIGINAL_SENTINEL", Program = "PROGRAM_SENTINEL", ProgramDraft = "DRAFT_SENTINEL",
            Reply = "HISTORY_REPLY_SENTINEL", Prompt = "HISTORY_PROMPT_SENTINEL",
            Reference = """{"books":[{"source":"BOOK_SENTINEL","text":"REFERENCE_SENTINEL"}]}""" };
        var prompt = MusicRhymeCreativeTask.SummaryPrompt(step);
        StringAssert.Contains(prompt, fragment);
        foreach (var forbidden in new[] { "OUTSIDE_PREFIX_SENTINEL", "OUTSIDE_SUFFIX_SENTINEL", "EDITED_ORIGINAL_SENTINEL",
            "PROGRAM_SENTINEL", "DRAFT_SENTINEL", "HISTORY_REPLY_SENTINEL", "HISTORY_PROMPT_SENTINEL", "REFERENCE_SENTINEL" })
            Assert.IsFalse(prompt.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void CompositionUsesTheRawSummaryAndBoundedBooksWithoutLeakingTheFirstConversation()
    {
        const string source = "SOURCE_ONLY_SENTINEL\nИ ещё одна исходная строка.";
        const string summary = "Курьер хранит истории зимнего города. RAW_SUMMARY_SENTINEL";
        var step = Prepared(source) with { CreativeSummary = summary,
            CreativeSummaryPrompt = "FIRST_CONVERSATION_SENTINEL", Original = "EDITED_ORIGINAL_SENTINEL",
            Program = "PROGRAM_SENTINEL", ProgramDraft = "DRAFT_SENTINEL", Reply = "PREVIOUS_REPLY_SENTINEL",
            Prompt = "PREVIOUS_PROMPT_SENTINEL", Suggestions = [new(1, "CANDIDATE_SENTINEL", "reading", "source", true)],
            Request = Request(source) with { CreativeMode = true, Tuning = Tuning(("RagPromptHits", 1), ("RagPromptChars", 50)) },
            Reference = JsonSerializer.Serialize(new { books = new[] {
                new { source = "BOOK_INCLUDED", text = new string('ж', 100) },
                new { source = "BOOK_OMITTED", text = "SECOND_REFERENCE_SENTINEL" }
            }, lexical = "LEXICAL_SENTINEL" }) };
        var prompt = MusicRhymeCreativeTask.ComposePrompt(step);
        StringAssert.Contains(prompt, summary);
        StringAssert.Contains(prompt, "BOOK_INCLUDED");
        StringAssert.Contains(prompt, new string('ж', 50));
        Assert.IsFalse(prompt.Contains(new string('ж', 51), StringComparison.Ordinal));
        foreach (var forbidden in new[] { "SOURCE_ONLY_SENTINEL", "FIRST_CONVERSATION_SENTINEL", "EDITED_ORIGINAL_SENTINEL",
            "PROGRAM_SENTINEL", "DRAFT_SENTINEL", "PREVIOUS_REPLY_SENTINEL", "PREVIOUS_PROMPT_SENTINEL",
            "CANDIDATE_SENTINEL", "BOOK_OMITTED", "SECOND_REFERENCE_SENTINEL", "LEXICAL_SENTINEL",
            "editorial_targets", "program_candidate", "editable_lines", "preserve_rhyme_pairs" })
            Assert.IsFalse(prompt.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    public void CreativeValidationAllowsANewStructureButRejectsEmptyProseAndOversizedAnswers()
    {
        const string fresh = "[Intro]\nДругая сцена.\n\n[Verse 1]\nКурьер идёт сквозь снег.\nИ слушает весь век.\n\n[Bridge]\nА город отвечает.";
        MusicRhymeCreativeTask.ValidateSong(fresh, new());
        foreach (var invalid in new[] { "", " \r\n\t", "Только одна строка прозы." })
            Assert.Throws<InvalidDataException>(() => MusicRhymeCreativeTask.ValidateSong(invalid, new()));
        Assert.Throws<InvalidDataException>(() => MusicRhymeCreativeTask.ValidateSong(
            "Первая строка\n" + new string('я', 1001), Tuning(("MaxAnswerChars", 1000))));
    }

    [TestMethod]
    public void NativeCreativePayloadsContainExactlyOneFreshUserMessageAndNoEditorSystemOrSchema()
    {
        const string prompt = "Курьер слушает зимний город. РУССКИЙ_ТЕКСТ";
        var tuning = new MusicRhymeTuning();
        var model = new DebugModelInfo { Path = CoreModelManager.CoreModelFileName, Name = CoreModelManager.CoreModelFileName, Format = "gguf" };
        var reasoning = MusicRhymeModelSettings.ReasoningOptions(model, tuning);
        var gguf = MusicRhymeCreativeModel.Payload(prompt, 1234, tuning, (.95, 40, 0, 1), reasoning);
        foreach (var payload in new[] { gguf, FinancialModelRuntime.RequestUserOnlyJson("chatllm", prompt, 1234) })
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement; var messages = root.GetProperty("messages");
            Assert.AreEqual(1, messages.GetArrayLength());
            Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
            Assert.AreEqual(prompt, messages[0].GetProperty("content").GetString());
            Assert.IsFalse(root.TryGetProperty("response_format", out _));
            Assert.IsFalse(root.GetProperty("cache_prompt").GetBoolean());
            Assert.AreEqual(1234, root.GetProperty("max_tokens").GetInt32());
            Assert.IsFalse(payload.Contains(MusicRhymeModel.System, StringComparison.Ordinal));
        }
        Assert.IsFalse(gguf.Contains("\\u", StringComparison.OrdinalIgnoreCase), "Russian input should not be expanded into Unicode escape tokens.");
    }

    [TestMethod]
    public async Task SummaryCheckpointIsRawAndCompletedSummaryIsReusedWithoutOpeningAnotherRuntime()
    {
        var previous = ApplicationBackgroundOperations.Current;
        var exit = ApplicationBackgroundOperations.ExitToken;
        try
        {
            ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            using var files = new Files();
            var state = new ModelState(); var saves = new List<MusicRhymeStep>();
            var initial = Prepared("Исходная песня.\nВторая строка.") with {
                Request = Request("Исходная песня.\nВторая строка.") with { CreativeMode = true } };
            var result = await MusicRhymeCreativeWork.SummarizeAsync(initial, () => new MockModel(state),
                step => { saves.Add(step.Snapshot()); RagBookPreparation.AtomicJson(files.Path("checkpoint.json"), step); return step; },
                Progress(), CancellationToken.None);
            Assert.AreEqual(2, saves.Count); Assert.AreEqual("Summary", saves[0].Stage);
            Assert.AreEqual("", saves[0].CreativeSummary); Assert.AreEqual("Reference", saves[1].Stage);
            Assert.AreEqual(state.Summary, result.CreativeSummary); Assert.AreEqual(1, state.Disposals);
            Assert.IsFalse(result.ModelReady); Assert.AreEqual("", result.Reply);
            var restored = JsonSerializer.Deserialize<MusicRhymeStep>(File.ReadAllText(files.Path("checkpoint.json")))!;
            var reused = await MusicRhymeCreativeWork.SummarizeAsync(restored,
                () => throw new AssertFailedException("A completed summary must not be generated again."),
                _ => throw new AssertFailedException("A completed summary is not a new checkpoint."), Progress(), CancellationToken.None);
            Assert.AreSame(restored, reused);
            Assert.AreEqual(1, state.Calls.Count); Assert.AreEqual(1, state.Disposals);
        }
        finally { ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = exit; }
    }

    [TestMethod]
    public async Task OrdinaryRequestsBypassTheSummaryStageWithoutInvokingAModel()
    {
        var step = Prepared("Старая песня.\nВторая строка.");
        var result = await MusicRhymeCreativeWork.SummarizeAsync(step,
            () => throw new AssertFailedException("Ordinary editing must not create a summary runtime."),
            _ => throw new AssertFailedException("Ordinary editing has no summary checkpoint."), Progress(), CancellationToken.None);
        Assert.AreSame(step, result);
    }

    [TestMethod]
    public async Task AnEmptySummaryCannotAdvanceToCompositionOrBecomeAReadyResult()
    {
        var state = new ModelState { Summary = " \n\t" };
        var step = Prepared("Исходная песня.\nВторая строка.") with {
            Request = Request("Исходная песня.\nВторая строка.") with { CreativeMode = true } };
        var checkpoint = step;
        await Assert.ThrowsAsync<InvalidDataException>(() => MusicRhymeCreativeWork.SummarizeAsync(step,
            () => new MockModel(state), saved => checkpoint = saved, Progress(), CancellationToken.None));
        Assert.AreEqual("Summary", checkpoint.Stage); Assert.AreEqual("", checkpoint.CreativeSummary);
        Assert.IsFalse(checkpoint.ModelReady); Assert.AreEqual("", checkpoint.Reply);
        CollectionAssert.AreEqual(new[] { "summary" }, state.Calls.ToArray()); Assert.AreEqual(1, state.Disposals);
    }

    [TestMethod]
    public async Task ACancelledLateSummaryCannotBeSavedAsCompletedEvenIfTheRunnerReturnsIt()
    {
        using var stop = new CancellationTokenSource();
        var state = new ModelState { BeforeSummary = _ => stop.Cancel() };
        var step = Prepared("Исходная песня.\nВторая строка.") with {
            Request = Request("Исходная песня.\nВторая строка.") with { CreativeMode = true } };
        var checkpoint = step;
        await Assert.ThrowsAsync<OperationCanceledException>(() => MusicRhymeCreativeWork.SummarizeAsync(step,
            () => new MockModel(state), saved => checkpoint = saved, Progress(), stop.Token));
        Assert.AreEqual("Summary", checkpoint.Stage); Assert.AreEqual("", checkpoint.CreativeSummary);
        Assert.IsFalse(checkpoint.ModelReady); Assert.AreEqual("", checkpoint.Reply);
        Assert.AreEqual(1, state.Disposals);
    }

    [TestMethod]
    public Task TogglePersistsWhileAnActiveRequestAndUserDraftStayFrozen() => StaAsync(async () =>
    {
        using var fixture = new ControllerFixture();
        Assert.IsFalse(fixture.Owner.Options.CreativeMode);
        fixture.Owner.SetCreativeMode(true);
        Assert.IsTrue(fixture.Preferences.Load().CreativeMode);
        var entered = Signal(); var release = Signal(); MusicRhymeStep? running = null;
        fixture.Owner.WorkRunner = async (step, save, _, _, _) => {
            running = save(step); entered.TrySetResult(); await release.Task;
            return save(step with { Model = "Новая песня\nДругие строки", ModelReady = true, Stage = "Ready" });
        };
        fixture.Click(); await entered.Task;
        Assert.IsNotNull(running); Assert.IsTrue(running.Request.CreativeMode);
        fixture.Owner.Edit(fixture.Project, running.Request.Id, 2, "Ручной набросок\nСохрани меня");
        fixture.Owner.SetCreativeMode(false);
        Assert.IsFalse(fixture.Preferences.Load().CreativeMode);
        Assert.IsTrue(running.Request.CreativeMode);
        Assert.AreEqual("Ручной набросок\nСохрани меня", fixture.Owner.History.Single().Model);
        release.TrySetResult(); await Until(() => !fixture.Owner.IsRunning(fixture.Project));
        var finished = fixture.Owner.History.Single();
        Assert.IsTrue(finished.Request.CreativeMode);
        Assert.AreEqual("Ручной набросок\nСохрани меня", finished.Model);
        Assert.AreEqual("Курьер несёт пакет.\nОн слушает зимний город.", fixture.View.Editor.Lyrics);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    [TestMethod]
    public Task ChangingModeCreatesANewPreviewRatherThanReusingTheOtherMode() => StaAsync(async () =>
    {
        using var fixture = new ControllerFixture();
        var requests = new List<MusicRhymeRequest>();
        fixture.Owner.ProgramRunner = (step, save, _, _) => {
            requests.Add(step.Request);
            return Task.FromResult(save(step with { ProgramReady = true, Program = step.Request.Text, Stage = "Reference" }));
        };
        // Explicit checks bypass the blur debounce; toggles must still affect the captured input.
        Assert.IsTrue(fixture.Owner.ApplyOptions(fixture.Owner.Options with { AutoAnalyze = true }));
        await fixture.Owner.CheckAfterEditingAsync();
        fixture.Owner.SetCreativeMode(true);
        await fixture.Owner.CheckAfterEditingAsync();
        Assert.AreEqual(2, requests.Count);
        Assert.IsFalse(requests[0].CreativeMode); Assert.IsTrue(requests[1].CreativeMode);
        Assert.AreNotEqual(requests[0].Id, requests[1].Id);
        Assert.AreEqual(0, fixture.ModelLoads);
    });

    [TestMethod, TestCategory("RhymeCpuRuntime")]
    public async Task CreativeWorkRunsTwoDistinctRequestsAndAcceptsNewSongStructureWithoutTargets()
    {
        await WithInstalledCorpus(async () => {
            const string raw = "[Verse]\nНовый герой идёт сквозь снег.\nОн слышит песню за окном.\n\n[Bridge]\nИ отвечает город.\n\nПримечания:\nЭто пояснение не является песней.";
            var state = new ModelState { Reply = raw };
            var saved = new List<MusicRhymeStep>();
            var initial = Prepared("[Verse]\nТихий город.") with { Request = Request("[Verse]\nТихий город.") with { CreativeMode = true } };
            Assert.AreEqual(0, MusicRhymeTask.EditableLines(initial).Count);
            var result = await new MusicRhymeWork(() => new MockModel(state)).RunAsync(initial,
                step => { saved.Add(step.Snapshot()); return step; }, Progress(), CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "summary", "compose" }, state.Calls.ToArray());
            Assert.AreEqual(0, state.RewriteCalls); Assert.AreEqual(2, state.Disposals);
            Assert.AreEqual(state.Summary, result.CreativeSummary);
            Assert.IsTrue(saved.Any(step => step.CreativeSummary == state.Summary && !step.ModelReady));
            Assert.AreEqual(raw, result.Reply, "The complete raw answer must remain available for diagnostics.");
            StringAssert.Contains(result.Model, "[Bridge]");
            Assert.IsFalse(result.Model.Contains("Это пояснение", StringComparison.Ordinal));
            Assert.IsTrue(result.Stage == "Ready" || result.Error == "Music.Rhyme.Unresolved", result.Error);
            Assert.IsTrue(result.ModelReady); Assert.IsFalse(result.ModelSkipped);
            StringAssert.Contains(state.SummaryPrompts.Single(), initial.Request.Text);
            StringAssert.Contains(state.ComposePrompts.Single(), state.Summary);
            Assert.IsFalse(state.ComposePrompts.Single().Contains(initial.Request.Text, StringComparison.Ordinal));
        });
    }

    [TestMethod, TestCategory("RhymeCpuRuntime")]
    public async Task NormalEditingStillUsesOrdinaryRewriteWithoutEitherCreativeStage()
    {
        await WithInstalledCorpus(async () => {
            const string source = "Кот\nСад";
            var initial = Prepared(source) with { Analysis = MusicRhymeAnalyzer.Analyze(source, _ => []) };
            Assert.IsTrue(MusicRhymeTask.EditableLines(initial).Count > 0);
            var state = new ModelState { OrdinaryReply = "Кот\nСвод" };
            var result = await new MusicRhymeWork(() => new MockModel(state)).RunAsync(initial,
                step => step, Progress(), CancellationToken.None);
            Assert.AreEqual(0, state.Calls.Count); Assert.AreEqual(1, state.RewriteCalls);
            Assert.IsTrue(result.ModelReady); Assert.IsFalse(result.ModelSkipped);
            Assert.AreEqual("Кот\nСвод", result.Model); Assert.AreEqual(1, state.Disposals);
        });
    }

    [TestMethod, TestCategory("RhymeCpuRuntime")]
    public async Task CancelledCompositionKeepsTheSummaryAndResumeDoesNotRepeatIt()
    {
        await WithInstalledCorpus(async () => {
            using var stop = new CancellationTokenSource();
            var state = new ModelState { BeforeCompose = token => { stop.Cancel(); token.ThrowIfCancellationRequested(); } };
            var initial = Prepared("Первая строка.\nВторая строка.") with {
                Request = Request("Первая строка.\nВторая строка.") with { CreativeMode = true } };
            var checkpoint = initial;
            var work = new MusicRhymeWork(() => new MockModel(state));
            await Assert.ThrowsAsync<OperationCanceledException>(() => work.RunAsync(initial,
                step => checkpoint = step.Snapshot(), Progress(), stop.Token));
            Assert.AreEqual(state.Summary, checkpoint.CreativeSummary);
            Assert.IsFalse(checkpoint.ModelReady); Assert.AreEqual("", checkpoint.Model);
            Assert.AreEqual("", checkpoint.Reply);
            Assert.AreEqual(1, state.Calls.Count(stage => stage == "summary"));
            Assert.AreEqual(2, state.Disposals);
            state.BeforeCompose = null;
            var result = await work.RunAsync(checkpoint, step => step, Progress(), CancellationToken.None);
            Assert.IsTrue(result.ModelReady);
            Assert.IsTrue(result.Stage == "Ready" || result.Error == "Music.Rhyme.Unresolved", result.Error);
            Assert.AreEqual(1, state.Calls.Count(stage => stage == "summary"));
            Assert.AreEqual(2, state.Calls.Count(stage => stage == "compose"));
            Assert.AreEqual(3, state.Disposals);
        });
    }

    [TestMethod, TestCategory("RhymeCpuRuntime")]
    public async Task InvalidCompositionRemainsARawDraftAndIsNeverMarkedReady()
    {
        await WithInstalledCorpus(async () => {
            const string raw = "Это однострочная проза, а не готовая песня.";
            var state = new ModelState { Reply = raw };
            var initial = Prepared("Исходная строка.\nВторая строка.") with {
                Request = Request("Исходная строка.\nВторая строка.") with { CreativeMode = true } };
            var result = await new MusicRhymeWork(() => new MockModel(state)).RunAsync(initial,
                step => step, Progress(), CancellationToken.None);
            Assert.AreEqual("Partial", result.Stage); Assert.IsFalse(result.ModelReady);
            Assert.IsNotNull(result.Error); Assert.AreEqual(raw, result.Reply);
            Assert.AreEqual(state.Summary, result.CreativeSummary);
            Assert.AreEqual(2, state.Disposals);
        });
    }

    private static MusicRhymeRequest Request(string text) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        text, 0, text.Length, "core", "Qwen3-8B-Q4_K_M.gguf", "", "ru");
    private static MusicRhymeStep Prepared(string text) => new() { Request = Request(text), Original = text,
        Program = text, ProgramDraft = text, ProgramReady = true, Stage = "Reference", Reference = "{}",
        Analysis = new([], []) { SelectionVersion = MusicRhymeSelection.Version } };
    private static MusicRhymeTuning Tuning(params (string Id, double Value)[] values) => new() {
        Values = values.ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal) };
    private static IProgress<RagBookProgress> Progress() => new InlineProgress<RagBookProgress>(_ => { });
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WithInstalledCorpus(Func<Task> test)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_CPU_RUNTIME") != "1")
        { Assert.Inconclusive("Opt-in installed corpus; model calls are mocked and no downloads are allowed."); return; }
        if (!File.Exists(Path.Combine(MusicRhymeCorpus.DirectoryPath, "corpus.json")))
        { Assert.Inconclusive("The installed corpus is required; this test never installs it."); return; }
        var previous = ApplicationBackgroundOperations.Current;
        var exit = ApplicationBackgroundOperations.ExitToken;
        try
        {
            ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            await MusicRhymeCorpus.VerifyInstalledAsync(Progress(), CancellationToken.None);
            await test();
        }
        finally { ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = exit; }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static Task StaAsync(Func<Task> action) => ScenarioNavigationTests.Sta(() => {
        var dispatcher = Dispatcher.CurrentDispatcher; var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action(); var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });
    private sealed class ModelState
    {
        public List<string> Calls { get; } = [];
        public List<string> SummaryPrompts { get; } = [];
        public List<string> ComposePrompts { get; } = [];
        public string Summary { get; init; } = "Курьер идёт зимой и собирает городские истории. Сырой пересказ.";
        public string Reply { get; init; } = "[Verse]\nЗимою слышен каждый шаг.\nИ город отвечает так.";
        public string? OrdinaryReply { get; init; }
        public Action<CancellationToken>? BeforeSummary { get; init; }
        public Action<CancellationToken>? BeforeCompose { get; set; }
        public int RewriteCalls, Disposals;
    }
    private sealed class MockModel(ModelState state) : IMusicRhymeModel
    {
        public Task<string> RewriteAsync(DebugModelInfo model, string prompt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); state.RewriteCalls++;
            return Task.FromResult(state.OrdinaryReply
                ?? throw new AssertFailedException("Creative work must not route through the editor model task."));
        }
        public Task<string> SummarizeAsync(DebugModelInfo model, string prompt, MusicRhymeTuning tuning, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); state.Calls.Add("summary"); state.SummaryPrompts.Add(prompt);
            state.BeforeSummary?.Invoke(token); return Task.FromResult(state.Summary);
        }
        public Task<string> ComposeAsync(DebugModelInfo model, string prompt, MusicRhymeTuning tuning, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); state.Calls.Add("compose"); state.ComposePrompts.Add(prompt);
            state.BeforeCompose?.Invoke(token); return Task.FromResult(state.Reply);
        }
        public void Dispose() => state.Disposals++;
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lopata-rhyme-creative-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(Root);
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Root);
            if (!full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(full).StartsWith("lopata-rhyme-creative-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    }
    private sealed class ControllerFixture : IDisposable
    {
        private readonly Files _files = new();
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        private readonly CancellationToken _exit = ApplicationBackgroundOperations.ExitToken;
        public MusicWorkspaceControl View { get; }
        public MusicRhymeController Owner { get; }
        public MusicRhymePreferences Preferences { get; }
        public string Project => View.Projects.Current.Id;
        public int ModelLoads { get; private set; }
        public ControllerFixture()
        {
            ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            View = new(new MusicProjects(_files.Path("projects")), new MusicGenerationJobs(_files.Path("jobs")));
            View.Rhyme.Dispose(); Preferences = new(_files.Path("preferences.json")); Preferences.Save(new() { AutoAnalyze = false });
            Owner = new(View, Preferences); Owner.Localize(key => key == "Music.Rhyme.Language" ? "ru" : key);
            Owner.ProgramAvailable = () => true; View.Editor.Lyrics = "Курьер несёт пакет.\nОн слушает зимний город.";
            WriteCore(_files.Path(CoreModelManager.CoreModelFileName));
            Owner.Configure(new() { Models = new() { Locations = [new() { Path = _files.Root }] } },
                () => { ModelLoads++; throw new AssertFailedException("A controller test must not load a model."); });
        }
        public void Click() => View.TextActions.RhymeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static void WriteCore(string path)
        {
            using var file = File.Create(path); using var writer = new BinaryWriter(file, Encoding.UTF8);
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(3ul);
            Text("general.architecture"); writer.Write(8u); Text("qwen3");
            Text("qwen3.block_count"); writer.Write(4u); writer.Write(32u);
            Text("qwen3.context_length"); writer.Write(4u); writer.Write(32768u);
            void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        }
        public void Dispose()
        {
            Owner.Dispose(); View.Dispose(); ApplicationBackgroundOperations.Current = _previous;
            ApplicationBackgroundOperations.ExitToken = _exit; _files.Dispose();
        }
    }
}
