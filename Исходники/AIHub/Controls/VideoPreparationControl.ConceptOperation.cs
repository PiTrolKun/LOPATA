using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private IVideoConceptAssistant? _assistant;
    private Func<CancellationToken, Task<IReadOnlyList<VideoConceptChoice>>>? _loadConceptChoices;
    private VideoConceptReceiptStore _conceptReceipts = new();
    private bool _conceptBusy, _loadingConceptModels;
    private bool _conceptDialogOpen;
    private CancellationTokenSource? _conceptCancel;
    private BackgroundOperationPhase? _lastConceptPhase;
    private bool ConceptBusy => _finishingBusy || _shotsStarting || _shotsBusy || _drawingBusy || _referenceImporting || _conceptBusy || _worldBusy || _warming || _loadingConceptModels || _conceptDialogOpen || _checkingNarrativeLanguage;
    private bool ConceptResultVisible => IsVisible && Window.GetWindow(this) is { IsActive: true, WindowState: not WindowState.Minimized };
    private BackgroundOperationState? PendingConceptState => ApplicationBackgroundOperations.Current is { } controller
        ? new[] { controller.State }.Concat(controller.PausedOperations).FirstOrDefault(s => s?.Kind is VideoConceptWork.Kind or VideoTranslationProtocol.Kind or VideoDevelopment.Kind or VideoDevelopment.TranslationKind or VideoWorld.Kind or VideoWorld.CardKind or VideoWorld.TranslationKind
            or VideoReferenceWork.Kind or VideoReferenceImageWork.Kind or VideoShots.Kind or VideoShots.TranslationKind or ExportKind && s.Project == Session.ArchivePath && s.Phase is not (BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled)) : null;
    private bool PendingConcept => PendingConceptState is not null;

    public void ConfigureConceptAssistant(IVideoConceptAssistant assistant,
        Func<CancellationToken, Task<IReadOnlyList<VideoConceptChoice>>> choices, VideoConceptReceiptStore? receipts = null)
    {
        if (ReferenceEquals(_assistant, assistant)) return;
        _assistant = assistant; _loadConceptChoices = choices; _conceptReceipts = receipts ?? new();
        if (assistant is IVideoConceptResident resident) resident.RuntimeChanged += RefreshRuntimeStatus;
        if (IsLoaded) { ScheduleWarmup(); ScheduleShots(); }
    }

    private async Task ToggleConceptPanelAsync()
    {
        if (ConceptBusy || PendingConcept) return;
        if (_assistant is null) { _statusKey = "AiUnavailable"; RefreshStatus(); return; }
        var material = ActiveText;
        if (_conceptChoices.Count == 0 || ActiveText.AssistantModelId == "") await LoadConceptChoicesAsync();
        if (!ReferenceEquals(ActiveText, material)) return;
        if (_conceptChoices.Count == 0) return;
        var projectId = Session.Project.Id;
        var dialog = new VideoConceptOptionsWindow(Window.GetWindow(this), _localize, _conceptChoices,
            ActiveText.AssistantModelId, ActiveText.ImprovementFreedom, development: IsDevelopmentStep);
        var accepted = false;
        _conceptDialogOpen = true;
        try
        {
            if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) == true
                && Session.Project.Id == projectId)
            {
                ActiveText.AssistantModelId = dialog.SelectedModelId;
                ActiveText.ImprovementFreedom = dialog.Freedom;
                Session.Project.LastAssistantModelId = dialog.SelectedModelId;
                Changed(); accepted = true;
            }
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _conceptDialogOpen = false; Render(); }
        if (accepted) await ImproveConceptAsync();
    }

    private async Task LoadConceptChoicesAsync()
    {
        if (_loadConceptChoices is null || ConceptBusy) return;
        var projectId = Session.Project.Id;
        var material = ActiveText;
        _loadingConceptModels = true; _statusKey = "AiCheckingModels"; Render();
        try
        {
            _conceptChoices = await _loadConceptChoices(ApplicationBackgroundOperations.ExitToken);
            if (Session.Project.Id == projectId && ReferenceEquals(ActiveText, material) && ActiveText.AssistantModelId == "")
            {
                ActiveText.AssistantModelId = VideoConceptModels.Default(_conceptChoices);
                Changed(); Checkpoint();
            }
            _statusKey = "";
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _loadingConceptModels = false; Render(); }
    }

    public async Task ImproveConceptAsync(CancellationToken token = default)
    {
        if (ConceptBusy || PendingConcept) return;
        if (IsDevelopmentStep)
        {
            if (!DevelopmentSourceAvailable()) return;
            VideoDevelopment.RefreshSource(Session.Project); Session.Changed();
        }
        else if (!HasIdeaForNext()) return;
        if (!WorldReviewed()) return;
        if (IsWorldStep) UpdateCardContext(SelectedWorldCard);
        if (_assistant is null) { _statusKey = "AiUnavailable"; Render(); return; }
        var material = ActiveText;
        if (_conceptChoices.Count == 0 || ActiveText.AssistantModelId == "") await LoadConceptChoicesAsync();
        if (!ReferenceEquals(ActiveText, material)) { _statusKey = "LanguageSourceChanged"; Render(); return; }
        var choice = _conceptChoices.FirstOrDefault(c => c.Id == ActiveText.AssistantModelId);
        if (choice?.Model is null) { _statusKey = "AiNeedsPreparation"; Render(); return; }
        if (choice.Fits == false) { _statusKey = "AiMemoryUnavailable"; Render(); return; }
        if (!Save()) return;
        var project = ActiveText;
        var request = new VideoConceptRequest(Guid.NewGuid().ToString("N"), project.Id, Session.ArchivePath!,
            project.IdeaRevisionId, project.IdeaText, project.Form, choice.Id, choice.Model.Path,
            project.ImprovementFreedom, IsWorldStep ? VideoWorldProtocol.Version : IsDevelopmentStep ? VideoDevelopmentProtocol.Version : VideoConceptProtocol.Version, EngineSetSelection.ActiveId,
            IsWorldStep ? VideoWorld.CardPurpose : IsDevelopmentStep ? VideoDevelopment.Purpose : "concept", ContextRevisionId: project.ContextRevisionId,
            ContextText: project.ContextText, ContextOriginal: IsWorldStep ? Session.Project.World!.Context : project.ContextOriginal, ResponseLanguage: InputLanguage,
            MaterialId: IsWorldStep ? SelectedWorldCard.Id : "");
        await RunConceptAsync(request, token);
    }

    private async Task RunConceptAsync(VideoConceptRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_assistant is null) throw new InvalidOperationException("VideoPrep.AiUnavailable");
        var translating = VideoTranslationProtocol.IsTranslation(request);
        _runningPurpose = request.Purpose;
        _conceptBusy = true;
        _conceptCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _statusKey = translating ? "TranslationWorking" : "AiWorking"; Render();
        try
        {
            await VideoConceptWork.RunAsync(request, T(translating ? "TranslationOperation" : VideoDevelopment.IsDevelopment(request) ? "DevelopmentOperation" : "AiOperation"), _assistant, _conceptReceipts, async answer =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (Session.Project.Id != request.ProjectId) throw new InvalidDataException("VideoPrep.AiWrongProject");
                    var target = VideoDevelopment.ForRequest(Session.Project, request);
                    var guarded = request with { ActivateResult = request.ActivateResult && VideoDevelopment.RequestCurrent(Session.Project, request) };
                    var selected = translating ? VideoTranslationHistory.Apply(target, guarded, answer)
                        : VideoConceptHistory.Apply(target, guarded, answer);
                    VideoWorld.Capture(Session.Project); Session.Changed();
                    if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
                    _statusKey = selected || ResultSelected(request) ? translating ? "TranslationReady" : "AiReady" : "AiReadyInHistory";
                    Render();
                });
            }, _conceptCancel.Token, restored);
            if (ConceptResultVisible && ResultSelected(request))
                ApplicationBackgroundOperations.Current?.Acknowledge(request.Id);
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally
        {
            _conceptBusy = false; _conceptCancel.Dispose(); _conceptCancel = null; Render();
            if (ResultSelected(request))
                _ = Dispatcher.InvokeAsync(() => { if (translating) _englishInput?.BringIntoView(); else _ideaInput?.BringIntoView(); },
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public async Task ResumeConceptAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<VideoConceptRequest>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (!OpenPath(request.ProjectPath) || Session.Project.Id != request.ProjectId)
            throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        if (request.Purpose == VideoWorld.CardPurpose && Session.Project.World is { } world) world.SelectedCardId = request.MaterialId;
        Session.Project.CurrentStep = VideoDevelopment.StepFor(request); Session.Changed();
        if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
        await RunConceptAsync(request, token, state);
    }

    public bool ShowConceptResult(BackgroundOperationState saved)
    {
        var request = saved.Input.Deserialize<VideoConceptRequest>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (ConceptBusy || !OpenPath(request.ProjectPath) || Session.Project.Id != request.ProjectId) return false;
        var answer = _conceptReceipts.Read(request); if (answer is null) return false;
        if (request.Purpose == VideoWorld.CardPurpose && Session.Project.World is { } world) world.SelectedCardId = request.MaterialId;
        var target = VideoDevelopment.ForRequest(Session.Project, request);
        if (VideoTranslationProtocol.IsTranslation(request))
        {
            VideoTranslationHistory.Apply(target, request, answer);
            VideoTranslationHistory.Capture(target);
            target.TranslationRevisionId = request.Id; target.EnglishText = answer.Text;
        }
        else
        {
            VideoConceptHistory.Apply(target, request, answer);
            VideoConceptHistory.Capture(target);
            target.IdeaRevisionId = request.Id; target.IdeaText = answer.Text;
            target.ContextRevisionId = request.ContextRevisionId; target.ContextText = request.ContextText;
        }
        Session.Project.CurrentStep = VideoDevelopment.StepFor(request);
        Session.Changed();
        if (!Checkpoint()) return false;
        _statusKey = "AiReady"; Render(); return true;
    }

    private bool ResultSelected(VideoConceptRequest request)
    {
        var target = VideoDevelopment.ForRequest(Session.Project, request);
        return request.Id == (VideoTranslationProtocol.IsTranslation(request) ? target.TranslationRevisionId : target.IdeaRevisionId);
    }

    public void RefreshConceptOperationState()
    {
        var state = PendingConceptState;
        if (state is null || state.Phase == _lastConceptPhase) return;
        _lastConceptPhase = state.Phase;
        _worldDialog?.SetPaused(state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown);
        _statusKey = state.RequiresDecision ? (_statusKey is "" or "AiWorking" or "AiPausing" ? "AiError" : _statusKey)
            : state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown
            ? "AiPaused" : state.Phase == BackgroundOperationPhase.Pausing ? "AiPausing" : "AiWorking";
        if (state.Kind == VideoWorld.Kind && state.Phase == BackgroundOperationPhase.Running) _statusKey = "WorldWorking";
        Render();
    }

    private async Task PauseResumeConceptAsync()
    {
        var controller = ApplicationBackgroundOperations.Current; var state = PendingConceptState;
        if (controller is null || state is null) return;
        try
        {
            if (state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing)
            { if (controller.State?.Id == state.Id) await controller.PauseAsync(); }
            else await controller.ResumeAsync(state.Id, ApplicationBackgroundOperations.ExitToken);
        }
        catch (Exception error) { ConceptError(error); RefreshStatus(); }
    }

    private void CancelConcept()
    {
        _shotsCancel?.Cancel();
        _worldCancel?.Cancel();
        if (_conceptCancel is not null) _conceptCancel.Cancel();
        else if (PendingConceptState is { } state && ApplicationBackgroundOperations.Current is { } controller && controller.State?.Id == state.Id)
        { controller.DiscardPending(state.Id); _statusKey = "AiCanceled"; Render(); }
    }

    private void ConceptError(Exception error)
    {
        ComponentAuditFailures.Report(error);
        OwnedProcessRegistry.Log("concept_assistance_failed", "VideoPreparation", detail: error.GetType().Name);
        _statusKey = error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal) ? error.Message[10..] : "AiError";
        if (T(_statusKey).StartsWith("VideoPrep.", StringComparison.Ordinal)) _statusKey = "AiError";
    }
}
