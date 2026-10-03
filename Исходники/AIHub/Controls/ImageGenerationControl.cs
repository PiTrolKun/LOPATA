using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl : UserControl
{
    private readonly ImageGenerationInstallation _installation;
    private readonly ImageGenerationRuntime _runtime;
    private Func<string, string> _l = key => key;
    private string _languageCode = "ru";
    private string _modelsRoot = "", _resultsRoot = "", _modelId = "z-image", _prompt = "", _statusText = "";
    private string? _sessionDirectory;
    private string? _statusKey;
    private static readonly string[] StatusKeys = ["Ready", "Generating", "Paused", "Canceled", "Verifying", "FolderRequired", "Preparation.Ready", "Preparation.NeedsDownload"];
    private int _page, _width = 2048, _height = 2048, _count = 1;
    private bool _busy;
    private CancellationTokenSource? _cancel;
    private TextBlock? _status;
    private TextBox? _promptBox;
    private ImageGenerationSettings _settings = new();
    private Action? _saveSettings;
    public event Action? WorkspaceChanged;
    public bool IsChat => _page == 2;
    public bool CanChangeModel => !_busy && !HasPendingGeneration();
    public void ConfigureOutput(ImageGenerationSettings settings, Action save) { _settings = settings; _saveSettings = save; }
    public ImageGenerationControl() : this(new(), new()) { }
    public ImageGenerationControl(ImageGenerationInstallation installation, ImageGenerationRuntime runtime)
    {
        _installation = installation; _runtime = runtime;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Generation.Page");
    }
    public void Configure(Func<string, string> localize, StorageSettings storage, int connections, string language = "ru")
    {
        _l = localize; _languageCode = language;
        if (_statusKey is not null) _statusText = L(_statusKey);
        var storageChanged = false;
        if (!_busy)
        {
            var modelsRoot = storage.Models.Locations.Select(l => l.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
            storageChanged = !string.Equals(_modelsRoot, modelsRoot, StringComparison.OrdinalIgnoreCase);
            if (storageChanged) { _preparationReady = _preparationChecked = false; _preparationCards = []; }
            _modelsRoot = modelsRoot;
            _resultsRoot = storage.Results.Locations.Select(l => l.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? AppDataPaths.BaseDirectory;
        }
        _installation.MaximumParallelConnections = connections;
        Render();
        if (_page == 1 && storageChanged) _ = CheckPreparationAsync();
    }
    public void Localize(Func<string, string> localize, string language = "ru") { _l = localize; _languageCode = language; if (_statusKey is not null) _statusText = L(_statusKey); Render(); }
    public void DownloadConnections(int value) => _installation.MaximumParallelConnections = value;
    public bool UsesArtifact(string id) => _busy && ImageGenerationCatalog.Get(_modelId).Components.Contains(id);
    public void RefreshBackgroundStatus()
    {
        if (ApplicationBackgroundOperations.Current?.State is { Kind: ImageGenerationCatalog.BackgroundKind } state && _busy)
        {
            if (state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting) Status(L("Paused"));
            else if (state.Phase == BackgroundOperationPhase.Running) Status(L("Generating"));
        }
        if (_page == 2)
        {
            if (_operationProgress is not null) _operationProgress.IsIndeterminate = _busy && ApplicationBackgroundOperations.Current?.State?.Phase == BackgroundOperationPhase.Running;
            if (_operationStatus is not null) _operationStatus.Text = _statusText;
            if (_pauseButton is not null) _pauseButton.IsEnabled = HasPendingGeneration();
            if (_stopButton is not null) _stopButton.IsEnabled = _busy || HasPendingGeneration();
            WorkspaceChanged?.Invoke();
        }
    }
    private string L(string key) => _l("Generation." + key);
    private TextBlock Text(string value, bool title = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = title ? 23 : 16,
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 8, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    private Button Button(string label, Func<Task> action, string id, bool enabled = true)
    {
        var button = new Button { Content = L(label), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 4), IsEnabled = enabled };
        AutomationProperties.SetAutomationId(button, "Generation." + id);
        button.Click += async (_, _) => { try { await action(); } catch (Exception e) { Error(e); } };
        return button;
    }
    private void Status(string value) { _statusKey = StatusKeys.FirstOrDefault(key => L(key) == value); _statusText = value; if (_status is not null) _status.Text = value; }
    private void Error(Exception error) => Status((error.Message.StartsWith("Generation.", StringComparison.Ordinal) ? _l(error.Message) : L("Error") + " " + error.Message));
    private void Render()
    {
        if (_promptBox is not null) _prompt = _promptBox.Text;
        _promptBox = null;
        WorkspaceChanged?.Invoke();
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Text(L("Title"), true));
        _status = Text(_statusText);
        if (_page == 2) { RenderChat(); return; }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (_page == 0) { RenderSelection(panel); panel.Children.Add(_status); }
        else RenderPreparation();
    }
    private async Task<bool> PrepareAsync(bool download)
    {
        if (_busy) return false;
        var prepared = false;
        var wasReady = _preparationReady;
        _busy = true; _cancel = new(); _preparationReady = false; Status(L("Verifying")); Render();
        try
        {
            _preparationCards = await _installation.PrepareAsync(_modelsRoot, _modelId, download, PreparationProgress(), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            _preparationReady = true; _preparationChecked = true; Status(L("Preparation.Ready"));
            prepared = true;
        }
        catch (OperationCanceledException) { _preparationReady = wasReady; Status(L("Canceled")); }
        catch (Exception e) { Error(e); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; Render(); }
        return prepared;
    }
    private Task CancelAsync()
    {
        _cancel?.Cancel();
        if (ApplicationBackgroundOperations.Current is { IsRunning: false, State.Kind: ImageGenerationCatalog.BackgroundKind } controller)
            controller.DiscardPending();
        return Task.CompletedTask;
    }
    public bool GoBack()
    {
        if (_page == 0) return false;
        if (_page == 1) _cancel?.Cancel();
        _page = 0; Render(); return true; // Navigation preserves running generation work.
    }
    public async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<ImageGenerationRequest>() ?? throw new InvalidDataException("Invalid generation checkpoint.");
        if (!ImageGenerationCatalog.IsAvailable(request.ModelId)) throw new InvalidOperationException("Generation.ModelUnavailable");
        _sessionDirectory = request.SessionDirectory; _modelId = request.ModelId; _modelsRoot = request.ModelsRoot;
        _width = request.Width; _height = request.Height; _page = 2;
        await GenerateAsync(request, token, state);
    }
    public void Restore(string directory)
    {
        if (!string.Equals(_sessionDirectory, directory, StringComparison.OrdinalIgnoreCase)) _hiddenTurns.Clear();
        _selectedTurnId = null;
        _selectedResultIndex = int.MaxValue;
        var session = ImageGenerationSessionStore.Load(directory);
        _sessionDirectory = directory;
        if (session.Turns.LastOrDefault(t => ImageGenerationCatalog.IsAvailable(t.Request.ModelId)) is { } last)
        { _modelId = last.Request.ModelId; _modelsRoot = last.Request.ModelsRoot; _width = last.Request.Width; _height = last.Request.Height; }
        else
        { _modelId = "z-image"; var model = ImageGenerationCatalog.Get(_modelId); _width = model.DefaultWidth; _height = model.DefaultHeight; }
        _page = 2; Render();
    }
    public void DisposeRuntime() { _cancel?.Cancel(); _installation.Dispose(); }
}
