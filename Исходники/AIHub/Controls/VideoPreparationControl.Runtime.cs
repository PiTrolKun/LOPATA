using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool _warming, _warmDeferred, _warmScheduled, _openingProject;
    private string _warmOperationId = "", _warmError = "";
    private CancellationTokenSource? _warmCancel;
    private TextBlock? _runtimeStatus;
    private StartupBusyControl? _warmOverlay;
    private IVideoConceptResident? Resident => _assistant as IVideoConceptResident;
    private bool ShotsOwnWarmup => _shotsStarting || _shotsBusy || Session.Project.CurrentStep == "shots" && Session.Project.Shots?.Attempted != true;
    private void BuildWarmupOverlay(Grid root)
    {
        _warmOverlay = null;
        if (!_warming) return;
        // Keep the existing pause/cancel controls and text inputs reachable.
        _warmOverlay = new StartupBusyControl { Message = T("WarmLoading"), IsHitTestVisible = false };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_warmOverlay, "VideoPrep.WarmupOverlay");
        Grid.SetRowSpan(_warmOverlay, root.RowDefinitions.Count);
        System.Windows.Controls.Panel.SetZIndex(_warmOverlay, 10);
        root.Children.Add(_warmOverlay);
        RefreshWarmupOverlay();
    }
    private void RefreshWarmupOverlay()
    {
        if (_warmOverlay is null) return;
        var state = ApplicationBackgroundOperations.Current?.State;
        var stopped = state?.Id == _warmOperationId && state.Phase is BackgroundOperationPhase.Paused
            or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled;
        _warmOverlay.Visibility = _warming && !stopped ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ScheduleWarmup()
    {
        if (_drawingStage || Session.Project.CurrentStep is "storyboard" or "package" or "finish" or "generator" || ShotsOwnWarmup || _openingProject || _warmScheduled || _warming || Resident is null) return;
        _warmScheduled = true;
        // A file-launch handler must finish loading its project before the warmup
        // marks the wizard busy or selects the default model for an empty project.
        Dispatcher.BeginInvoke(() =>
        {
            _warmScheduled = false;
            if (!_openingProject && IsLoaded && IsVisible) _ = WarmDefaultAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
    private void BuildRuntimeStatus(StackPanel top)
    {
        if (Session.Project.CurrentStep is "storyboard" or "package" or "finish" or "generator") return;
        if (Resident is null) return;
        var row = new WrapPanel(); _runtimeStatus = Text("WarmIdle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_runtimeStatus, "VideoPrep.RuntimeStatus"); row.Children.Add(_runtimeStatus);
        if (_warming)
        {
            row.Children.Add(Action("WarmPause", () => _ = PauseWarmAsync()));
            row.Children.Add(Action("WarmCancel", () => _warmCancel?.Cancel()));
        }
        top.Children.Add(row); RefreshRuntimeStatus();
    }
    public void RefreshRuntimeStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshRuntimeStatus); return; }
        RefreshWarmupOverlay();
        if (_runtimeStatus is not null)
        {
            var state = ApplicationBackgroundOperations.Current?.State;
            _runtimeStatus.Text = _warming ? T(state?.Id == _warmOperationId && state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting
                ? "WarmPaused" : "WarmWorking") : Resident?.RuntimeInfo is { } info
                ? string.Format(T("RuntimeReady"), VideoConceptModels.Name(info.ModelId), T("RuntimeMode." + info.Mode), info.Backend, info.Device, info.Layers)
                : T(ShotsOwnWarmup && _warmError == "WarmDeferred" ? "ShotsWorking" : _warmError.Length == 0 ? "WarmIdle" : _warmError);
        }
        if (!_drawingStage && !ShotsOwnWarmup && _warmDeferred && !_openingProject && IsVisible && !_warming && ApplicationBackgroundOperations.Current?.BlocksNewWork != true)
        { _warmDeferred = false; Dispatcher.BeginInvoke(() => _ = WarmDefaultAsync()); }
    }
    private async Task WarmDefaultAsync()
    {
        if (_drawingStage || Session.Project.CurrentStep is "storyboard" or "package" or "finish" or "generator" || ShotsOwnWarmup || ConceptBusy || Resident is null || _openingProject || _warming || !IsVisible) return;
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true) { _warmDeferred = true; _warmError = "WarmDeferred"; Render(); return; }
        _warming = true; _warmError = ""; Render();
        _warmCancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        try
        {
            if (_loadConceptChoices is null) return;
            _conceptChoices = await _loadConceptChoices(_warmCancel.Token);
            var id = Session.Project.LastAssistantModelId.Length != 0 ? Session.Project.LastAssistantModelId
                : ActiveText.AssistantModelId.Length != 0 ? ActiveText.AssistantModelId : VideoConceptModels.Default(_conceptChoices);
            var choice = _conceptChoices.FirstOrDefault(c => c.Id == id);
            if (choice?.Model is null) { _warmError = "AiNeedsPreparation"; return; }
            if (choice.Fits == false) { _warmError = "AiMemoryUnavailable"; return; }
            // Warming weights alone is not an edit to an empty or saved project.
            _warmOperationId = Guid.NewGuid().ToString("N");
            await VideoConceptWarmup.RunAsync(_warmOperationId, new(id, choice.Model.Path, EngineSetSelection.ActiveId),
                T("WarmOperation"), Session.ArchivePath, Resident, _warmCancel.Token);
            ApplicationBackgroundOperations.Current?.Acknowledge(_warmOperationId);
        }
        catch (OperationCanceledException) { _warmError = "WarmCanceled"; }
        catch (Exception e) { ConceptError(e); _warmError = "WarmFailed"; }
        finally { _warming = false; _warmCancel.Dispose(); _warmCancel = null; Render(); }
    }
    private async Task PauseWarmAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State?.Id != _warmOperationId) return;
        if (controller.State.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing) await controller.PauseAsync();
        else await controller.ResumeAsync(_warmOperationId, ApplicationBackgroundOperations.ExitToken);
    }
    public async Task ResumeWarmAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (Resident is null) throw new BackgroundOperationWaitingException("VideoPrep.AiUnavailable");
        var request = state.Input.Deserialize<VideoConceptModelRequest>() ?? throw new InvalidOperationException("VideoPrep.AiInvalidRequest");
        _warming = true; _warmDeferred = false; _warmOperationId = state.Id;
        _warmCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken); Render();
        try { await VideoConceptWarmup.RunAsync(state.Id, request, T("WarmOperation"), state.Project, Resident, _warmCancel.Token, state); }
        finally { _warming = false; _warmCancel.Dispose(); _warmCancel = null; Render(); }
    }
    public Task<LlamaGpuDecision> DecideGpuAsync(LlamaGpuAdmission admission, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        var cpu = admission.Reason == "cpu_fallback";
        var message = cpu ? T("GpuCpuQuestion") : string.Format(T(admission.Reason == "gpu_memory_busy" ? "GpuMemoryQuestion" : "GpuLoadQuestion"),
            admission.Device, admission.RequiredBytes / (1024d * 1024 * 1024), admission.FreeBytes / (1024d * 1024 * 1024));
        var choices = cpu ? new[] { ("GpuUseCpu", "cpu"), ("AiDialogCancel", "cancel") }
            : new[] { ("GpuRetry", "retry"), ("GpuHybrid", "hybrid"), ("AiDialogCancel", "cancel") };
        var dialog = new VideoPreparationDecisionWindow(Window.GetWindow(this), _localize, "GpuTitle", message, choices);
        BackgroundUserDecision.ShowDialog(dialog, this, token);
        if (dialog.Choice == "cancel") { _warmCancel?.Cancel(); _conceptCancel?.Cancel(); _worldCancel?.Cancel(); }
        return dialog.Choice switch { "retry" => LlamaGpuDecision.Retry, "hybrid" => LlamaGpuDecision.Hybrid, "cpu" => LlamaGpuDecision.Cpu, _ => LlamaGpuDecision.Cancel };
    }).Task;
}
