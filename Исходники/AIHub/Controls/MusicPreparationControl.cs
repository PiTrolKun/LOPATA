using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed partial class MusicPreparationControl : UserControl, IDisposable
{
    private readonly IMusicPreparation _preparation;
    private readonly Func<MusicWorkspaceControl> _createWorkspace;
    private Func<string, string> _l = key => key;
    private string _root = "", _statusKey = "Music.Preparation.Checking";
    private IReadOnlyList<ManagedModelArtifactCard> _cards = [];
    private CancellationTokenSource? _cancel;
    private bool _ready, _disposed;
    private TextBlock? _status;
    private ProgressBar? _progress;
    private MusicWorkspaceControl? _workspace;
    private MusicProjectSnapshot? _pendingExample;
    private readonly MusicModelSelectionControl _models = new();
    public bool IsModelSelection { get; private set; } = true;
    private string _outputFolder = "";
    private Action<string> _saveOutputFolder = _ => { };
    public bool IsBusy { get; private set; }
    public bool IsWorkspace { get; private set; }
    private string _variation = MusicStudioRuntime.Variation;
    private bool _applySelection;
    private string? _preparedHardware;
    public event Action? WorkspaceChanged;
    public event Action<MusicWorkspaceControl>? PoetryRequested;
    public bool CanChangeModel => IsWorkspace && !IsBusy && _workspace?.Session.CanChangeModel == true;
    public void ShowModelMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var id in new[] { MusicStudioRuntime.Variation, MusicAceCatalog.Variation, MusicDiffRhythmCatalog.Variation, MusicHeartMuLaCatalog.Variation }) {
            var cards = MusicModelVariants.Cards(_root, id);
            var installed = cards.All(c => c.Files.All(f => System.IO.File.Exists(System.IO.Path.Combine(c.InstallDirectory, f.RelativePath))));
            if (!installed || id == MusicStudioRuntime.Variation && !MusicStudioRuntime.Available) continue;
            var item = new MenuItem { Header = MusicModelVariants.Name(id), IsCheckable = true,
                IsChecked = _workspace?.Generation.Variation == id, IsEnabled = installed && CanChangeModel };
            item.Click += async (_, _) => {
                var previous = _variation; IsBusy = true; WorkspaceChanged?.Invoke();
                try {
                    _preparation.Variation = id;
                    var checkedCards = await _preparation.CheckAsync(_root, null, ApplicationBackgroundOperations.ExitToken);
                    if (!checkedCards.All(c => c.Status == ManagedModelStatuses.Installed)) throw new System.IO.IOException(_l("Music.Preparation.Missing"));
                    var hardware = id == MusicModelVariants.Bf16 ? await MusicBf16Worker.ProbeAsync(_root, ApplicationBackgroundOperations.ExitToken,
                        line => _workspace!.Status.AppendLog(line)) : null;
                    _workspace!.Projects.SwitchModel(id, hardware); _variation = id; _cards = checkedCards;
                }
                catch (Exception error) { _preparation.Variation = previous; _workspace?.Status.AppendLog(error.Message); }
                finally { IsBusy = false; WorkspaceChanged?.Invoke(); }
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = _l("Music.Models.All"), IsEnabled = CanChangeModel };
        all.Click += (_, _) => { IsWorkspace = false; IsModelSelection = true; Render(); };
        menu.Items.Add(all);
        anchor.ContextMenu = menu; menu.IsOpen = true;
    }
    public bool CanContinue => _ready && !IsModelSelection && !IsBusy && !_disposed;
    public MusicPreparationControl() : this(new MusicPreparationService()) { }
    public MusicPreparationControl(IMusicPreparation preparation) : this(preparation, () => new MusicWorkspaceControl()) { }
    internal MusicPreparationControl(IMusicPreparation preparation, Func<MusicWorkspaceControl> createWorkspace)
    {
        _preparation = preparation;
        _createWorkspace = createWorkspace;
        _preparation.Variation = _variation;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Music.Page");
        _models.OpenRequested += SelectModelAsync;
        _models.CanApplyExample = () => !IsBusy && !PendingMusicOperation && _workspace?.Session.HasPendingOrRunning != true;
        _models.ExampleRequested += ApplyExampleAsync;
        _models.CanRemoveModel = variation => CanRemoveModel?.Invoke(variation) == true;
        _models.RemoveRequested += variation => RemoveModelRequested?.Invoke(variation) ?? Task.CompletedTask;
        IsVisibleChanged += (_, _) => { if (Visibility != Visibility.Visible) { _workspace?.Player.Pause(); _models.PauseExamples(); } };
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
    public bool UsesArtifact(string id) => (IsBusy && !_removingModel || PendingMusicOperation || _workspace?.Session.HasPendingOrRunning == true) &&
        (MusicComponentCatalog.ComponentIds.Contains(id) || MusicModelVariants.Components(MusicModelVariants.Bf16).Contains(id)
            || MusicModelVariants.Components(MusicStudioRuntime.Variation).Contains(id) || MusicAceCatalog.Components.Contains(id) || MusicDiffRhythmCatalog.Components.Contains(id) || MusicHeartMuLaCatalog.Components.Contains(id));
    public Task OpenAsync() { if (IsBusy) return Task.CompletedTask; if (ShowPendingWorkspace()) return Task.CompletedTask; IsWorkspace = false; IsModelSelection = true; Render(); return Task.CompletedTask; }
    public Task SelectModelAsync(string modelId, string variantId)
    {
        if (_disposed || IsBusy || !MusicModelSelectionCatalog.CanOpen(modelId, variantId)) return Task.CompletedTask;
        if (ShowPendingWorkspace()) return Task.CompletedTask;
        _pendingExample = null;
        _variation = MusicModelVariants.Normalize(variantId); _preparation.Variation = _variation;
        _applySelection = true;
        IsModelSelection = false; return CheckAsync();
    }
    private async Task ApplyExampleAsync(MusicProjectSnapshot snapshot)
    {
        if (_disposed || IsBusy || _workspace?.Session.HasPendingOrRunning == true) return;
        snapshot.Validate(); _pendingExample = snapshot.Snapshot(); IsModelSelection = false;
        _variation = MusicModelVariants.WorkspaceVariation(snapshot.Variation); _preparation.Variation = _variation;
        await CheckAsync();
        if (CanContinue) await ContinueAsync();
    }
    private void ShowWorkspace()
    { if (_cards.Count == 0) _cards = MusicModelVariants.Cards(_root, _variation); IsModelSelection = false; IsWorkspace = true; Render(); }
    private void EnsureWorkspace()
    {
        if (_workspace is not null) return;
        _workspace = _createWorkspace();
        _workspace.Generation.OpenPoetry = () => PoetryRequested?.Invoke(_workspace);
        _workspace.Generation.OptionsChanged += () => { _variation = _workspace.Generation.Variation; WorkspaceChanged?.Invoke(); };
        _workspace.Session.StateChanged += () => WorkspaceChanged?.Invoke();
    }
    private bool ShowPendingWorkspace()
    {
        if (ApplicationBackgroundOperations.Current is not { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind }) return false;
        try {
            EnsureWorkspace();
            _pendingExample = null; _applySelection = false; _preparedHardware = null;
            _workspace!.Localize(_l);
            _workspace.Session.RestorePendingJob();
            _variation = _workspace.Generation.Variation; _preparation.Variation = _variation;
            ShowWorkspace();
        }
        catch (Exception error) { ShowPreparationError(error); Render(); }
        return true;
    }
    private void ShowPreparationError(Exception error)
    {
        IsWorkspace = false; IsModelSelection = false; _ready = false;
        _statusKey = "Music.Error"; ErrorText = error.Message;
        OwnedProcessRegistry.Log("music_ui_preparation_failed", "Music.Preparation", detail: error.ToString());
    }
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
        if (!IsWorkspace) { if (IsModelSelection) return false; _pendingExample = null; IsModelSelection = true; Render(); return true; }
        IsWorkspace = false; Render(); return true;
    }

    private async Task RunAsync(bool acknowledge, bool download, bool open)
    {
        if (_disposed || IsBusy) return;
        if (open && ShowPendingWorkspace()) return;
        IsWorkspace = false; IsModelSelection = false; _ready = false;
        if (string.IsNullOrWhiteSpace(_root)) { _statusKey = "Music.StorageRequired"; Render(); return; }
        IsBusy = true; _cancel = new();
        _preparedHardware = null;
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
            var filesWatch = System.Diagnostics.Stopwatch.StartNew();
            // External pipelines delegate complete verification to their worker.
            // Previous status is a UI hint only; the worker re-hashes every component before execution.
            if (!(open && MusicModelVariants.ExternalPipeline(_variation))) _cards = acknowledge
                ? await _preparation.PrepareAsync(_root, download, progress, operation.Token)
                : await _preparation.CheckAsync(_root, progress, operation.Token);
            else if (_progress is not null) _progress.IsIndeterminate = true;
            OwnedProcessRegistry.Log("music_ui_preparation", "Music.Preparation", detail:
                $"variation={_variation}; stage={(open && MusicModelVariants.ExternalPipeline(_variation) ? "worker-verifies" : "files")}; elapsedMs={filesWatch.ElapsedMilliseconds}");
            operation.Token.ThrowIfCancellationRequested();
            _ready = _cards.Count == MusicModelVariants.Cards(_root, _variation).Count && _cards.All(c => c.Status == ManagedModelStatuses.Installed);
            _statusKey = _ready ? "Music.Preparation.Ready" : "Music.Preparation.Missing";
            if (open && _ready && _variation == MusicModelVariants.Bf16)
                _preparedHardware = await MusicBf16Worker.ProbeAsync(_root, operation.Token,
                    line => { if (_status is not null) _status.Text = line; });
            if (open && _ready && MusicHeartMuLaCatalog.IsHeart(_variation)) {
                var receipt = await MusicHeartMuLaWorker.ProbeAsync(_root, operation.Token, line => ReportExternalPreparation(line, operation));
                _preparedHardware = receipt.Hardware; _cards = receipt.Cards;
                _ready = _cards.Count == MusicHeartMuLaCatalog.Cards(_root).Count && _cards.All(c => c.Status == ManagedModelStatuses.Installed);
            }
            if (open && _ready && MusicDiffRhythmCatalog.IsDiff(_variation)) {
                var receipt = await MusicDiffRhythmWorker.ProbeAsync(_root, operation.Token, line => ReportExternalPreparation(line, operation));
                _preparedHardware = receipt.Hardware; _cards = receipt.Cards;
                _ready = _cards.Count == MusicDiffRhythmCatalog.Cards(_root).Count && _cards.All(c => c.Status == ManagedModelStatuses.Installed);
            }
            if (open && _ready && MusicAceCatalog.IsAce(_variation)) {
                var receipt = await MusicAceWorker.ProbeAsync(_root, operation.Token, line => ReportExternalPreparation(line, operation));
                _preparedHardware = receipt.Hardware; _cards = receipt.Cards;
                _ready = _cards.Count == MusicAceCatalog.Cards(_root).Count && _cards.All(c => c.Status == ManagedModelStatuses.Installed);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (open && _ready && !_disposed && !ShowPendingWorkspace()) {
                EnsureWorkspace();
                _workspace!.ConfigureOutput(_outputFolder, _saveOutputFolder);
                _workspace.Localize(_l);
                _workspace.ConfigureGeneration(_root, _l);
                if (_applySelection) {
                    _workspace.Projects.SwitchModel(_preparation.Variation, _preparedHardware);
                    _variation = _workspace.Generation.Variation; _applySelection = false; _preparedHardware = null;
                }
                if (_pendingExample is { } example) { _workspace.Projects.ApplyExample(example); _pendingExample = null; }
                IsWorkspace = true;
            }
        }
        catch (OperationCanceledException) { _ready = false; _statusKey = "Music.Canceled"; }
        catch (Exception e)
        {
            ShowPreparationError(e);
        }
        finally
        {
            IsBusy = false; operation.Dispose(); _cancel = null;
            if (_disposed) _preparation.Dispose();
            else Render();
        }
    }
    private string ErrorText { get; set; } = "";

    private void ReportExternalPreparation(string line, CancellationTokenSource operation)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ReportExternalPreparation(line, operation)); return; }
        if (_disposed || !IsBusy || _cancel != operation) return;
        var key = line.Contains("source", StringComparison.OrdinalIgnoreCase) ? "Source"
            : line.Contains("Importing", StringComparison.Ordinal) ? "Api"
            : line.Contains("hardware", StringComparison.OrdinalIgnoreCase) || line.Contains("Python profile", StringComparison.Ordinal) ? "Hardware"
            : line.Contains("librar", StringComparison.OrdinalIgnoreCase) ? "Libraries" : "Files";
        var prefix = MusicHeartMuLaCatalog.IsHeart(_variation) ? "Music.HeartMuLa.Prepare." : MusicDiffRhythmCatalog.IsDiff(_variation) ? "Music.Diff.Prepare." : "Music.Ace.Prepare.";
        if (_status is not null) _status.Text = _l(prefix + key) + "\n" + line;
        if (_progress is not null) {
            var count = System.Text.RegularExpressions.Regex.Match(line, @" · (\d+)/(\d+)$");
            _progress.IsIndeterminate = !count.Success;
            if (count.Success) _progress.Value = 100d * double.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                / double.Parse(count.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void Render()
    {
        if (_disposed) return;
        WorkspaceChanged?.Invoke();
        if (IsModelSelection) { _models.Localize(_l); Content = _models; return; }
        if (IsWorkspace)
        {
            EnsureWorkspace();
            _workspace!.ConfigureOutput(_outputFolder, _saveOutputFolder);
            _workspace.Localize(_l);
            _workspace.ConfigureGeneration(_root, _l);
            Content = _workspace;
            var model = MusicModelVariants.Cards(_root, _workspace.Generation.Variation).Single(c => c.ModelArtifactId == MusicModelVariants.Weights(_workspace.Generation.Variation));
            if (!MusicModelVariants.ExternalPipeline(_workspace.Generation.Variation)) _ = _workspace.Editor.LoadTokenizerAsync(System.IO.Path.Combine(model.InstallDirectory,
                _workspace.Generation.Variation == MusicModelVariants.Bf16 ? "qwen.tiktoken" : model.Files.Single().RelativePath));
            return;
        }
        var panel = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24) };
        panel.Children.Add(Text(_l(MusicHeartMuLaCatalog.IsHeart(_variation) ? "Music.HeartMuLa.PreparationTitle" : MusicDiffRhythmCatalog.IsDiff(_variation) ? "Music.DiffRhythm.PreparationTitle" : MusicAceCatalog.IsAce(_variation) ? "Music.Ace.PreparationTitle" : "Music.Preparation.Title"), true));
        panel.Children.Add(Text(_l("Music.Preparation.Hint")));
        foreach (var card in MusicModelVariants.Cards(_root, _variation))
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
        _disposed = true; _cancel?.Cancel(); _models.Dispose(); _workspace?.Dispose();
        if (!IsBusy) _preparation.Dispose();
    }
}
