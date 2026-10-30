using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private string _vaceModelsRoot = "";
    private int _vaceConnections;
    private bool _vaceBusy;
    private CancellationTokenSource? _vaceCancel;
    private Func<string, Task>? _vaceEditor;
    private Func<bool>? _vaceHideToTray;
    private DateTimeOffset? _vaceScheduledUtc;

    internal void ConfigureVaceGeneration(string root, int connections) { _vaceModelsRoot = root; _vaceConnections = connections; }
    internal void ConfigureVaceEditor(Func<string, Task> open) => _vaceEditor = open;
    internal void ConfigureVaceTray(Func<bool> hide) => _vaceHideToTray = hide;

    internal void RefreshVaceOperationState()
    {
        var state = ApplicationBackgroundOperations.Current?.State;
        var pending = state?.Kind == VideoVaceJob.Kind && state.Phase is BackgroundOperationPhase.Paused
            or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown;
        _vacePage?.SetGenerating(_vaceBusy && _vaceScheduledUtc is null && !pending, pending);
        _vacePage?.SetScheduled(_vaceScheduledUtc);
        if (pending) _vacePage?.NotifyWorkspace(T(state!.Detail == "VideoPrep.VaceRetriesExhausted" ? "VaceRetriesExhausted"
            : state.Phase == BackgroundOperationPhase.Paused ? "VacePaused" : "VaceWaiting"));
    }
    private void CancelVace()
    {
        if (_vaceCancel is not null) _vaceCancel.Cancel();
        else if (ApplicationBackgroundOperations.Current is { State.Kind: VideoVaceJob.Kind } controller)
            controller.DiscardPending(controller.State!.Id);
        RefreshVaceOperationState();
    }
    private async Task PauseVaceAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State is not { Kind: VideoVaceJob.Kind } state) return;
        if (state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing) await controller.PauseAsync();
        else await controller.ResumeAsync(state.Id, ApplicationBackgroundOperations.ExitToken);
    }
    private async Task GenerateVaceAsync(DateTimeOffset? due = null)
    {
        if (!VideoGeneratorPlan.IsAvailable(VideoVaceCatalog.ModelId))
        { _vacePage?.NotifyWorkspace(T("GeneratorUnavailable")); return; }
        if (ConceptBusy || PendingConcept || !Save()) return;
        if (due <= DateTimeOffset.UtcNow) { _vacePage?.NotifyWorkspace(T("H3ScheduleFutureRequired")); return; }
        try
        {
            var job = VideoVaceJob.Create(Session.Project, Session.ArchivePath!, _vaceModelsRoot, _vaceConnections) with { ScheduledUtc = due };
            Session.Changed(); await Task.Run(() => Session.Checkpoint()); _vacePage?.RefreshResults();
            await RunVaceAsync(job, null, ApplicationBackgroundOperations.ExitToken);
        }
        catch (OperationCanceledException) { _vacePage?.NotifyWorkspace(T("VaceCancelled")); }
        catch (BackgroundOperationWaitingException error) { _vacePage?.NotifyWorkspace(_localize(error.Message)); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _vacePage?.NotifyWorkspace(error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal) && !error.Message.Contains(':')
                ? _localize(error.Message) : T("VaceFailed"));
        }
    }
    private async Task<string> RunVaceAsync(VideoVaceJob job, BackgroundOperationState? restored, CancellationToken token)
    {
        if (Session.Project.Id != job.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        _vaceBusy = true; _vaceScheduledUtc = job.ScheduledUtc > DateTimeOffset.UtcNow ? job.ScheduledUtc : null;
        _vaceCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _vacePage?.SetGenerating(_vaceScheduledUtc is null);
        _vacePage?.GenerationProgress("[VACE event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = job.Run.GenerationSeconds }));
        if (_vaceScheduledUtc is { } scheduled) _vacePage?.SetScheduled(scheduled);
        var project = Session.Project;
        async Task Commit()
        {
            if (!ReferenceEquals(project, Session.Project)) throw new IOException("VideoPrep.AiWrongProject");
            Session.Changed(); await Task.Run(() => Session.Checkpoint()); _vacePage?.RefreshResults();
        }
        try
        {
            return await ApplicationBackgroundOperations.RunAsync(VideoVaceJob.Kind, T("VaceGenerating"), Session.ArchivePath, job,
                async ct =>
                {
                    if (job.ScheduledUtc is { } due)
                    {
                        if (restored is null && _vaceHideToTray is not null && !_vaceHideToTray())
                            _vacePage?.NotifyWorkspace(T("H3ScheduleTrayUnavailable"));
                        await VideoH3Schedule.WaitAsync(due, _ => _vacePage?.SetScheduled(due), ct);
                        _vaceScheduledUtc = null; _vacePage?.SetScheduled(null); _vacePage?.SetGenerating(true);
                        _vacePage?.GenerationProgress("[VACE event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = job.Run.GenerationSeconds }));
                    }
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    await VideoVaceRunner.RunAsync(job, project, Commit, line => _vacePage?.GenerationProgress(line), ct);
                    _vacePage?.NotifyWorkspace(T("VaceGenerated"));
                    return Session.ArchivePath!;
                }, _vaceCancel.Token, restored);
        }
        finally
        {
            _vaceCancel.Dispose(); _vaceCancel = null; _vaceBusy = false;
            _vaceScheduledUtc = null; RefreshVaceOperationState();
        }
    }
    internal async Task<string> ResumeVaceAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (!VideoGeneratorPlan.IsAvailable(VideoVaceCatalog.ModelId))
            throw new BackgroundOperationWaitingException("VideoPrep.GeneratorUnavailable");
        var request = state.Input.Deserialize<VideoVaceJob>() ?? throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (ConceptBusy || !OpenPath(request.ArchivePath) || Session.Project.Id != request.ProjectId)
            throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        var run = VideoVaceWorkspaceState.Ensure(Session.Project).Runs.SingleOrDefault(r => r.Id == request.Run.Id)
            ?? throw new InvalidDataException("VideoPrep.VaceChangedProject");
        if (run.Complete)
            return await ApplicationBackgroundOperations.RunAsync(VideoVaceJob.Kind, T("VaceGenerated"), Session.ArchivePath,
                request, _ => Task.FromResult(Session.ArchivePath!), token, state);
        var fresh = VideoVaceJob.Create(Session.Project, Session.ArchivePath!, _vaceModelsRoot, _vaceConnections);
        if (fresh.Run.Id != run.Id || fresh.Run.InputHash != request.Run.InputHash)
            throw new BackgroundOperationWaitingException("VideoPrep.VaceChangedProject");
        return await RunVaceAsync(fresh with { ScheduledUtc = request.ScheduledUtc }, state, token);
    }
    internal bool ShowVaceResult(BackgroundOperationState state)
    {
        if (state.Project is null || !OpenPath(state.Project)) return false;
        OpenSelectedGenerator(); _vacePage?.ShowSavedResults(); return true;
    }
}
