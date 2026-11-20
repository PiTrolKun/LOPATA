using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AIHub.Models;
using AIHub.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ShapePath = System.Windows.Shapes.Path;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    internal const string WarmupKind = "image_generation.warmup";
    private ImageGenerationResident? _resident;
    private string? _standaloneModel;
    private string? _warmupOperation;
    private Task _retiringResident = Task.CompletedTask;
    private bool _warming, _loadingForGeneration, _warmOnIdle, _releaseWhenIdle;
    private TextBlock? _warmupLabel;
    private RotateTransform? _warmupRotation;
    private bool _warmupAnimating;

    internal void EnableModelWarmup()
    {
        if (_resident is not null) return;
        _resident = new();
        _runtime = new(new ImageGenerationNativeWorker(null, _resident, starting =>
        {
            void Update()
            {
                if (_loadingForGeneration == starting) return;
                _loadingForGeneration = starting;
                if (_page == 2) Render();
            }
            if (Dispatcher.CheckAccess()) Update();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Update);
        }));
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) TryWarmSelectedWhenIdle();
            else if (Visibility == Visibility.Collapsed && !ApplicationBackgroundOperations.PreserveHiddenWork)
                EndWarmupOnExit();
        };
    }

    internal Task ResumeWarmupAsync(BackgroundOperationState saved, CancellationToken token)
    {
        var request = saved.Input.Deserialize<ImageGenerationRequest>()
            ?? throw new InvalidOperationException("Generation.WarmFailed");
        if (request.Id != saved.Id) throw new InvalidOperationException("Generation.WarmFailed");
        ImageGenerationCatalog.Validate(request);
        _modelId = request.ModelId;
        (_width, _height) = (request.Width, request.Height);
        _sessionDirectory = request.SessionDirectory;
        _page = 2;
        return WarmSelectedModelAsync(null, request, saved, token);
    }

    private async Task WarmSelectedModelAsync(IReadOnlyList<ManagedModelArtifactCard>? cards = null,
        ImageGenerationRequest? request = null, BackgroundOperationState? saved = null, CancellationToken token = default)
    {
        if (_resident is not { } resident || _busy || _page != 2) return;
        if (saved is null && (HasPendingGeneration() || ApplicationBackgroundOperations.Current?.BlocksNewWork == true))
        { _warmOnIdle = true; return; }
        _warmOnIdle = false;
        request ??= new(Guid.NewGuid().ToString("N"), _modelId, "A grey square on a white background.", _width, _height,
            [1], _modelsRoot, _sessionDirectory!) { EngineSetId = EngineSetSelection.ActiveId };
        _busy = _warming = true;
        _releaseWhenIdle = false;
        _standaloneModel = null;
        _warmupOperation = request.Id;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _cancel = cancel;
        Status(L("Warming")); Render();
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            await _retiringResident;
            cancel.Token.ThrowIfCancellationRequested();
            saved ??= new() { Id = request.Id, Kind = WarmupKind, Title = L("Warming"), Project = request.SessionDirectory,
                Input = JsonSerializer.SerializeToElement(request) };
            var ready = await ApplicationBackgroundOperations.RunAsync(WarmupKind, L("Warming"), request.SessionDirectory,
                request, async attempt =>
                {
                    var prepared = cards ?? await _installation.PrepareAsync(request.ModelsRoot, request.ModelId, false, null, attempt);
                    await ApplicationBackgroundOperations.RetireModelsExceptAsync(resident);
                    try { return await _runtime.WarmAsync(request, prepared, attempt); }
                    catch (OperationCanceledException) when (attempt.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                    { throw new BackgroundOperationFailedException("Generation.WarmFailed", error); }
                }, cancel.Token, saved);
            ApplicationBackgroundOperations.Current?.Acknowledge(request.Id);
            if (!ready) _standaloneModel = request.ModelId;
            Status(L(ready ? "WarmReady" : "WarmUnavailable"));
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception error)
        {
            _standaloneModel = request.ModelId;
            OwnedProcessRegistry.Log("resident_warmup_failed", "ImageGeneration", detail: error.GetType().Name + ": " + error.Message);
            Error(error);
        }
        finally
        {
            _warmupOperation = null;
            _cancel = null;
            _busy = _warming = false;
            if (_releaseWhenIdle) EndWarmupOnExit();
            Render();
        }
    }

    private void EndWarmupOnExit()
    {
        _warmOnIdle = false;
        if (_resident is not { } resident) return;
        if (_busy)
        {
            _releaseWhenIdle = true;
            if (_warming)
            {
                _cancel?.Cancel();
                if (_warmupOperation is { } id) ApplicationBackgroundOperations.Current?.DiscardPending(id);
            }
            return;
        }
        _releaseWhenIdle = false;
        _retiringResident = RetireAsync();
        async Task RetireAsync()
        {
            try { await resident.RetireAsync(); }
            catch (Exception error) { OwnedProcessRegistry.Log("resident_retire_failed", "ImageGeneration", detail: error.Message); }
        }
    }

    private void AddWarmupIndicator(Grid workspace)
    {
        _warmupLabel = null;
        _warmupRotation = null;
        _warmupAnimating = false;
        if (!_warming && !_loadingForGeneration) return;
        var overlay = new Border { Padding = new Thickness(16) };
        overlay.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        AutomationProperties.SetAutomationId(overlay, "Generation.Warmup");
        AutomationProperties.SetName(overlay, L("Warming"));
        Grid.SetRowSpan(overlay, 2);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var circle = new Grid { Width = 108, Height = 108, HorizontalAlignment = HorizontalAlignment.Center };
        var track = new Ellipse { Margin = new Thickness(8), StrokeThickness = 8 };
        track.SetResourceReference(Shape.StrokeProperty, "LineBrush");
        var arc = new ShapePath { Data = Geometry.Parse("M54,8 A46,46 0 1 1 8,54"), StrokeThickness = 8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = _warmupRotation = new RotateTransform(0, 54, 54) };
        arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        circle.Children.Add(track); circle.Children.Add(arc); content.Children.Add(circle);
        _warmupLabel = Text("", true);
        _warmupLabel.TextAlignment = TextAlignment.Center;
        AutomationProperties.SetLiveSetting(_warmupLabel, AutomationLiveSetting.Polite);
        content.Children.Add(_warmupLabel);
        content.Children.Add(Button("Cancel", CancelAsync, "CancelWarmup"));
        if (ApplicationBackgroundOperations.Current is not null)
            content.Children.Add(Button("WarmPauseResume", PauseResumeAsync, "PauseWarmup"));
        overlay.Child = content;
        workspace.Children.Add(overlay);
        RefreshWarmupIndicator();
    }

    private void RefreshWarmupIndicator()
    {
        if (_warmupLabel is null || _warmupRotation is null) return;
        var waiting = ApplicationBackgroundOperations.Current?.State is { } state
            && (state.Id == _warmupOperation || _loadingForGeneration && state.Kind == ImageGenerationCatalog.BackgroundKind)
            && state.Phase is BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting;
        _warmupLabel.Text = L(waiting ? "Paused" : "Warming") + " · " + ImageGenerationCatalog.DisplayName(_modelId);
        if (_warmupAnimating == !waiting) return;
        _warmupAnimating = !waiting;
        _warmupRotation.BeginAnimation(RotateTransform.AngleProperty, waiting ? null :
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void TryWarmSelectedWhenIdle()
    {
        if (_warmOnIdle && IsVisible && _page == 2 && !_busy && ApplicationBackgroundOperations.Current?.BlocksNewWork != true)
            _ = WarmSelectedModelAsync();
    }
}
