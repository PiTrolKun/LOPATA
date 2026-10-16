using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls;

/// <summary>Owns draft history and visual state; the background service owns the calculation.</summary>
public sealed partial class MusicRhymeController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicRhymePreferences _preferences;
    private readonly Dictionary<string, List<MusicRhymeStep>> _drafts = [];
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _analysisCancel;
    private Func<string, string> _l = key => key;
    private StorageSettings? _storage;
    private Func<IMusicRhymeModel>? _models;
    private MusicRhymeOptions _options = new();
    private MusicRhymeAnalysis? _analysis;
    private MusicRhymeWindow? _window;
    private string? _active, _activeProject, _visual;
    private bool _blink, _disposed;
    public event Action? Changed;
    public string Color => _options.Color;
    public bool AutoAnalyze => _options.AutoAnalyze;
    public MusicRhymeOptions Options => _options.Snapshot();
    public bool SetCreativeMode(bool enabled) => _options.CreativeMode == enabled || ApplyOptions(_options with { CreativeMode = enabled });
    public IReadOnlyList<MusicPoetryModelChoice> ModelChoices => _storage is null ? [] : MusicRhymePreferences.Choices(_storage);
    public IReadOnlyList<MusicRhymeStep> History => HistoryFor(_view.Projects.Current.Id);
    public IReadOnlyList<MusicRhymeStep> HistoryFor(string project) => _drafts.GetValueOrDefault(project) ?? [];
    public MusicRhymeController(MusicWorkspaceControl view, MusicRhymePreferences? preferences = null)
    {
        _view = view; _preferences = preferences ?? MusicRhymePreferences.Default;
        try { _options = _preferences.Load(); } catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); }
        UpdateIntervals();
        view.TextActions.RhymeRequested += Click;
        view.TextActions.RhymeButton.ContextMenu = new();
        view.TextActions.RhymeButton.ContextMenuOpening += (_, args) => {
            args.Handled = true; Menu(); view.TextActions.RhymeButton.ContextMenu.IsOpen = true;
        };
        view.Editor.LyricsChanged += TextChanged;
        view.Editor.EditingFinished += EditingFinished;
        view.Editor.EditingStarted += EditingStarted;
        view.Projects.LyricsContextChanged += ProjectChanged;
        _pulse.Tick += (_, _) => { _blink = !_blink; ButtonState();
            var paused = IsPaused(_activeProject ?? "");
            if (paused != _lastPaused) { _lastPaused = paused; Changed?.Invoke(); } };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await CheckAfterEditingAsync(); };
        ButtonState();
    }
    public void Configure(StorageSettings storage, Func<IMusicRhymeModel> models) { _storage = storage; _models = models; }
    public void Localize(Func<string, string> l) { _l = l; ButtonState(); }
    public string Error(string message) => message.StartsWith("Music.", StringComparison.Ordinal) || message.StartsWith("RagBooks.", StringComparison.Ordinal) ? _l(message) : message;
    private void ProjectChanged()
    {
        _debounce.Stop(); _analysisCancel?.Cancel();
        var step = History.LastOrDefault(); _visual = step?.Request.Id;
        _analysis = step is not null && StillCurrent(step.Request) ? step.Analysis : null;
        _view.Editor.SetRhymeAnalysis(_analysis, _options.Color); Changed?.Invoke(); ButtonState();
    }
    private void TextChanged(object? sender, EventArgs args)
    {
        _debounce.Stop(); LaunchError = null;
        _analysisCancel?.Cancel(); _analysis = null; _view.Editor.SetRhymeAnalysis(null, _options.Color);
        ButtonState();
    }
    public async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<MusicRhymeRequest>() ?? throw new InvalidDataException("Music.Rhyme.InvalidInput");
        var step = state.Checkpoint?.Deserialize<MusicRhymeStep>() ?? new() { Request = request, Original = request.Text };
        step.Validate();
        if (step.Request != request) throw new InvalidDataException("Music.Rhyme.InvalidInput");
        _view.Projects.RestoreRhymeDraft(request);
        await RunAsync(step, token, state);
    }
    private async Task RunAsync(MusicRhymeStep step, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_active is not null) throw new InvalidOperationException("Music.Rhyme.Running");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCancel = cancel; _runFinished = finished.Task;
        _active = step.Request.Id; _activeProject = step.Request.ProjectId; _visual = _active;
        step = Accept(step with { GenerationRequested = true });
        _pulse.Start(); ButtonState();
        try
        {
            if (_models is null) throw new InvalidOperationException("Music.Rhyme.Unavailable");
            var progress = new InlineProgress<RagBookProgress>(p => _view.Dispatcher.Invoke(() => {
                if (!_disposed) _view.Status.AppendLog(_l("Music.Rhyme.Stage." + (p.Stage == "Ready" ? "ReadyWorker" : p.Stage))
                    + (p.Total > 0 ? $" · {p.Done}/{p.Total}" : ""));
            }));
            if (WorkRunner is { } execute)
                await execute(step, next => _view.Dispatcher.Invoke(() => Accept(next)), progress, cancel.Token, restored);
            else await new MusicRhymeWork(_models).RunAsync(step, next => _view.Dispatcher.Invoke(() => Accept(next)), progress, cancel.Token, restored, _l("Music.Rhyme.Title"));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!_disposed && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested)
            {
                var current = HistoryFor(step.Request.ProjectId).FirstOrDefault(s => s.Request.Id == step.Request.Id) ?? step;
                Accept(current with { Stage = "Partial", Error = "Music.Rhyme.Stopped" });
            }
            throw;
        }
        finally { _active = _activeProject = null; _runCancel = null; _pulse.Stop(); ButtonState(); Changed?.Invoke(); finished.TrySetResult(); }
    }
    internal MusicRhymeStep Accept(MusicRhymeStep incoming)
    {
        if (!_drafts.TryGetValue(incoming.Request.ProjectId, out var history)) _drafts.Add(incoming.Request.ProjectId, history = []);
        var index = history.FindIndex(s => s.Request.Id == incoming.Request.Id);
        if (index >= 0)
        {
            var edited = history[index]; incoming = incoming with {
                GenerationRequested = edited.GenerationRequested || incoming.GenerationRequested,
                Original = edited.OriginalEdited ? edited.Original : incoming.Original, OriginalEdited = edited.OriginalEdited,
                Program = edited.ProgramEdited ? edited.Program : incoming.Program, ProgramEdited = edited.ProgramEdited,
                Model = edited.ModelEdited ? edited.Model : incoming.Model, ModelEdited = edited.ModelEdited };
            history[index] = incoming;
        }
        else history.Add(incoming);
        if (StillCurrent(incoming.Request) && (_visual is null || _visual == incoming.Request.Id))
        { _analysis = incoming.Analysis; _view.Editor.SetRhymeAnalysis(_analysis, _options.Color); }
        Changed?.Invoke(); ButtonState(); return incoming;
    }
    public void Edit(string project, string id, int column, string text)
    {
        var history = _drafts.GetValueOrDefault(project); var index = history?.FindIndex(s => s.Request.Id == id) ?? -1;
        if (index < 0 || text.Length > _options.Tuning.Integer("history.column_limit")) return;
        var s = history![index]; history[index] = column switch {
            0 => s with { Original = text, OriginalEdited = true }, 1 => s with { Program = text, ProgramEdited = true },
            2 => s with { Model = text, ModelEdited = true }, _ => s };
    }
    public MusicRhymeStep[] SnapshotHistory() => History.Select(s => s.Snapshot()).ToArray();
    public void Restore(MusicRhymeStep[] steps)
    {
        if (_activeProject == _view.Projects.Current.Id) return;
        _drafts[_view.Projects.Current.Id] = steps.Select(s => s.Snapshot()).ToList();
        _visual = steps.LastOrDefault()?.Request.Id; ProjectChanged(); _visual = steps.LastOrDefault()?.Request.Id; ButtonState();
    }
    public void Open(string? id = null, string? project = null)
    {
        project ??= _view.Projects.Current.Id;
        if (_window is not null && _window.Project != project) _window.Close();
        if (_window is null)
        { var host = Window.GetWindow(_view); _window = new(this, project, _l, host?.Resources) { Owner = host }; _window.Closed += (_, _) => _window = null; _window.Show(); }
        _window.Show(id); _window.Activate();
    }
    public void OpenSettings()
    {
        var project = _view.Projects.Current.Id;
        if (_window is not null && _window.Project != project) _window.Close();
        if (_window is null)
        { var host = Window.GetWindow(_view); _window = new(this, project, _l, host?.Resources) { Owner = host }; _window.Closed += (_, _) => _window = null; _window.Show(); }
        _window.ShowSettings(); _window.Activate();
    }
    private void ButtonState()
    {
        if (_disposed) return;
        var button = _view.TextActions.RhymeButton;
        var running = IsRunning(_view.Projects.Current.Id);
        var paused = running && ApplicationBackgroundOperations.Current?.FindOperation(MusicRhymeWork.Kind)?.Phase
            is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting;
        var ready = History.LastOrDefault(s => s.Request.Id == _visual);
        button.Opacity = running && !paused && _blink ? .45 : 1;
        if (paused) button.Background = Brushes.DarkOrange;
        else if (running) button.Background = Brushes.RoyalBlue;
        else if (ready is { Stage: "Ready" or "Partial" }) button.Background = ready.Error is null ? Brushes.DarkGreen : Brushes.DarkOrange;
        else button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
        button.ToolTip = _l(paused ? "Music.Rhyme.Paused" : running ? "Music.Rhyme.Running" : ready is not null ? "Music.Rhyme.Open" : "Music.Text.Rhyme.Hint");
        AutomationProperties.SetName(button, button.ToolTip.ToString());
    }
    private void Menu()
    {
        var button = _view.TextActions.RhymeButton; var menu = new ContextMenu { PlacementTarget = button };
        var reset = new MenuItem { Header = _l("Music.Rhyme.Reset") };
        reset.Click += (_, _) => { _visual = null; _view.Editor.SetRhymeAnalysis(null, _options.Color); ButtonState(); }; menu.Items.Add(reset);
        var history = new MenuItem { Header = _l("Music.Rhyme.History"), IsEnabled = History.Count > 0 };
        history.Click += (_, _) => Open(); menu.Items.Add(history); menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = _l("Music.Rhyme.Settings.Title") };
        settings.Click += (_, _) => OpenSettings(); menu.Items.Add(settings);
        button.ContextMenu = menu;
    }
    public void ShowModelCatalog(Action<string>? selected = null)
    {
        if (_storage is null) return;
        var choices = MusicRhymePreferences.Choices(_storage);
        var picker = new MusicPoetryModelWindow(choices, choices.FirstOrDefault(c => c.Candidate.Id == _options.ModelId)?.Model?.Path,
            _l, model => {
                var id = MusicPoetryModelCatalog.Find(model)!.Id;
                if (selected is null) Save(_options with { ModelId = id }); else selected(id);
            }, download: async (candidate, progress, token) => {
                using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
                if (candidate.Id != "core") return await MusicPoetryProfileInstallation.DownloadAsync(candidate, _storage, progress, token);
                var root = _storage.Models.Locations.FirstOrDefault()?.Path ?? throw new IOException("Music.StorageRequired");
                using var preparation = new RagBookModelPreparation(root, new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0);
                var card = await preparation.PrepareAsync(ManagedModelCatalog.CoreArtifactId, true, progress, token);
                if (card.Status != ManagedModelStatuses.Installed) throw new IOException("Music.Poetry.ModelMissing");
                return new DebugModelInfo { Name = candidate.FileName, Path = Path.Combine(card.InstallDirectory, candidate.FileName), Format = candidate.Format };
            }) { Owner = Window.GetWindow(_view) };
        if (picker.Owner is { } host) picker.Resources.MergedDictionaries.Add(host.Resources);
        picker.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        picker.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        picker.ShowDialog();
    }
    private void Save(MusicRhymeOptions options) => ApplyOptions(options);
    public bool ApplyOptions(MusicRhymeOptions options)
    {
        try { _preferences.Save(options); _options = options.Snapshot(); UpdateIntervals();
            if (!options.AutoAnalyze) { _debounce.Stop(); _analysisCancel?.Cancel(); }
            else { _analysisCancel?.Cancel(); _analysis = null; _debounce.Stop(); if (!_view.Editor.IsEditing) _debounce.Start(); }
            _view.Editor.SetRhymeAnalysis(_analysis, options.Color); Changed?.Invoke(); return true; }
        catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); return false; }
    }
    private void UpdateIntervals()
    {
        _debounce.Interval = TimeSpan.FromMilliseconds(_options.Tuning.Integer("history.reanalysis_delay"));
        _pulse.Interval = TimeSpan.FromMilliseconds(_options.Tuning.Integer("history.pulse_delay"));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _analysisCancel?.Cancel(); _pulse.Stop(); _debounce.Stop(); _window?.Close();
        _view.TextActions.RhymeRequested -= Click; _view.Editor.LyricsChanged -= TextChanged; _view.Projects.LyricsContextChanged -= ProjectChanged;
        _view.Editor.EditingFinished -= EditingFinished; _view.Editor.EditingStarted -= EditingStarted;
        _lifetime.Dispose();
    }
}
