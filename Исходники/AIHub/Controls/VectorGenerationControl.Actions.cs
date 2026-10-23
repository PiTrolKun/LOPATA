using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl
{
    public bool UsesArtifact(string id) => _busy && id == VectorModels.ArtifactId(_modelId);
    private static bool IsVector(string kind) => kind is VectorStore.BackgroundKind or VectorPreparation.BackgroundKind;
    private static bool HasPending() => ApplicationBackgroundOperations.Current is { HasPending: true, State: { } state } && IsVector(state.Kind);
    private async Task SendAsync()
    {
        if (_busy || HasPending() || string.IsNullOrWhiteSpace(_draft)) return;
        var request = new VectorRequest(Guid.NewGuid().ToString("N"), _draft, _outputRoot, _author, DateTimeOffset.UtcNow,
            _modelId, System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue), _session.Id, _options);
        request.Options!.Validate();
        VectorStore.Save(new(request)); _session.Add(request); _selectedId = request.Id; _draft = "";
        await RunAsync(request, CancellationToken.None);
    }
    private async Task RunAsync(VectorRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_busy) throw new InvalidOperationException("Vector.Error.Busy");
        _modelId = VectorModels.Validate(request.ModelId);
        _page = 2;
        var live = BeginLive(request);
        _statusDetail = ""; _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); _statusKey = "Generating"; Render();
        try
        {
            await ApplicationBackgroundOperations.RunAsync(VectorStore.BackgroundKind, L("Title"), request.Id, request,
                async attempt =>
                {
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    try { return await _work.RunAsync(request, Progress, attempt, live); }
                    catch (InvalidDataException error) when (VectorWork.IsTerminalContentError(error.Message))
                    { throw new BackgroundOperationFailedException(error.Message); }
                }, _cancel.Token, restored);
            _statusKey = "Ready";
        }
        catch (OperationCanceledException) { _statusKey = "Canceled"; }
        catch (Exception error) { _statusKey = "Error"; ShowError(error); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; EndLive(); Render(); }
    }
    private void Progress(string key, int count)
    {
        _liveState?.SetStage(key);
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgress) < 150) return;
        Interlocked.Exchange(ref _lastProgress, now);
        _ = Dispatcher.BeginInvoke(() => { if (!_busy) return; _statusKey = key; if (_status is not null) _status.Text = L(key) + (count > 0 ? " · " + count.ToString("N0") + " " + L("Characters") : ""); });
    }
    public void RefreshBackgroundStatus()
    {
        if (ApplicationBackgroundOperations.Current?.State is { } state && IsVector(state.Kind) && _busy)
        {
            if (state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting) _statusKey = "Paused";
            else if (state.Phase == BackgroundOperationPhase.Running && _statusKey == "Paused")
                _statusKey = state.Kind == VectorPreparation.BackgroundKind ? "Preparing" : "Generating";
            if (_status is not null) _status.Text = L(_statusKey);
        }
    }
    public Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Kind == VectorPreparation.BackgroundKind)
        {
            _page = 1;
            _modelId = VectorModels.RestorePreparationModel(state.Input);
            var download = !state.Input.TryGetProperty("Download", out var value) || value.GetBoolean();
            return PrepareAsync(token, state, download);
        }
        var request = state.Input.Deserialize<VectorRequest>() ?? throw new InvalidDataException("Vector.Error.Job");
        _session.Restore(request);
        _selectedId = request.Id; return RunAsync(request, token, state);
    }
    public void ViewResult(string id)
    {
        var item = VectorStore.Load(id); _modelId = VectorModels.Validate(item.Request.ModelId);
        _session.Restore(item.Request); _page = 2; _selectedId = id; Render();
    }
    private async Task RetryAsync(VectorResult item)
    {
        if (_busy || HasPending()) return;
        await RunAsync(item.Request, CancellationToken.None);
    }
    private async Task PrepareAsync() => await PrepareAsync(CancellationToken.None, null, true);
    private async Task PrepareAsync(CancellationToken token, BackgroundOperationState? restored, bool download)
    {
        if (_busy || restored is null && HasPending()) return;
        _preparationState = null;
        _statusDetail = ""; _busy = true; _statusKey = "Preparing"; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); Render();
        try
        {
            await ApplicationBackgroundOperations.RunAsync(VectorPreparation.BackgroundKind, L("Preparation.Title"), null, new { Download = download, ModelId = _modelId },
                ct => VectorPreparation.PrepareAsync(_modelId, download, PreparationProgress(), ct), _cancel.Token, restored);
            _preparationState = new(true, true); _statusKey = PreparationReadyKey();
        }
        catch (OperationCanceledException) { _statusKey = "Canceled"; }
        catch (Exception error) { _statusKey = "Error"; ShowError(error); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; Render(); }
    }
    private static Task PauseResumeAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State is not { } state || !IsVector(state.Kind)) return Task.CompletedTask;
        return state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing ? controller.PauseAsync() : controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
    }
    private async Task CancelAsync()
    {
        _cancel?.Cancel();
        var controller = ApplicationBackgroundOperations.Current;
        if (HasPending() && controller is not null)
        {
            var id = controller.State!.Id;
            await controller.PauseAsync();
            await controller.WaitForIdleAsync(id);
            controller.DiscardPending(id);
        }
    }
    private string TranslateError(string error) => error.StartsWith("Vector.", StringComparison.Ordinal) ? _l(error) : L("Error") + " " + error;
    private void ShowError(Exception error)
    {
        ComponentAuditFailures.Report(error);
        var text = error is ImageAnalysisContextExhaustedException ? L("Error.Context")
            : error is FileNotFoundException ? L("Error.ModelMissing") : TranslateError(error.Message);
        _statusDetail = text; if (_status is not null) _status.Text = text;
    }
    private Task SaveSvg(VectorResult item)
    {
        var source = VectorStore.FilePath(item.Request.Id, "image.svg");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SVG (*.svg)|*.svg", DefaultExt = ".svg", FileName = Path.GetFileName(item.ExportPath ?? "Vector.svg") };
        if (dialog.ShowDialog() == true && !Path.GetFullPath(source).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) File.Copy(source, dialog.FileName, true);
        return Task.CompletedTask;
    }
    private static Task CopySvg(VectorResult item)
    {
        var path = VectorStore.FilePath(item.Request.Id, "image.svg");
        Clipboard.SetFileDropList(new StringCollection { path }); return Task.CompletedTask;
    }
    private static Task ShowFolder(VectorResult item)
    {
        var path = item.ExportPath ?? VectorStore.FilePath(item.Request.Id, "image.svg");
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = true }); return Task.CompletedTask;
    }
    private void OpenLarge(VectorResult item)
    {
        var path = VectorStore.FilePath(item.Request.Id, "preview.png"); if (!File.Exists(path)) return;
        var bitmap = ReadImage(path);
        var window = new Window { Title = L("Title"), Owner = Window.GetWindow(this), Width = Math.Min(1300, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(1000, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new VectorImageViewport(PreviewSurface(new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform }), L("ZoomHint"),
                () => File.ReadAllText(VectorStore.FilePath(item.Request.Id, "image.svg")),
                (double)bitmap.PixelWidth / bitmap.PixelHeight, L("ZoomError")) };
        ApplyViewerResources(window); window.SetResourceReference(Window.BackgroundProperty, "PanelBrush"); window.Show();
    }
    private void ApplyViewerResources(Window window)
    {
        if (Window.GetWindow(this) is { } owner) window.Resources.MergedDictionaries.Add(owner.Resources);
        window.Resources.MergedDictionaries.Add(Resources);
    }
}
