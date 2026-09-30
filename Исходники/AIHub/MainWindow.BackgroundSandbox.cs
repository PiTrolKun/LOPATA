using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private const string SandboxCoreBackgroundKind = "sandbox.core";
    private sealed record SandboxCoreInput(ResumableScenarioSession Session, StorageSettings Storage,
        bool Final, bool ConsumesAnswer, string Trigger);
    private sealed record SandboxCoreCheckpoint(ResumableScenarioSession Session,
        Dictionary<string, ChoiceScenarioGenerationResult> Generations, bool Completed);
    private SandboxCoreCheckpoint? _sandboxCoreCheckpoint;
    private StorageSettings? _sandboxSavedStorage;
    private string? _sandboxSavedSessionId;
    private StorageSettings ActiveSandboxStorage => _sandboxSavedSessionId == _activeResumableSession?.SessionId
        ? _sandboxSavedStorage ?? _storageSettings : _storageSettings;
    private bool _sandboxCoreRunning;
    private CancellationTokenSource? _sandboxCoreLifetime;

    private void RegisterSandboxBackgroundOperations()
    {
        _backgroundOperations!.Register(SandboxCoreBackgroundKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<SandboxCoreInput>() ?? throw new InvalidDataException("Missing sandbox input.");
            var checkpoint = state.Checkpoint?.Deserialize<SandboxCoreCheckpoint>();
            RestoreBackgroundSandboxPage(checkpoint?.Session ?? input.Session, input.Storage);
            await RunBackgroundCoreAsync(input.Final, input.ConsumesAnswer, input.Trigger, state, token);
        });
        _backgroundOperations.Register(ExecutorWorkflowService.BackgroundKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<ExecutorBackgroundInput>() ?? throw new InvalidDataException("Missing executor input.");
            var session = _sessionArchiveService.Load(input.Storage, input.SessionId)
                ?? throw new FileNotFoundException("Saved sandbox session is missing.");
            RestoreBackgroundSandboxPage(session, input.Storage);
            SetExecutorInteractionEnabled(false); StartChoiceAiActivity();
            try
            {
                var result = await _executorWorkflowService.ResumeBackgroundAsync(state, CreateMatrixStreamProgress(), token);
                if (result is ExecutorTurnResult turn) DisplayExecutorResponse(turn);
                else if (result is ExecutorResultSnapshot snapshot) ShowExecutorResultSnapshot(snapshot, snapshot.IsFinal);
                SaveActiveSessionCheckpoint(strict: true);
            }
            finally { StopChoiceAiActivity(); SetExecutorInteractionEnabled(true); }
        });
    }

    private async Task RunBackgroundCoreAsync(bool final, bool consumesAnswer, string trigger,
        BackgroundOperationState? restored = null, CancellationToken lifetime = default)
    {
        if (_sandboxCoreRunning || _choiceScenarioRequestInProgress) return;
        if (_activeResumableSession is null) return;
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _sandboxCoreLifetime = owner; _sandboxCoreRunning = true;
        _sandboxCoreCheckpoint = restored?.Checkpoint?.Deserialize<SandboxCoreCheckpoint>()
            ?? new(_activeResumableSession, [], false);
        try
        {
            if (!_sandboxCoreCheckpoint.Completed)
                SaveActiveSessionCheckpoint(pendingCoreRequest: true, pendingCoreRequestFinal: final,
                    pendingCoreRequestConsumesAnswer: consumesAnswer, pendingCoreRequestTrigger: trigger, strict: true);
            var input = new SandboxCoreInput(_activeResumableSession, ActiveSandboxStorage, final, consumesAnswer, trigger);
            await ApplicationBackgroundOperations.RunAsync(SandboxCoreBackgroundKind, L("ChoiceScenario.Title"),
                _activeResumableSession.SessionId, input, async attempt =>
                {
                    if (_sandboxCoreCheckpoint.Completed) return true;
                    await RequestChoiceScenarioAttemptAsync(final, consumesAnswer, trigger, attempt);
                    SaveActiveSessionCheckpoint(strict: true);
                    _sandboxCoreCheckpoint = _sandboxCoreCheckpoint with { Session = _activeResumableSession!, Completed = true };
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(_sandboxCoreCheckpoint);
                    return true;
                }, owner.Token, restored);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportBackgroundFailure(error); }
        finally
        {
            _sandboxCoreRunning = false; _sandboxCoreLifetime = null; _sandboxCoreCheckpoint = null;
            SetChoiceScenarioInteractionEnabled(true);
        }
    }

    private async Task<ChoiceScenarioGenerationResult> CacheBackgroundCoreGenerationAsync(string stage,
        Func<Task<ChoiceScenarioGenerationResult>> generate)
    {
        if (_sandboxCoreCheckpoint?.Generations.TryGetValue(stage, out var saved) == true) return saved;
        var result = await generate();
        if (_sandboxCoreCheckpoint is not null)
        {
            _sandboxCoreCheckpoint.Generations[stage] = result;
            _sandboxCoreCheckpoint = _sandboxCoreCheckpoint with { Session = _activeResumableSession! };
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(_sandboxCoreCheckpoint);
        }
        return result;
    }

    private void RestoreBackgroundSandboxPage(ResumableScenarioSession session, StorageSettings storage)
    {
        _activeResumableSession = session;
        _sandboxSavedStorage = storage; _sandboxSavedSessionId = session.SessionId;
        _choiceScenarioState.Restore(session.Core);
        _choiceScenarioLog?.Dispose();
        _choiceScenarioLog = ScenarioSessionLog.CreateUncertainty(storage, session.SessionId, session.CurrentRunId);
        _executorWorkflowService.BackgroundSessionId = session.SessionId;
        _currentChoiceScenarioStep = _choiceScenarioState.CurrentStep;
        if (_currentChoiceScenarioStep is { } step) RenderChoiceScenarioStep(step);
        else ChoiceScenarioStatusText.Text = L("ChoiceScenario.CoreWorking");
        RefreshSessionFileCards();
        ShowBackgroundScenarioPage(ChoiceScenarioPage);
    }

    private Task<bool> ViewBackgroundSandboxResultAsync(BackgroundOperationNotice notice)
    {
        var result = _backgroundOperations!.LoadResult(notice.Id);
        if (result is null) return Task.FromResult(false);
        string text;
        if (notice.Kind == SandboxCoreBackgroundKind)
        {
            var checkpoint = result.Checkpoint?.Deserialize<SandboxCoreCheckpoint>();
            if (checkpoint?.Completed != true) return Task.FromResult(false);
            var step = checkpoint.Session.Core.Steps.LastOrDefault();
            if (step is null) return Task.FromResult(false);
            text = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { step.CoreThought, step.Question, string.Join(Environment.NewLine, step.SummaryLines), step.TaskCard?.Goal,
                    step.TaskCard?.PromptForExecutor }.Where(value => !string.IsNullOrWhiteSpace(value)))
                + Environment.NewLine + string.Join(Environment.NewLine, step.Options.Select(option => "• " + option.Title));
        }
        else
        {
            var checkpoint = result.Checkpoint?.Deserialize<ExecutorBackgroundCheckpoint>();
            if (checkpoint?.Result is null) return Task.FromResult(false);
            var input = result.Input.Deserialize<ExecutorBackgroundInput>()!;
            if (input.Action is "CreateFinalResultAsync" or "CreateResultSnapshotAsync")
                text = checkpoint.Result.Value.Deserialize<ExecutorResultSnapshot>()!.Markdown;
            else
            {
                var turn = checkpoint.Result.Value.Deserialize<ExecutorTurnResult>()!;
                text = string.Join(Environment.NewLine + Environment.NewLine,
                    new[] { turn.Thought, turn.Question, turn.CurrentResultSummary, turn.WorkingResultFragment }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                    + Environment.NewLine + string.Join(Environment.NewLine, turn.Options.Select(option => "• " + option.Title));
            }
        }
        ShowBackgroundResultPreview(notice.Title, text);
        return Task.FromResult(true);
    }

    private void ShowBackgroundResultPreview(string title, string text)
    {
        RestoreFromTray();
        new BackgroundResultWindow(this, title, text, L("Tray.ResultReadOnly")).Show();
    }
}
