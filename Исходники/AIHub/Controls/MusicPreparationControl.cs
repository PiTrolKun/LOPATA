using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class MusicPreparationControl : UserControl, IDisposable
{
    private readonly IMusicPreparation _preparation;
    private Func<string, string> _l = key => key;
    private string _root = "", _statusKey = "Music.Preparation.Checking";
    private IReadOnlyList<ManagedModelArtifactCard> _cards = [];
    private CancellationTokenSource? _cancel;
    private bool _ready, _disposed;
    private TextBlock? _status;
    private ProgressBar? _progress;
    private MusicWorkspaceControl? _workspace;
    private readonly MusicModelSelectionControl _models = new();
    public bool IsModelSelection { get; private set; } = true;
    private string _outputFolder = "";
    private Action<string> _saveOutputFolder = _ => { };
    public bool IsBusy { get; private set; }
    public bool IsWorkspace { get; private set; }
    public bool CanContinue => _ready && !IsModelSelection && !IsBusy && !_disposed;
    public MusicPreparationControl() : this(new MusicPreparationService()) { }
    public MusicPreparationControl(IMusicPreparation preparation)
    {
        _preparation = preparation;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Music.Page");
        _models.OpenRequested += SelectModelAsync;
        IsVisibleChanged += (_, _) => { if (Visibility != Visibility.Visible) _workspace?.Player.Pause(); };
    }

    public void Configure(Func<string, string> localize, StorageSettings storage, int connections)
    {
        _l = localize;
        var root = storage.Models.Locations.Select(p => p.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
        { _cancel?.Cancel(); _ready = false; IsWorkspace = false; _cards = []; _workspace?.Editor.ResetTokenizer(); }
        _root = root;
        DownloadConnections(connections);
        Render();
    }
    public void Localize(Func<string, string> localize) { _l = localize; Render(); }
    public void ConfigureOutput(string folder, Action<string> save)
    { _outputFolder = folder; _saveOutputFolder = save; _workspace?.ConfigureOutput(folder, save); }
    public void DownloadConnections(int connections) => _preparation.MaximumParallelConnections = connections;
    public bool UsesArtifact(string id) => (IsBusy || _workspace?.Session.HasPendingOrRunning == true) && MusicComponentCatalog.ComponentIds.Contains(id);
    public Task OpenAsync() { if (IsBusy) return Task.CompletedTask; if (_workspace?.Session.HasPendingOrRunning == true) { ShowWorkspace(); return Task.CompletedTask; } IsWorkspace = false; IsModelSelection = true; Render(); return Task.CompletedTask; }
    public Task SelectModelAsync(string modelId, string variantId)
    {
        if (_disposed || IsBusy || !MusicModelSelectionCatalog.CanOpen(modelId, variantId)) return Task.CompletedTask;
        IsModelSelection = false; return CheckAsync();
    }
    private void ShowWorkspace()
    { if (_cards.Count == 0) _cards = MusicComponentCatalog.CreateCards(_root); IsModelSelection = false; IsWorkspace = true; Render(); }
    public Task ResumeGenerationAsync(BackgroundOperationState state, CancellationToken token)
    { ShowWorkspace(); return _workspace!.Session.ResumeAsync(state, token); }
    public void ViewGenerationResult(string id)
    { ShowWorkspace(); _workspace!.Session.ViewResult(id); }
    public Task CheckAsync() => RunAsync(false, false, false);
    public Task DownloadAsync() => RunAsync(true, true, false);
    public Task ContinueAsync() => CanContinue ? RunAsync(true, false, true) : Task.CompletedTask;
    public bool GoBack()
    {
        if (IsBusy) { _cancel?.Cancel(); return true; }
        if (!IsWorkspace) { if (IsModelSelection) return false; IsModelSelection = true; Render(); return true; }
        IsWorkspace = false; Render(); return true;
    }

    private async Task RunAsync(bool acknowledge, bool download, bool open)
    {
        if (_disposed || IsBusy) return;
        IsWorkspace = false; IsModelSelection = false; _ready = false;
        if (string.IsNullOrWhiteSpace(_root)) { _statusKey = "Music.StorageRequired"; Render(); return; }
        IsBusy = true; _cancel = new();
        var operation = _cancel;
        _statusKey = "Music.Preparation.Checking"; Render();
        var progress = new Progress<ManagedModelDownloadProgress>(p =>
        {
            if (_disposed || _cancel != operation || !IsBusy) return;
            var stage = _l(p.Stage.StartsWith("verif", StringComparison.Ordinal) ? "Music.Verifying" : "Music.Downloading");
            if (_status is not null) _status.Text = $"{stage} · {p.FileName} · {p.DownloadedBytes / 1e9:0.00} / {p.TotalBytes / 1e9:0.00} GB";
            if (_progress is not null)
            { _progress.IsIndeterminate = p.TotalBytes <= 0; if (p.TotalBytes > 0) _progress.Value = 100d * p.DownloadedBytes / p.TotalBytes; }
        });
        try
        {
            _cards = acknowledge
                ? await _preparation.PrepareAsync(_root, download, progress, operation.Token)
                : await _preparation.CheckAsync(_root, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _ready = MusicComponentCatalog.IsComplete(_cards);
            _statusKey = _ready ? "Music.Preparation.Ready" : "Music.Preparation.Missing";
            IsWorkspace = open && _ready && !_disposed;
        }
        catch (OperationCanceledException) { _statusKey = "Music.Canceled"; }
        catch (Exception e)
        {
            _statusKey = "Music.Error";
            ErrorText = e.Message;
        }
        finally
        {
            IsBusy = false; operation.Dispose(); _cancel = null;
            if (_disposed) _preparation.Dispose();
            else Render();
        }
    }
    private string ErrorText { get; set; } = "";

    private void Render()
    {
        if (_disposed) return;
        if (IsModelSelection) { _models.Localize(_l); Content = _models; return; }
        if (IsWorkspace)
        {
            _workspace ??= new MusicWorkspaceControl();
            _workspace.ConfigureOutput(_outputFolder, _saveOutputFolder);
            _workspace.Localize(_l);
            _workspace.ConfigureGeneration(_root, _l);
            Content = _workspace;
            var model = _cards.Single(c => c.ModelArtifactId == MusicComponentCatalog.ModelId);
            _ = _workspace.Editor.LoadTokenizerAsync(System.IO.Path.Combine(model.InstallDirectory, model.Files.Single().RelativePath));
            return;
        }
        var panel = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24) };
        panel.Children.Add(Text(_l("Music.Preparation.Title"), true));
        panel.Children.Add(Text(_l("Music.Preparation.Hint")));
        foreach (var card in MusicComponentCatalog.CreateCards(_root))
        {
            var current = _cards.FirstOrDefault(c => c.ModelArtifactId == card.ModelArtifactId);
            panel.Children.Add(Text(card.DisplayName + " · " + $"{card.TotalBytes / 1e9:0.00} GB" + " — " +
                _l(current?.Status == ManagedModelStatuses.Installed ? "Music.Available" : "Music.Missing")));
        }
        panel.Children.Add(Text(_l("Music.Preparation.LicenseHint")));
        _status = Text(_l(_statusKey) + (_statusKey == "Music.Error" ? " " + ErrorText : ""));
        panel.Children.Add(_status);
        _progress = new ProgressBar { Height = 10, Maximum = 100, IsIndeterminate = IsBusy, Value = _ready ? 100 : 0, Margin = new Thickness(0, 10, 0, 14) };
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush"); panel.Children.Add(_progress);
        var actions = new WrapPanel();
        var canCheck = !IsBusy && !string.IsNullOrWhiteSpace(_root);
        if (!_ready) actions.Children.Add(ActionButton("Music.Download", DownloadAsync, canCheck));
        actions.Children.Add(ActionButton("Music.Verify", CheckAsync, canCheck));
        actions.Children.Add(ActionButton("Music.Continue", ContinueAsync, CanContinue));
        if (IsBusy) actions.Children.Add(ActionButton("Music.Cancel", () => { _cancel?.Cancel(); return Task.CompletedTask; }, true));
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private TextBlock Text(string value, bool title = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = title ? 23 : 16,
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 8, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    private Button ActionButton(string key, Func<Task> action, bool enabled)
    {
        var button = new Button { Content = _l(key), IsEnabled = enabled, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 8, 4) };
        AutomationProperties.SetAutomationId(button, key);
        button.Click += async (_, _) => await action();
        return button;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _cancel?.Cancel(); _workspace?.Dispose();
        if (!IsBusy) _preparation.Dispose();
    }
}
