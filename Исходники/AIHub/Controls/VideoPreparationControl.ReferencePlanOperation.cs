using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private async Task PrepareReferencePlanAsync(bool discover, string? onlyItem = null, int freedom = 1)
    {
        if (ConceptBusy || PendingConcept) return;
        if (_assistant is null) { _statusKey = "AiUnavailable"; RefreshStatus(); return; }
        var p = Session.Project; var plan = VideoReferencePlans.Ensure(p);
        if (_conceptChoices.Count == 0) await LoadConceptChoicesAsync();
        if (!ReferenceEquals(p, Session.Project)) return;
        if (plan.ModelId == "") plan.ModelId = p.LastAssistantModelId.Length > 0 ? p.LastAssistantModelId : VideoConceptModels.Default(_conceptChoices);
        var choice = _conceptChoices.FirstOrDefault(c => c.Id == plan.ModelId);
        if (choice?.Model is null || choice.Fits == false) { _statusKey = "AiNeedsPreparation"; Render(); return; }
        if (!discover && !plan.Items.Any(i => i.Selected && (onlyItem is null || i.Id == onlyItem)))
        { _statusKey = "RefPlanChooseItems"; RefreshStatus(); return; }
        if (!discover && plan.Items.Where(i => i.Selected && (onlyItem is null || i.Id == onlyItem)).Any(i =>
            string.IsNullOrWhiteSpace(i.Name) || VideoReferences.Description(p, i.CardId) is null))
        { _statusKey = "RefPlanMissingName"; RefreshStatus(); return; }
        p.LastAssistantModelId = choice.Id; Changed();
        if (!Save()) return;
        var context = VideoReferencePlans.Context(p);
        VideoConceptRequest Request(string purpose, string text, string materialId = "") => new(Guid.NewGuid().ToString("N"),
            p.Id, Session.ArchivePath!, p.IdeaRevisionId, text, p.Form, choice.Id, choice.Model.Path, freedom,
            VideoReferencePromptProtocol.Version, EngineSetSelection.ActiveId, purpose,
            ContextRevisionId: p.World?.RevisionId ?? p.IdeaRevisionId, ContextText: context, ResponseLanguage: InputLanguage, MaterialId: materialId);
        var requests = discover ? new[] { Request(VideoReferencePromptProtocol.ListPurpose, "[]") }
            : plan.Items.Where(i => i.Selected && (onlyItem is null ? !VideoReferencePlans.Ready(p, i) : i.Id == onlyItem))
                .Select(i => Request(VideoReferencePromptProtocol.PromptPurpose, VideoReferencePlans.Snapshot(i), i.Id)).ToArray();
        if (!discover) plan.Page = "prompts";
        Changed(); if (!Checkpoint()) return;
        if (requests.Length == 0) { _statusKey = "RefPlanPrepared"; Render(); return; }
        await RunReferenceBatchAsync(new(Guid.NewGuid().ToString("N"), p.Id, Session.ArchivePath!, requests), default);
    }
    private async Task RunReferenceBatchAsync(VideoReferenceBatch batch, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_assistant is null) throw new InvalidOperationException("VideoPrep.AiUnavailable");
        var p = Session.Project;
        _conceptBusy = true; _runningPurpose = batch.Requests[0].Purpose;
        _conceptCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _statusKey = "RefPlanWorking"; Render();
        try
        {
            await VideoReferenceWork.RunAsync(batch, T("RefPlanOperation"), _assistant, _conceptReceipts, async (request, answer) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(p, Session.Project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
                    VideoReferencePlans.Apply(p, request, answer); Session.Changed();
                    if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
                    Render();
                });
            }, _conceptCancel.Token, restored);
            _statusKey = "RefPlanPrepared";
            if (ConceptResultVisible) ApplicationBackgroundOperations.Current?.Acknowledge(batch.Id);
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _conceptBusy = false; _conceptCancel.Dispose(); _conceptCancel = null; Render(); }
    }
    private async Task ImproveReferencePromptAsync(VideoReferencePrompt item)
    {
        if (ConceptBusy || PendingConcept || _assistant is null) return;
        if (_conceptChoices.Count == 0) await LoadConceptChoicesAsync();
        var p = Session.Project; var plan = VideoReferencePlans.Ensure(p);
        var selected = plan.ModelId.Length > 0 ? plan.ModelId : p.LastAssistantModelId.Length > 0
            ? p.LastAssistantModelId : VideoConceptModels.Default(_conceptChoices);
        var dialog = new VideoConceptOptionsWindow(Window.GetWindow(this), key => _localize(key is "VideoPrep.AiToggle" or "VideoPrep.AiImprove"
            ? "VideoPrep.RefPlanImprove" : key), _conceptChoices,
            selected, 1);
        var accepted = false; _conceptDialogOpen = true;
        try
        {
            accepted = BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) == true;
            if (accepted) { plan.ModelId = dialog.SelectedModelId; p.LastAssistantModelId = dialog.SelectedModelId; Changed(); }
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        finally { _conceptDialogOpen = false; }
        if (accepted) await PrepareReferencePlanAsync(false, item.Id, dialog.Freedom);
    }
    public async Task ResumeReferencePlanAsync(BackgroundOperationState state, CancellationToken token)
    {
        var batch = state.Input.Deserialize<VideoReferenceBatch>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoReferenceWork.Validate(batch);
        if (!OpenPath(batch.ProjectPath) || Session.Project.Id != batch.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        Session.Project.CurrentStep = "references"; VideoReferences.Ensure(Session.Project).Choice = "create";
        Session.Changed(); if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
        await RunReferenceBatchAsync(batch, token, state);
    }
    public bool ShowReferencePlanResult(BackgroundOperationState state)
    {
        var batch = state.Input.Deserialize<VideoReferenceBatch>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoReferenceWork.Validate(batch);
        if (ConceptBusy || !OpenPath(batch.ProjectPath) || Session.Project.Id != batch.ProjectId) return false;
        foreach (var request in batch.Requests)
            if (_conceptReceipts.Read(request) is { } answer) VideoReferencePlans.Apply(Session.Project, request, answer);
        Session.Project.CurrentStep = "references"; VideoReferences.Ensure(Session.Project).Choice = "create";
        Changed(); if (!Checkpoint()) return false; Render(); return true;
    }
}
