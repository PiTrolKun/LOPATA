using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundSandboxTests
{
    private static void Host(BackgroundOperationController? controller, CancellationToken token = default)
    {
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null, controller);
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null, token);
    }
    private static StructuredChatResult Turn() => new() { Content = JsonSerializer.Serialize(new ExecutorTurnResult
    {
        Status = ExecutorTurnStatuses.Working, Action = ExecutorTurnActions.AskUser,
        StageId = ExecutorStageIds.TaskDefinition, Thought = "Уточняю задачу", Question = "Какой результат нужен?",
        Options = [new() { Title = "Первый" }, new() { Title = "Второй" }], AllowCustom = true
    }) };
    private static StructuredChatResult Tools(string name = "session_files_list") => new() { ToolCalls =
        [new() { Id = "call1", Function = new() { Name = name, Arguments = "{}" } }] };
    private static Task<ExecutorToolExecution> CompletedTool(StructuredToolCall call, CancellationToken token)
        => Task.FromResult(new ExecutorToolExecution(call.Function.Name, "{\"files\":[]}", true));

    [TestMethod]
    public async Task LivePauseKeepsConfirmedToolAndDoesNotDuplicateAnswer()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Store); Host(controller);
        using var workflow = f.Workflow(); var call = 0; var tools = 0; var started = new TaskCompletionSource();
        workflow.BackgroundGeneration = async (messages, token) =>
        {
            if (++call == 1) return Tools();
            if (call == 2) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Turn();
        };
        workflow.BackgroundTool = (tool, token) => { tools++; return CompletedTool(tool, token); };
        var work = workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, tools);
        var checkpoint = f.Store.Load()!.Checkpoint!.Value.Deserialize<ExecutorBackgroundCheckpoint>()!;
        Assert.HasCount(1, checkpoint.Session.EvidenceReceipts); Assert.IsTrue(checkpoint.Session.BackgroundLoopPending);
        await controller.ResumeAsync(default); await work;
        Assert.AreEqual(1, tools); Assert.AreEqual(3, call);
        Assert.HasCount(1, workflow.CreateCheckpoint()!.Messages.Where(m => m.Role == "tool").ToArray());
    }

    [TestMethod]
    public async Task RestartResumesPendingLoopWithoutReplayingConfirmedTool()
    {
        using var f = new Fixture(); using var exit = new CancellationTokenSource();
        var first = new BackgroundOperationController(f.Store); Host(first, exit.Token);
        using var workflow = f.Workflow(); var calls = 0; var started = new TaskCompletionSource();
        workflow.BackgroundGeneration = async (_, token) =>
        { if (++calls == 1) return Tools(); started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Turn(); };
        workflow.BackgroundTool = CompletedTool;
        var work = workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await first.PauseAsync(); first.CheckpointForExit(); exit.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        var second = new BackgroundOperationController(f.Store); second.Load(); Host(second);
        using var restored = f.Workflow(); var repeatedTools = 0;
        restored.BackgroundGeneration = (_, _) => Task.FromResult(Turn());
        restored.BackgroundTool = (call, token) => { repeatedTools++; return CompletedTool(call, token); };
        await restored.ResumeBackgroundAsync(second.State!, new Progress<ModelStreamChunk>(), default);
        Assert.AreEqual(0, repeatedTools); Assert.HasCount(1, restored.CreateCheckpoint()!.EvidenceReceipts);
        Assert.AreEqual(first.State!.Id, second.State!.Id); Assert.AreEqual(BackgroundOperationPhase.Completed, second.State.Phase);
    }

    [TestMethod]
    public async Task PausedContinuationDoesNotAppendUserAnswerTwice()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Store); Host(controller);
        using var workflow = f.Workflow(); workflow.BackgroundGeneration = (_, _) => Task.FromResult(Turn());
        await workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        var started = new TaskCompletionSource(); var blocked = true;
        workflow.BackgroundGeneration = async (messages, token) =>
        { if (blocked) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } return Turn(); };
        // The installed session owns the backend seam captured at construction.
        var service = (ExecutorSessionService)typeof(ExecutorWorkflowService).GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workflow)!;
        service.BackgroundGeneration = workflow.BackgroundGeneration;
        var work = workflow.ContinueAndRunAsync("UNIQUE_ANSWER", new Progress<ModelStreamChunk>(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync(); blocked = false;
        await controller.ResumeAsync(default); await work;
        Assert.HasCount(1, workflow.CreateCheckpoint()!.Messages.Where(m => m.Role == "user" && m.Content?.Contains("UNIQUE_ANSWER") == true).ToArray());
    }

    [TestMethod]
    public async Task UncertainToolOutcomeRequiresDecisionAndNeverRepeatsTool()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Store); Host(controller);
        using var workflow = f.Workflow(); var started = new TaskCompletionSource(); var calls = 0;
        workflow.BackgroundGeneration = (_, _) => Task.FromResult(Tools("session_image_transform"));
        workflow.BackgroundTool = async (_, token) => { calls++; started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new("transform", "", true); };
        var work = workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync(); await controller.ResumeAsync(default);
        await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => work);
        Assert.AreEqual(1, calls); Assert.IsTrue(controller.State!.RequiresDecision);
    }

    [TestMethod]
    public async Task CompletedReceiptSurvivesNextOperationAndIsReadByExactId()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Store); Host(controller);
        using var workflow = f.Workflow(); workflow.BackgroundGeneration = (_, _) => Task.FromResult(Turn());
        await workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        var id = controller.State!.Id;
        await workflow.ContinueAndRunAsync("NEXT", new Progress<ModelStreamChunk>(), default);
        var first = controller.LoadResult(id)!;
        Assert.AreEqual(id, first.Id); Assert.AreNotEqual(id, controller.State.Id);
        Assert.IsNotNull(first.Checkpoint!.Value.Deserialize<ExecutorBackgroundCheckpoint>()!.Result);
    }

    [TestMethod]
    public async Task SnapshotCommittedBeforeRootCompletionIsNotGeneratedTwice()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Store); Host(controller);
        using var workflow = f.Workflow(); workflow.BackgroundGeneration = (_, _) => Task.FromResult(Turn());
        await workflow.ExecuteAsync(f.Artifact, f.Handoff, new(), f.Storage, new Progress<ModelStreamChunk>(), default);
        var checkpoint = workflow.CreateCheckpoint()!; checkpoint.BriefConfirmed = true;
        checkpoint.PendingSnapshotId = "snapshot_fixture"; checkpoint.PendingSnapshotMarkdown = "Saved text";
        checkpoint.Snapshots.Add(new() { Id = "snapshot_fixture", Markdown = "Saved text", Version = 1 });
        var state = new BackgroundOperationState { Kind = ExecutorWorkflowService.BackgroundKind, Title = "Fixture",
            Input = JsonSerializer.SerializeToElement(new ExecutorBackgroundInput("fixture-session", "CreateResultSnapshotAsync",
                JsonSerializer.SerializeToElement(new { Operation = "result" }), f.Storage, checkpoint, new())),
            Checkpoint = JsonSerializer.SerializeToElement(new ExecutorBackgroundCheckpoint(checkpoint, new())) };
        using var restored = f.Workflow(); var calls = 0;
        restored.BackgroundGeneration = (_, _) => { calls++; return Task.FromResult(Turn()); };
        var result = (ExecutorResultSnapshot)await restored.ResumeBackgroundAsync(state, new Progress<ModelStreamChunk>(), default);
        Assert.AreEqual("snapshot_fixture", result.Id); Assert.AreEqual(0, calls); Assert.HasCount(1, restored.Snapshots);
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-background-sandbox-" + Guid.NewGuid().ToString("N"));
        public BackgroundOperationStore Store => new(Path.Combine(Root, "operation.json"));
        public StorageSettings Storage { get; } = new();
        public ExecutorModelArtifact Artifact { get; }
        public ExecutorHandoffPackage Handoff { get; } = new() { LanguageCode = "ru", Goal = "Проверить данные" };
        public Fixture()
        {
            Directory.CreateDirectory(Root); var model = Path.Combine(Root, "fixture.gguf"); File.WriteAllText(model, "FAKE_MODEL");
            Artifact = new() { IsInstalled = true, InstalledPath = model, RepoId = "fixture", FileName = "fixture.gguf" };
            Storage.Results.Locations.Add(new() { Path = Root });
        }
        public ExecutorWorkflowService Workflow() => new(new UserContextService(new UserProfileStore(), new IpLocationService()))
        { BackgroundSessionId = "fixture-session" };
        public void Dispose()
        {
            Host(null);
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }
}
