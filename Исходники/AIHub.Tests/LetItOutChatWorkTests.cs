using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutChatWorkTests
{
    private string _folder = null!;
    private BackgroundOperationController? _previousController;
    private CancellationToken _previousExit;
    private BackgroundOperationStore Store => new(Path.Combine(_folder, "operation.json"));

    [TestInitialize]
    public void Initialize()
    {
        _folder = Path.Combine(Path.GetTempPath(), "lopata-let-it-out-" + Guid.NewGuid().ToString("N"));
        _previousController = ApplicationBackgroundOperations.Current;
        _previousExit = ApplicationBackgroundOperations.ExitToken;
        ApplicationBackgroundOperations.Current = new(Store);
        ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
    }

    [TestCleanup]
    public void Cleanup()
    {
        ApplicationBackgroundOperations.Current = _previousController;
        ApplicationBackgroundOperations.ExitToken = _previousExit;
        var path = Path.GetFullPath(_folder);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-let-it-out-", StringComparison.Ordinal))
            throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static LetItOutChatRequest Request() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        Path.Combine(Path.GetTempPath(), "Core", LetItOutModel.FileName),
        [new("user", "Мне трудно."), new("assistant", "Хочешь рассказать?"), new("user", "Сегодня опять поссорились.")], "ru");
    private static LetItOutChatCheckpoint Checkpoint(BackgroundOperationState state) =>
        state.Checkpoint!.Value.Deserialize<LetItOutChatCheckpoint>()!;
    private static BackgroundOperationState State(LetItOutChatRequest request, LetItOutChatCheckpoint checkpoint) => new()
    { Id = request.TurnId, Kind = LetItOutChatWork.Kind, Title = "Если накипело", Project = request.SessionId,
        Input = JsonSerializer.SerializeToElement(request), Checkpoint = JsonSerializer.SerializeToElement(checkpoint), Private = true };

    [TestMethod]
    public async Task LegacyUnfinishedCoreTurnReleasesGateButPreservesTheVisibleHistory()
    {
        var request = Request() with { CoreModelPath = Path.Combine(Path.GetTempPath(), CoreModelManager.CoreModelFileName) };
        var runtime = new FakeRuntime((_, _, _) => throw new LetItOutModelChangedException());
        var work = new LetItOutChatWork(runtime);
        var error = await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => work.GenerateAsync(request,
            new InlineProgress<string>(_ => { }), CancellationToken.None));
        Assert.AreEqual("LetItOut.ModelChanged", error.Message);
        Assert.IsFalse(ComponentAuditFailures.IsEligible(error));
        Assert.IsFalse(ApplicationBackgroundOperations.Current!.BlocksNewWork);
        CollectionAssert.AreEqual(request.Messages.ToArray(), Checkpoint(ApplicationBackgroundOperations.Current.State!).Request.Messages.ToArray());
        Assert.AreEqual(1, runtime.Retirements);
    }

    [TestMethod]
    public async Task MemoryPressureKeepsAResumableTurnAndAReadableReason()
    {
        var request = Request();
        var runtime = new FakeRuntime((_, progress, _) => {
            progress.Report(new("Начало ответа")); throw new LetItOutMemoryException("LetItOut.VramChanged");
        });
        var error = await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => new LetItOutChatWork(runtime)
            .GenerateAsync(request, new InlineProgress<string>(_ => { }), CancellationToken.None));
        Assert.AreEqual("LetItOut.VramChanged", error.Message);
        var state = ApplicationBackgroundOperations.Current!.State!;
        Assert.AreEqual(BackgroundOperationPhase.Waiting, state.Phase);
        Assert.AreEqual("LetItOut.VramChanged", state.Detail);
        Assert.AreEqual("Начало ответа", Checkpoint(state).Partial);
        Assert.IsTrue(ApplicationBackgroundOperations.Current.BlocksNewWork);
        Assert.IsFalse(ComponentAuditFailures.IsEligible(error));
        Assert.AreEqual(1, runtime.Retirements);
    }

    [TestMethod]
    public async Task MultiTurnUsesAnImmutableSnapshotAndLeavesNoCompletedPrivateArchive()
    {
        var request = Request();
        var mutable = request.Messages.ToList(); request = request with { Messages = mutable };
        var snapshots = new List<string>();
        var runtime = new FakeRuntime((received, progress, _) => {
            mutable.Add(new("assistant", "A later UI mutation"));
            Assert.HasCount(3, received.Messages);
            CollectionAssert.AreEqual(new[] { "user", "assistant", "user" }, received.Messages.Select(message => message.Role).ToArray());
            progress.Report(new("Слышу, ")); progress.Report(new("это задело."));
            return Task.FromResult("Слышу, это задело.");
        });
        var result = await new LetItOutChatWork(runtime).GenerateAsync(request, new InlineProgress<string>(snapshots.Add), CancellationToken.None);
        Assert.AreEqual("Слышу, это задело.", result);
        Assert.AreEqual("", snapshots[0]); CollectionAssert.Contains(snapshots, "Слышу, ");
        var state = ApplicationBackgroundOperations.Current!.State!;
        Assert.IsTrue(state.Private); Assert.AreEqual(BackgroundOperationPhase.Completed, state.Phase);
        var checkpoint = Checkpoint(state);
        Assert.IsTrue(checkpoint.Completed); Assert.AreEqual(result, checkpoint.Result); Assert.AreEqual("", checkpoint.Partial);
        Assert.HasCount(3, checkpoint.Request.Messages); Assert.AreEqual(0, runtime.Retirements);
        Assert.IsNull(Store.Load()); Assert.IsNull(Store.LoadResult(request.TurnId));
        Assert.IsFalse(Directory.EnumerateFiles(_folder, "*.json", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task PauseKeepsPartialAndRepeatsOnlyTheUnfinishedAssistant()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<LetItOutChatRequest>();
        var snapshots = new List<string>();
        var runtime = new FakeRuntime(async (request, progress, token) => {
            attempts.Add(request);
            if (attempts.Count == 1) {
                progress.Report(new("Незаконченная фраза")); started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            progress.Report(new("Готовый ответ")); return "Готовый ответ";
        });
        var request = Request();
        var work = new LetItOutChatWork(runtime).GenerateAsync(request, new InlineProgress<string>(snapshots.Add), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var controller = ApplicationBackgroundOperations.Current!;
        var pausedPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () => {
            if (controller.State?.Phase == BackgroundOperationPhase.Paused) pausedPublished.TrySetResult();
        };
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Pause commits the canonical job before completing its waiter. Its Changed notification
        // updates operation.json afterward, so reading that mirror immediately would race the notification.
        var saved = Checkpoint(Store.Operation(request.TurnId).Load()!);
        Assert.AreEqual("Незаконченная фраза", saved.Partial); Assert.IsFalse(saved.Completed); Assert.IsNull(saved.Result);
        Assert.AreEqual(saved.Partial, Checkpoint(controller.State!).Partial);
        await pausedPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(saved.Partial, Checkpoint(Store.Load()!).Partial);
        Assert.AreEqual(1, runtime.Retirements);
        await controller.ResumeAsync(CancellationToken.None);
        Assert.AreEqual("Готовый ответ", await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.HasCount(2, attempts);
        CollectionAssert.AreEqual(attempts[0].Messages.ToArray(), attempts[1].Messages.ToArray());
        Assert.AreEqual(2, snapshots.Count(text => text.Length == 0)); Assert.AreEqual(1, runtime.Retirements);
    }

    [TestMethod]
    public async Task ExitAndColdRestartKeepInputButDoNotFeedTheOldPartialBackToTheModel()
    {
        using var exit = new CancellationTokenSource(); ApplicationBackgroundOperations.ExitToken = exit.Token;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeRuntime(async (_, progress, token) => {
            progress.Report(new("OLD PARTIAL SECRET")); started.SetResult();
            await Task.Delay(Timeout.Infinite, token); return "never";
        });
        var request = Request();
        var pending = new LetItOutChatWork(first).GenerateAsync(request, new InlineProgress<string>(_ => { }), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); exit.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, Store.Load()!.Phase);
        ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
        var controller = new BackgroundOperationController(Store); controller.Load(); ApplicationBackgroundOperations.Current = controller;
        var restored = controller.State!; Assert.AreEqual("OLD PARTIAL SECRET", Checkpoint(restored).Partial);
        var second = new FakeRuntime((received, _, _) => {
            Assert.HasCount(3, received.Messages);
            Assert.IsFalse(received.Messages.Any(message => message.Content.Contains("OLD PARTIAL SECRET", StringComparison.Ordinal)));
            Assert.AreEqual(request.TurnId, received.TurnId); return Task.FromResult("Заново завершённый ответ");
        });
        Assert.AreEqual("Заново завершённый ответ", await new LetItOutChatWork(second).GenerateAsync(
            request, new InlineProgress<string>(_ => { }), CancellationToken.None, restored));
        Assert.AreEqual(1, second.Calls); Assert.IsNull(Store.Load());
    }

    [TestMethod]
    public async Task UserCancellationRetiresRuntimeAndDiscardsThePrivateCheckpoint()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(async (_, progress, token) => {
            progress.Report(new("partial")); started.SetResult(); await Task.Delay(Timeout.Infinite, token); return "never";
        });
        var task = new LetItOutChatWork(runtime).GenerateAsync(Request(), new InlineProgress<string>(_ => { }), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.AreEqual(1, runtime.Retirements); Assert.AreEqual(BackgroundOperationPhase.Canceled, ApplicationBackgroundOperations.Current!.State!.Phase);
        Assert.IsFalse(Checkpoint(ApplicationBackgroundOperations.Current.State).Completed); Assert.IsNull(Store.Load());
    }

    [TestMethod]
    public async Task CompletedCheckpointIsReusedWithoutAnotherModelCall()
    {
        var request = Request(); var restored = State(request, new(request, "", "Уже готово", true));
        var runtime = new FakeRuntime((_, _, _) => throw new AssertFailedException("A completed turn was generated twice."));
        Assert.AreEqual("Уже готово", await new LetItOutChatWork(runtime).GenerateAsync(request,
            new InlineProgress<string>(_ => { }), CancellationToken.None, restored));
        Assert.AreEqual(0, runtime.Calls); Assert.AreEqual(0, runtime.Retirements);
    }

    [TestMethod]
    public async Task TruncatedSsePreservesVisiblePartialWithoutCompletingTheTurn()
    {
        var runtime = new FakeRuntime(async (_, progress, token) => {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"часть\"}}]}\n\n"));
            return LetItOutChatRuntime.ValidateResult(await OpenAiSseStreamParser.ReadAsync(stream, progress, token));
        });
        await Assert.ThrowsAsync<IOException>(async () => await new LetItOutChatWork(runtime).GenerateAsync(
            Request(), new InlineProgress<string>(_ => { }), CancellationToken.None));
        var state = Store.Load()!; var checkpoint = Checkpoint(state);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, state.Phase); Assert.IsTrue(state.RequiresDecision);
        Assert.IsFalse(checkpoint.Completed); Assert.IsNull(checkpoint.Result); Assert.AreEqual("часть", checkpoint.Partial);
        Assert.AreEqual(1, runtime.Retirements);
    }

    [TestMethod]
    public void TokenLimitAndEmptyOrToolResponsesAreNotCompletedAnswers()
    {
        Assert.Throws<LetItOutChatIncompleteException>(() => LetItOutChatRuntime.ValidateResult(new() { Content = "cut", FinishReason = "length" }));
        Assert.Throws<LetItOutChatIncompleteException>(() => LetItOutChatRuntime.ValidateResult(new() { Content = "", FinishReason = "stop" }));
        Assert.Throws<LetItOutChatIncompleteException>(() => LetItOutChatRuntime.ValidateResult(new() { Content = "tool", FinishReason = "tool_calls" }));
        Assert.AreEqual("done", LetItOutChatRuntime.ValidateResult(new() { Content = " done ", FinishReason = "stop" }));
    }

    [TestMethod]
    public void PayloadPreservesRolesAndDisablesThinkingWithoutToolsOrPersonalContext()
    {
        var request = Request(); using var payload = JsonDocument.Parse(LetItOutChatRuntime.BuildPayload(request));
        var root = payload.RootElement; var messages = root.GetProperty("messages");
        Assert.AreEqual(request.Messages.Count + 1, messages.GetArrayLength()); Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        for (var index = 0; index < request.Messages.Count; index++) {
            Assert.AreEqual(request.Messages[index].Role, messages[index + 1].GetProperty("role").GetString());
            Assert.AreEqual(request.Messages[index].Content, messages[index + 1].GetProperty("content").GetString());
        }
        Assert.IsFalse(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.AreEqual(1536, root.GetProperty("max_tokens").GetInt32()); Assert.IsFalse(root.TryGetProperty("tools", out _));
    }

    [TestMethod]
    public void RuntimeNeverFallsBackToAnArbitraryModelPath()
    {
        var path = Request().CoreModelPath; LetItOutChatRuntime.ValidateCorePath(path, path);
        Assert.Throws<InvalidOperationException>(() => LetItOutChatRuntime.ValidateCorePath(path, null));
        Assert.Throws<InvalidOperationException>(() => LetItOutChatRuntime.ValidateCorePath(path, Path.Combine(Path.GetTempPath(), "other", LetItOutModel.FileName)));
        var arbitrary = Path.Combine(Path.GetTempPath(), "arbitrary.gguf");
        Assert.Throws<InvalidOperationException>(() => LetItOutChatRuntime.ValidateCorePath(arbitrary, arbitrary));
    }

    [TestMethod]
    public async Task MismatchedCheckpointCannotReplaceThePendingUserInput()
    {
        var request = Request(); var other = request with { Messages = [new("user", "foreign text")] };
        var runtime = new FakeRuntime((_, _, _) => throw new AssertFailedException("Invalid checkpoint reached model."));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new LetItOutChatWork(runtime).GenerateAsync(
            request, new InlineProgress<string>(_ => { }), CancellationToken.None, State(request, new(other, "", null, false))));
        Assert.AreEqual(0, runtime.Calls);
    }

    [TestMethod]
    public async Task IsolatedWorkAlsoRunsWithoutTheDesktopController()
    {
        ApplicationBackgroundOperations.Current = null;
        var runtime = new FakeRuntime((_, _, _) => Task.FromResult("готово"));
        Assert.AreEqual("готово", await new LetItOutChatWork(runtime).GenerateAsync(Request(), new InlineProgress<string>(_ => { }), CancellationToken.None));
        Assert.AreEqual(0, runtime.Retirements);
    }

    [TestMethod]
    public async Task MessageAfterACanceledAnswerKeepsBothUserMessagesWithoutThePartial()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeRuntime(async (_, progress, token) => {
            progress.Report(new("discard this partial")); started.SetResult();
            await Task.Delay(Timeout.Infinite, token); return "never";
        });
        var request = Request();
        var pending = new LetItOutChatWork(first).GenerateAsync(request, new InlineProgress<string>(_ => { }), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        var next = request with { TurnId = Guid.NewGuid().ToString("N"),
            Messages = request.Messages.Append(new LetItOutChatMessage("user", "Хочу добавить ещё кое-что.")).ToArray() };
        var second = new FakeRuntime((received, _, _) => {
            Assert.HasCount(4, received.Messages);
            Assert.AreEqual(request.Messages[^1], received.Messages[^2]); Assert.AreEqual(next.Messages[^1], received.Messages[^1]);
            using var payload = JsonDocument.Parse(LetItOutChatRuntime.BuildPayload(received));
            Assert.AreEqual("user", payload.RootElement.GetProperty("messages")[3].GetProperty("role").GetString());
            Assert.AreEqual("user", payload.RootElement.GetProperty("messages")[4].GetProperty("role").GetString());
            Assert.IsFalse(received.Messages.Any(message => message.Content == "discard this partial"));
            return Task.FromResult("Слушаю.");
        });
        Assert.AreEqual("Слушаю.", await new LetItOutChatWork(second).GenerateAsync(next, new InlineProgress<string>(_ => { }), CancellationToken.None));
    }

    [TestMethod]
    public async Task FullContextIsTerminalAndAllowsANewConversation()
    {
        var runtime = new FakeRuntime((_, _, _) => throw new LetItOutChatContextException());
        var error = await Assert.ThrowsAsync<BackgroundOperationFailedException>(async () =>
            await new LetItOutChatWork(runtime).GenerateAsync(Request(), new InlineProgress<string>(_ => { }), CancellationToken.None));
        Assert.AreEqual("LetItOut.ContextFull", error.Message);
        var controller = ApplicationBackgroundOperations.Current!;
        Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
        Assert.IsFalse(Checkpoint(controller.State).Completed); Assert.IsFalse(controller.BlocksNewWork);
        Assert.AreEqual(1, runtime.Retirements); Assert.IsNull(Store.Load());
        var fresh = new FakeRuntime((_, _, _) => Task.FromResult("Новый разговор"));
        Assert.AreEqual("Новый разговор", await new LetItOutChatWork(fresh).GenerateAsync(Request(), new InlineProgress<string>(_ => { }), CancellationToken.None));
    }

    [TestMethod]
    public async Task EmergencyStopDoesNotTurnThePartialIntoACompletedReply()
    {
        using var registry = new OwnedProcessRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(async (_, progress, token) => {
            progress.Report(new("partial before STOP")); started.SetResult();
            await Task.Delay(Timeout.Infinite, token); return "never";
        });
        var pending = new LetItOutChatWork(runtime).GenerateAsync(Request(), new InlineProgress<string>(_ => { }), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var controller = ApplicationBackgroundOperations.Current!;
        await BackgroundEmergencyStop.ExecuteAsync(controller, registry,
            BackgroundEmergencyStop.Capture(controller, registry), () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(BackgroundOperationPhase.Canceled, controller.State!.Phase);
        var checkpoint = Checkpoint(controller.State);
        Assert.IsFalse(checkpoint.Completed); Assert.IsNull(checkpoint.Result); Assert.AreEqual("partial before STOP", checkpoint.Partial);
        Assert.IsFalse(controller.BlocksNewWork); Assert.AreEqual(1, runtime.Retirements); Assert.IsNull(Store.Load());
    }

    [TestMethod, DataRow(1), DataRow(2), DataRow(7), DataRow(100)]
    public async Task StreamedThoughtTagsNeverReachVisibleSnapshotsOrCompletedHistory(int chunkSize)
    {
        const string raw = "  <think>PRIVATE THOUGHT</think>\n\nПривет. Я слушаю.";
        var snapshots = new List<string>();
        var runtime = new FakeRuntime((_, progress, _) => {
            var answer = LetItOutChatRuntime.CreateAnswerProgress(progress);
            for (var offset = 0; offset < raw.Length; offset += chunkSize)
                answer.Report(new(raw.Substring(offset, Math.Min(chunkSize, raw.Length - offset))));
            answer.Report(new("", true));
            return Task.FromResult(LetItOutChatRuntime.ValidateResult(new() { Content = raw, FinishReason = "stop" }));
        });
        var result = await new LetItOutChatWork(runtime).GenerateAsync(Request(), new InlineProgress<string>(snapshots.Add), CancellationToken.None);
        Assert.AreEqual("Привет. Я слушаю.", result);
        Assert.IsFalse(snapshots.Any(text => text.Contains('<') || text.Contains('>') || text.Contains("PRIVATE", StringComparison.Ordinal)));
        Assert.IsTrue(snapshots.Any(text => text.Contains("Привет", StringComparison.Ordinal)));
        var saved = Checkpoint(ApplicationBackgroundOperations.Current!.State!);
        Assert.AreEqual(result, saved.Result); Assert.AreEqual("", saved.Partial); Assert.IsTrue(saved.Completed);
    }

    [TestMethod, DataRow(""), DataRow("<thi"), DataRow("<think>PRIVATE"), DataRow("<think></think>"), DataRow("<think>PRIVATE</think>\n\n")]
    public async Task EmptyOrUnfinishedReasoningCannotBecomeAnAssistantAnswer(string raw)
    {
        var snapshots = new List<string>();
        var runtime = new FakeRuntime((_, progress, _) => {
            var answer = LetItOutChatRuntime.CreateAnswerProgress(progress);
            foreach (var character in raw) answer.Report(new(character.ToString()));
            answer.Report(new("", true));
            return Task.FromResult(LetItOutChatRuntime.ValidateResult(new() { Content = raw, FinishReason = "stop" }));
        });
        await Assert.ThrowsAsync<LetItOutChatIncompleteException>(async () => await new LetItOutChatWork(runtime).GenerateAsync(
            Request(), new InlineProgress<string>(snapshots.Add), CancellationToken.None));
        var saved = Checkpoint(ApplicationBackgroundOperations.Current!.State!);
        Assert.IsFalse(saved.Completed); Assert.IsNull(saved.Result); Assert.IsTrue(string.IsNullOrWhiteSpace(saved.Partial));
        Assert.IsTrue(snapshots.All(string.IsNullOrWhiteSpace));
        Assert.Throws<LetItOutChatIncompleteException>(() => LetItOutChatRuntime.CleanAnswer(raw));
    }

    [TestMethod]
    public async Task LegacyCheckpointCleansOnlyAssistantMessagesAndReusesACleanedCompletedResult()
    {
        const string literalUser = "Показывает <think>мой буквальный текст</think>.";
        var legacy = Request() with { Messages = [new("user", literalUser),
            new("assistant", "<think></think>\n\nЯ слушаю."), new("user", "Продолжу.")] };
        var restored = State(legacy, new(legacy, "<think>private unfinished", "<think></think>\n\nРасскажи.", true));
        var runtime = new FakeRuntime((_, _, _) => throw new AssertFailedException("A completed checkpoint must be reused."));
        Assert.AreEqual("Расскажи.", await new LetItOutChatWork(runtime).GenerateAsync(legacy,
            new InlineProgress<string>(_ => { }), CancellationToken.None, restored));
        var checkpoint = Checkpoint(ApplicationBackgroundOperations.Current!.State!);
        Assert.AreEqual(literalUser, checkpoint.Request.Messages[0].Content);
        Assert.AreEqual("Я слушаю.", checkpoint.Request.Messages[1].Content);
        Assert.AreEqual("Расскажи.", checkpoint.Result); Assert.AreEqual("", checkpoint.Partial);
        Assert.AreEqual("Я слушаю.", ApplicationBackgroundOperations.Current!.State!.Input.Deserialize<LetItOutChatRequest>()!.Messages[1].Content);
        Assert.AreEqual(0, runtime.Calls);
    }

    [TestMethod]
    public void LegacyThoughtOnlyHistoryIsNotAnAnswerAndPartialPreviewHidesOpenTags()
    {
        var request = Request() with { Messages = [new("user", "Первое."), new("assistant", "<think></think>"), new("user", "Второе.")] };
        var normalized = LetItOutChatWork.Snapshot(request);
        Assert.HasCount(2, normalized.Messages); Assert.IsTrue(normalized.Messages.All(message => message.Role == "user"));
        Assert.AreEqual("", LetItOutChatRuntime.CleanPartial("<thi"));
        Assert.AreEqual("", LetItOutChatRuntime.CleanPartial("<think>PRIVATE"));
        Assert.AreEqual("\nПривет", LetItOutChatRuntime.CleanPartial("<think>PRIVATE</think>\nПривет"));
        Assert.AreEqual("<3", LetItOutChatRuntime.CleanAnswer("<3"));
    }

    [TestMethod]
    public void LiveCapacityRejectsAConversationThatWouldFitOnlyTheRequestedContext()
    {
        using var props = JsonDocument.Parse("{\"default_generation_settings\":{\"n_ctx\":4096}}");
        var actual = LetItOutChatRuntime.ReadContextCapacity(props.RootElement);
        Assert.AreEqual(4096, actual);
        LetItOutChatRuntime.RequireContextSpace(3000, LetItOutChatRuntime.AnswerTokens, 32768);
        Assert.Throws<LetItOutChatContextException>(() => LetItOutChatRuntime.RequireContextSpace(3000, LetItOutChatRuntime.AnswerTokens, actual!.Value));
        using var slots = JsonDocument.Parse("[{\"n_ctx\":8192},{\"n_ctx\":4096}]");
        Assert.AreEqual(4096, LetItOutChatRuntime.ReadContextCapacity(slots.RootElement));
        foreach (var invalid in new[] { "{}", "[]", "{\"default_generation_settings\":{\"n_ctx\":\"32768\"}}", "[{\"n_ctx\":0}]" }) {
            using var document = JsonDocument.Parse(invalid); Assert.IsNull(LetItOutChatRuntime.ReadContextCapacity(document.RootElement));
        }
    }

    private sealed class FakeRuntime(Func<LetItOutChatRequest, IProgress<ModelStreamChunk>, CancellationToken, Task<string>> generate) : ILetItOutChatRuntime
    {
        public int Calls { get; private set; }
        public int Retirements { get; private set; }
        public Task<string> GenerateAsync(LetItOutChatRequest request, IProgress<ModelStreamChunk> progress, CancellationToken token)
        { Calls++; return generate(request, progress, token); }
        public Task RetireAsync() { Retirements++; return Task.CompletedTask; }
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
