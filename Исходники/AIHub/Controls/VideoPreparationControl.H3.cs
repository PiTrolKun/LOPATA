using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private string _h3ModelsRoot = "";
    private int _h3Connections;
    private bool _h3Busy;
    private CancellationTokenSource? _h3Cancel;
    private Func<string, Task>? _h3Editor;
    private Func<bool>? _h3HideToTray;
    private DateTimeOffset? _h3ScheduledUtc;
    internal void ConfigureH3Tray(Func<bool> hide) => _h3HideToTray = hide;
    internal void ConfigureH3Generation(string root, int connections) { _h3ModelsRoot = root; _h3Connections = connections; }
    internal void ConfigureH3Editor(Func<string, Task> open) => _h3Editor = open;
    private void CancelH3()
    {
        if (_h3Cancel is not null) _h3Cancel.Cancel();
        else if (ApplicationBackgroundOperations.Current is { State.Kind: VideoH3Job.Kind } c) c.DiscardPending(c.State!.Id);
        RefreshH3OperationState();
    }
    internal void RefreshH3OperationState()
    {
        var state = ApplicationBackgroundOperations.Current?.State;
        var pending = state?.Kind == VideoH3Job.Kind && state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown;
        _generatorPage?.SetGenerating(_h3Busy && _h3ScheduledUtc is null && !pending, pending);
        if (_h3ScheduledUtc is { } due && _h3Busy && !pending) _generatorPage?.SetScheduled(due);
        else if (pending)
        {
            _generatorPage?.SetScheduled(null);
            _generatorPage?.NotifyWorkspace(T(state!.Detail == "VideoPrep.H3RetriesExhausted" ? "H3RetriesExhausted"
                : state.Phase == BackgroundOperationPhase.Paused ? "H3PausedStatus" : "H3WaitingStatus"));
        }
    }
    private Task ScheduleH3Async(DateTimeOffset due)
    {
        if (due <= DateTimeOffset.UtcNow)
        { _generatorPage?.NotifyWorkspace(T("H3ScheduleFutureRequired")); return Task.CompletedTask; }
        return GenerateH3Async(due.ToUniversalTime());
    }
    private async Task GenerateH3Async(DateTimeOffset? scheduledUtc = null)
    {
        if (ConceptBusy || PendingConcept || !Save()) return;
        try
        {
            var job = VideoH3Job.Create(Session.Project, Session.ArchivePath!, _h3ModelsRoot, _h3Connections, VideoGeneratorPlan.Selected(Session.Project)?.Settings.ModelId ?? VideoH3Catalog.ModelId) with { ScheduledUtc = scheduledUtc };
            Session.Changed(); await Task.Run(() => Session.Checkpoint()); _generatorPage?.RefreshResults();
            await RunH3Async(job, null, ApplicationBackgroundOperations.ExitToken);
        }
        catch (OperationCanceledException) { _generatorPage?.NotifyWorkspace(T("H3Cancelled")); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _generatorPage?.NotifyWorkspace(error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal) && !error.Message.Contains(':') ? _localize(error.Message) : T("H3Failed"));
        }
    }
    private async Task<string> RunH3Async(VideoH3Job job, BackgroundOperationState? restored, CancellationToken token)
    {
        if (Session.Project.Id != job.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        _h3Busy = true;
        _h3ScheduledUtc = job.ScheduledUtc > DateTimeOffset.UtcNow ? job.ScheduledUtc : null;
        _generatorPage?.SetGenerating(_h3ScheduledUtc is null);
        if (_h3ScheduledUtc is { } scheduled) _generatorPage?.SetScheduled(scheduled);
        _h3Cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        var project = Session.Project;
        async Task Commit()
        {
            if (!ReferenceEquals(project, Session.Project)) throw new IOException("VideoPrep.AiWrongProject");
            Session.Changed(); await Task.Run(() => Session.Checkpoint()); _generatorPage?.RefreshResults();
        }
        try
        {
            var path = await ApplicationBackgroundOperations.RunAsync(VideoH3Job.Kind, T("H3Generating"), Session.ArchivePath, job,
                async ct =>
                {
                    if (job.ScheduledUtc is { } due)
                    {
                        if (restored is null && _h3HideToTray is not null && !_h3HideToTray())
                            _generatorPage?.NotifyWorkspace(T("H3ScheduleTrayUnavailable"));
                        if (due <= DateTimeOffset.UtcNow)
                            _generatorPage?.NotifyWorkspace(T("H3ScheduleDelayed"));
                        await VideoH3Schedule.WaitAsync(due, remaining =>
                        {
                            _h3ScheduledUtc = due; _generatorPage?.SetScheduled(due);
                        }, ct);
                        _h3ScheduledUtc = null; _generatorPage?.SetScheduled(null); _generatorPage?.SetGenerating(true);
                    }
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    await VideoH3Runner.RunAsync(job, project, Commit, line => _generatorPage?.GenerationProgress(line), ct);
                    return Session.ArchivePath!;
                }, _h3Cancel.Token, restored);
            _generatorPage?.NotifyWorkspace(T(job.CompletionMessageKey)); return path;
        }
        finally
        {
            _h3Cancel.Dispose(); _h3Cancel = null; _h3Busy = false;
            _h3ScheduledUtc = null; _generatorPage?.SetScheduled(null); RefreshH3OperationState();
        }
    }
    internal async Task<string> ResumeH3Async(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<VideoH3Job>() ?? throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (ConceptBusy || !OpenPath(request.ArchivePath) || Session.Project.Id != request.ProjectId)
            throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        var run = VideoH3WorkspaceState.Ensure(Session.Project, request.ModelId).Runs.SingleOrDefault(r => r.Id == request.Run.Id)
            ?? throw new InvalidDataException("VideoPrep.H3ChangedProject");
        if (run.Complete)
            return await ApplicationBackgroundOperations.RunAsync(VideoH3Job.Kind, T("H3Generated"), Session.ArchivePath, request,
                _ => Task.FromResult(Session.ArchivePath!), token, state);
        var fresh = VideoH3Job.Create(Session.Project, Session.ArchivePath!, _h3ModelsRoot, _h3Connections, request.ModelId);
        if (fresh.Run.Id != run.Id || fresh.Run.InputHash != request.Run.InputHash)
        {
            fresh = await VideoH3FinalizationRecovery.CreateAsync(request, fresh, Session.Project, token);
            Session.Changed(); await Task.Run(() => Session.Checkpoint(), token);
            state = state with { Input = JsonSerializer.SerializeToElement(fresh) };
        }
        else fresh = fresh with { FinalizeOnly = request.FinalizeOnly };
        return await RunH3Async(fresh with { ScheduledUtc = request.ScheduledUtc }, state, token);
    }
    internal bool ShowH3Result(BackgroundOperationState state)
    {
        if (state.Project is null || !OpenPath(state.Project)) return false;
        OpenSelectedGenerator(); _generatorPage?.ShowSavedResults(); return true;
    }
    private async Task PauseH3Async()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State is not { Kind: VideoH3Job.Kind } state) return;
        if (state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing) await controller.PauseAsync();
        else await controller.ResumeAsync(state.Id, ApplicationBackgroundOperations.ExitToken);
    }
}
