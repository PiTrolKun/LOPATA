using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoReferenceDrawingWindow? _drawingWindow;
    private IImageGenerationPreparation? _drawingInstallation;
    private ImageGenerationRuntime _drawingRuntime = new();
    private string _drawingModelsRoot = "";
    private Func<string> _drawingAuthor = () => "";
    private Action<VideoPreparationProject>? _manualDrawing;
    private bool _drawingBusy, _drawingStage, _drawingGenerating;
    private CancellationTokenSource? _drawingCancel;
    private bool CanEditDrawingPrompts => Session.Project.References?.Plan is { } plan && plan.Items.Any(i => i.Selected)
        && plan.Items.Where(i => i.Selected).All(i => i.DirectPrompt || VideoReferencePlans.Ready(Session.Project, i));
    public bool UsesDrawingArtifact(string id) => _drawingBusy && _drawingInstallation is not null
        && (id.StartsWith("runtime.sd.", StringComparison.Ordinal)
            || ImageGenerationCatalog.Get(VideoReferenceDrawings.Ensure(Session.Project).ModelId).Components.Contains(id));
    public void ConfigureReferenceDrawing(string modelsRoot, int connections, Func<string> author,
        Action<VideoPreparationProject> manual, IImageGenerationPreparation? installation = null, ImageGenerationRuntime? runtime = null)
    {
        _drawingModelsRoot = modelsRoot; _drawingAuthor = author; _manualDrawing = manual;
        _drawingInstallation ??= installation ?? new ImageGenerationInstallation();
        _drawingInstallation.MaximumParallelConnections = connections;
        if (runtime is not null) _drawingRuntime = runtime;
    }
    private void BuildReferenceDrawingEntry(StackPanel page, VideoReferencePlan plan, bool busy)
    {
        page.Children.Add(Text("RefDrawingReady", true));
        page.Children.Add(Text(plan.Mode == "manual" ? "RefManualImportHint" : "RefDrawingHint"));
        var open = Action(plan.Mode == "manual" ? "RefManualOpen" : "RefDrawingOpen", () => _ = OpenReferenceDrawingAsync());
        open.IsEnabled = _drawingWindow?.Project == Session.Project || !busy && (plan.Mode == "assisted" ? CanEditDrawingPrompts : VideoReferencePlans.Ready(Session.Project)); page.Children.Add(open);
        var import = Action("RefHaveFiles", () => { ReferenceChoice("import"); }); import.IsEnabled = !busy; page.Children.Add(import);
        if (plan.Mode == "assisted")
        {
            var complete = Action("RefDrawingComplete", () => _ = NextAsync());
            complete.IsEnabled = !busy && VideoReferenceDrawings.Ready(Session.Project); page.Children.Add(complete);
        }
    }
    public async Task OpenReferenceDrawingAsync()
    {
        if (_drawingWindow?.Project == Session.Project) { _drawingWindow.Show(); _drawingWindow.Activate(); return; }
        if (ConceptBusy || PendingConcept || !(Session.Project.References?.Plan?.Mode == "assisted" ? CanEditDrawingPrompts : VideoReferencePlans.Ready(Session.Project)) || !Save()) return;
        _drawingStage = true; _warmDeferred = false;
        _drawingBusy = true; Render();
        try
        {
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            if (Resident is not null) await Resident.RetireAsync();
        }
        catch (Exception e) { _statusKey = "RefDrawingFailed"; RefreshStatus(); ComponentAuditFailures.Report(e); return; }
        finally { _drawingBusy = false; Render(); }
        var p = Session.Project;
        if (VideoReferencePlans.Ensure(p).Mode == "manual") { _manualDrawing?.Invoke(p); return; }
        if (_drawingWindow?.Project != p) { _drawingWindow?.Close(); _drawingWindow = null; }
        if (_drawingWindow is null)
        {
            _drawingWindow = new(Window.GetWindow(this), _localize, p,
                () => { if (!ReferenceEquals(p, Session.Project)) return; Session.Changed(); Checkpoint(); Render(); },
                PrepareReferenceDrawingAsync, GenerateReferenceImageAsync, () => _drawingBusy,
                () => _ = ToggleReferenceDrawingPauseAsync(), () => _drawingCancel?.Cancel(), () => _drawingGenerating,
                () => FinishReferenceDrawing(p));
            _drawingWindow.Closed += (_, _) => { _drawingWindow = null; };
        }
        _drawingWindow.Show(); _drawingWindow.Activate();
        await PrepareReferenceDrawingAsync(false);
    }
    private bool FinishReferenceDrawing(VideoPreparationProject project)
    {
        if (_drawingBusy || !ReferenceEquals(project, Session.Project) || !VideoReferenceDrawings.Ready(project)) return false;
        Session.Changed(); if (!Checkpoint()) return false;
        _statusKey = "RefDrawingFinished"; Render(); return true;
    }
    private async Task PrepareReferenceDrawingAsync(bool download)
    {
        if (_drawingBusy || PendingConcept || _drawingInstallation is null) return;
        _drawingBusy = true; _drawingCancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _drawingWindow?.Refresh(T("RefDrawingPreparing")); Render();
        try
        {
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            if (Resident is not null) await Resident.RetireAsync();
            await _drawingInstallation.PrepareAsync(_drawingModelsRoot, VideoReferenceDrawings.Ensure(Session.Project).ModelId,
                download, null, _drawingCancel.Token);
            _drawingWindow?.Refresh(T("RefDrawingPrepared"));
        }
        catch (OperationCanceledException) { _drawingWindow?.Refresh(T("AiCanceled")); }
        catch (Exception e) { ComponentAuditFailures.Report(e); _drawingWindow?.Refresh(DrawingError(e)); }
        finally { _drawingBusy = false; _drawingCancel.Dispose(); _drawingCancel = null; _drawingWindow?.Refresh(); Render(); }
    }
    private string DrawingError(Exception e)
    {
        var key = e.Message.StartsWith("VideoPrep.") || e.Message.StartsWith("Generation.") ? e.Message : "VideoPrep.RefDrawingFailed";
        var text = _localize(key); return text == key ? T("RefDrawingFailed") : text;
    }
    private async Task GenerateReferenceImageAsync(VideoReferencePrompt item, string prompt, int width, int height)
    {
        if (ConceptBusy || PendingConcept || _drawingInstallation is null || _drawingWindow?.Project != Session.Project) return;
        try
        {
            var p = Session.Project;
            if (p.References!.Images.Count >= 512) throw new InvalidDataException("VideoPrep.RefLimit");
            var image = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), VideoReferenceDrawings.Ensure(p).ModelId,
                prompt, width, height, [Random.Shared.NextInt64(0, int.MaxValue)], _drawingModelsRoot,
                ImageGenerationSessionStore.Create(Path.Combine(Session.RecoveryRoot, "Drawing", p.Id)))
                { Metadata = new(_drawingAuthor(), VideoReferenceImageWork.Kind), SubmittedAt = DateTimeOffset.UtcNow, EngineSetId = EngineSetSelection.ActiveId };
            ImageGenerationCatalog.Validate(image);
            // A manually adjusted prompt is a new portable version before the operation is submitted.
            item.English = prompt; VideoReferencePlans.Capture(p); Session.Changed(); if (!Checkpoint()) return;
            var request = new VideoReferenceImageRequest(p.Id, Session.ArchivePath!, item.Id, VideoReferencePlans.Definition(item),
                item.CardId, item.Purpose, VideoReferences.Description(p, item.CardId) ?? throw new InvalidDataException("VideoPrep.RefMissingCard"), image);
            await RunReferenceImageAsync(request, default);
        }
        catch (Exception e) { _drawingWindow?.Refresh(DrawingError(e)); }
    }
    private async Task RunReferenceImageAsync(VideoReferenceImageRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_drawingInstallation is null) throw new InvalidOperationException("VideoPrep.AiUnavailable");
        var p = Session.Project; _drawingStage = true; _drawingBusy = true; _drawingGenerating = true;
        _drawingCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _drawingWindow?.Refresh(T("RefDrawingWorking")); Render();
        try
        {
            await VideoReferenceImageWork.RunAsync(request, T("RefDrawingOperation"), _drawingInstallation, _drawingRuntime,
                async (r, turn) =>
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (!ReferenceEquals(p, Session.Project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
                        await VideoReferenceImageWork.ApplyAsync(p, r, turn, Path.Combine(Session.RecoveryRoot, "Media", p.Id, "References"), _drawingCancel.Token);
                        Session.Changed(); if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
                    }).Task.Unwrap();
                }, _drawingCancel.Token, restored);
            _drawingWindow?.ShowVariant(request.Image.Id); _drawingWindow?.Refresh(T("RefDrawingGenerated"));
        }
        catch (OperationCanceledException) { _drawingWindow?.Refresh(T("AiCanceled")); }
        catch (Exception e) { _drawingWindow?.Refresh(DrawingError(e)); }
        finally { _drawingBusy = false; _drawingGenerating = false; _drawingCancel.Dispose(); _drawingCancel = null; _drawingWindow?.Refresh(); Render(); }
    }
    private async Task ToggleReferenceDrawingPauseAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State?.Kind == VideoReferenceImageWork.Kind)
        {
            if (controller.State.Phase == BackgroundOperationPhase.Paused) await controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
            else await controller.PauseAsync();
        }
    }
    public async Task ResumeReferenceImageAsync(BackgroundOperationState state, CancellationToken token)
    {
        var r = state.Input.Deserialize<VideoReferenceImageRequest>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoReferenceImageWork.Validate(r);
        if (!OpenPath(r.ProjectPath) || Session.Project.Id != r.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        await RunReferenceImageAsync(r, token, state);
    }
    public bool ShowReferenceImageResult(BackgroundOperationState state)
    {
        var r = state.Input.Deserialize<VideoReferenceImageRequest>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoReferenceImageWork.Validate(r);
        if (ConceptBusy || !OpenPath(r.ProjectPath) || Session.Project.Id != r.ProjectId) return false;
        Session.Project.CurrentStep = "references"; VideoReferences.Ensure(Session.Project).Choice = "create";
        VideoReferencePlans.Ensure(Session.Project).Page = "ready"; Render(); _ = OpenReferenceDrawingAsync(); return true;
    }
}
