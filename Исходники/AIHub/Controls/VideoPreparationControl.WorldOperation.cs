using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoWorldOptionsWindow? _worldDialog;
    private CancellationTokenSource? _worldCancel;
    private bool _worldBusy;
    private IReadOnlyList<VideoWorldOption> VisibleOptions => Session.Project.World?.Options
        .Where(o => o.Context == Session.Project.World.Context).ToArray() ?? [];
    private async Task ShowWorldOptionsAsync(BackgroundOperationState? restored = null)
    {
        if (_worldDialog is not null || _conceptBusy || _worldBusy || restored is null && PendingConcept) return;
        if (!WorldReviewed()) return;
        if (_assistant is null) { _statusKey = "AiUnavailable"; RefreshStatus(); return; }
        if (_conceptChoices.Count == 0 || ActiveText.AssistantModelId == "") await LoadConceptChoicesAsync();
        if (_conceptChoices.Count == 0 || !IsWorldStep) return;
        var project = Session.Project; var w = VideoWorld.Ensure(project);
        if (w.Working.AssistantModelId == "") w.Working.AssistantModelId = ActiveText.AssistantModelId;
        if (!Save()) return;
        var dialog = new VideoWorldOptionsWindow(Window.GetWindow(this), _localize, _conceptChoices, w.Working.AssistantModelId);
        _worldDialog = dialog; _conceptDialogOpen = true; Task running = Task.CompletedTask;
        dialog.StartRequested += model => { if (!_worldBusy) running = StartWorldBatchAsync(model); };
        dialog.PauseRequested += () => _ = PauseResumeConceptAsync();
        dialog.RegenerateRequested += id =>
        {
            if (_worldBusy) return;
            var option = w.Options.Single(o => o.Id == id);
            running = StartWorldBatchAsync(dialog.SelectedModelId, option.Freedom);
        };
        dialog.ChooseRequested += id =>
        {
            try
            {
                if (!ReferenceEquals(Session.Project, project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
                VideoWorld.Apply(project, w.Options.Single(o => o.Id == id)); Session.Changed();
                if (!Checkpoint()) return;
                _worldCancel?.Cancel(); dialog.Accept(id);
            }
            catch (Exception e) { ConceptError(e); RefreshStatus(); }
        };
        dialog.Closing += (_, _) => _worldCancel?.Cancel();
        dialog.Update(VisibleOptions, false);
        if (restored is not null)
            dialog.Loaded += (_, _) => running = RunWorldBatchAsync(restored.Input.Deserialize<VideoWorldBatch>()
                ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest"), default, restored);
        try { BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken); }
        catch (OperationCanceledException) { _worldCancel?.Cancel(); }
        finally
        {
            _worldDialog = null; _conceptDialogOpen = false;
            await running; Render();
        }
    }
    private Task StartWorldBatchAsync(string model, int? onlyLevel = null)
    {
        var p = Session.Project; var w = VideoWorld.Ensure(p);
        if (!VideoWorld.SourceCurrent(p)) { _statusKey = "WorldStale"; RefreshStatus(); return Task.CompletedTask; }
        var choice = _conceptChoices.SingleOrDefault(c => c.Id == model);
        if (choice is not { Model: not null, Fits: not false }) { _statusKey = "AiMemoryUnavailable"; RefreshStatus(); return Task.CompletedTask; }
        VideoWorld.Capture(p); w.Working.AssistantModelId = model; p.LastAssistantModelId = model;
        var batchId = Guid.NewGuid().ToString("N");
        var requests = Enumerable.Range(1, 3).Where(level => onlyLevel is null || level == onlyLevel).Select(level =>
            new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, Session.ArchivePath!, w.RevisionId,
                w.Cards.Any(c => !string.IsNullOrWhiteSpace(c.Text.IdeaText)) ? w.Working.IdeaText : "", p.Form,
                model, choice.Model.Path, level, VideoWorldProtocol.Version, EngineSetSelection.ActiveId, VideoWorld.Purpose,
                ActivateResult: false, ContextRevisionId: p.Development!.TranslationRevisionId, ContextText: w.Context,
                ContextOriginal: p.IdeaText, ResponseLanguage: InputLanguage)).ToArray();
        foreach (var r in requests) w.Options.Add(new()
        {
            Id = r.Id,
            BatchId = batchId,
            Freedom = r.Freedom,
            Context = w.Context,
            BaseRevisionId = w.RevisionId
        });
        Session.Changed();
        if (!Checkpoint()) return Task.CompletedTask;
        return RunWorldBatchAsync(new(batchId, p.Id, Session.ArchivePath!, requests), default);
    }
    private async Task RunWorldBatchAsync(VideoWorldBatch batch, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var project = Session.Project; var world = VideoWorld.Ensure(project);
        _worldBusy = true; _worldCancel = CancellationTokenSource.CreateLinkedTokenSource(token); _statusKey = "WorldWorking";
        _worldDialog?.Update(VisibleOptions, true); Render();
        try
        {
            await VideoWorldWork.RunAsync(batch, T("WorldPropose"), _assistant!, _conceptReceipts, async (r, status, answer) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(Session.Project, project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
                    var option = world.Options.SingleOrDefault(o => o.Id == r.Id) ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
                    if (option.Answer is not null && status == "running") return;
                    option.Status = status; if (answer is not null) option.Answer = answer;
                    Session.Changed(); if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
                    _worldDialog?.Update(VisibleOptions, true);
                });
            }, _worldCancel.Token, restored);
            _statusKey = "WorldReady";
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception e) { ConceptError(e); }
        finally
        {
            foreach (var option in world.Options.Where(o => o.BatchId == batch.Id && o.Status is "running" or "pending")) option.Status = "canceled";
            Session.Changed(); Checkpoint(); _worldBusy = false; _worldCancel.Dispose(); _worldCancel = null;
            _worldDialog?.Update(VisibleOptions, false); Render();
        }
    }
    public async Task ResumeWorldAsync(BackgroundOperationState state, CancellationToken token)
    {
        var batch = state.Input.Deserialize<VideoWorldBatch>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoWorldWork.Validate(batch);
        if (!OpenPath(batch.ProjectPath) || Session.Project.Id != batch.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        Session.Project.CurrentStep = "world"; Session.Changed(); Checkpoint(); Render();
        await RunWorldBatchAsync(batch, token, state);
    }
    public bool ShowWorldResult(BackgroundOperationState state)
    {
        var batch = state.Input.Deserialize<VideoWorldBatch>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (ConceptBusy || !OpenPath(batch.ProjectPath)) return false;
        Session.Project.CurrentStep = "world"; Session.Changed(); Checkpoint(); Render(); _ = ShowWorldOptionsAsync(); return true;
    }
}
