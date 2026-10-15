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
public sealed class MusicRhymeController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
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
    public IReadOnlyList<MusicRhymeStep> History => HistoryFor(_view.Projects.Current.Id);
    public IReadOnlyList<MusicRhymeStep> HistoryFor(string project) => _drafts.GetValueOrDefault(project) ?? [];
    public MusicRhymeController(MusicWorkspaceControl view)
    {
        _view = view;
        try { _options = MusicRhymePreferences.Default.Load(); } catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); }
        view.TextActions.RhymeRequested += Click;
        view.TextActions.RhymeButton.ContextMenu = new();
        view.TextActions.RhymeButton.ContextMenuOpening += (_, args) => {
            args.Handled = true; Menu(); view.TextActions.RhymeButton.ContextMenu.IsOpen = true;
        };
        view.Editor.LyricsChanged += TextChanged;
        view.Projects.LyricsContextChanged += ProjectChanged;
        _pulse.Tick += (_, _) => { _blink = !_blink; ButtonState(); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await AnalyzeEditsAsync(); };
        ButtonState();
    }
    public void Configure(StorageSettings storage, Func<IMusicRhymeModel> models) { _storage = storage; _models = models; }
    public void Localize(Func<string, string> l) { _l = l; ButtonState(); }
    public string Error(string message) => message.StartsWith("Music.", StringComparison.Ordinal) || message.StartsWith("RagBooks.", StringComparison.Ordinal) ? _l(message) : message;
    private void ProjectChanged()
    {
        var step = History.LastOrDefault(); _visual = step?.Request.Id;
        _analysis = step?.Request.Source == _view.Editor.Lyrics ? step.Analysis : null;
        _view.Editor.SetRhymeAnalysis(_analysis, _options.Color); Changed?.Invoke(); ButtonState();
    }
    private void TextChanged(object? sender, EventArgs args)
    {
        _analysisCancel?.Cancel(); _analysis = null; _view.Editor.SetRhymeAnalysis(null, _options.Color);
        if (_options.AutoAnalyze && History.Count > 0) { _debounce.Stop(); _debounce.Start(); }
    }
    private async Task AnalyzeEditsAsync()
    {
        if (_disposed || !File.Exists(Path.Combine(MusicRhymeCorpus.DirectoryPath, "corpus.json"))) return;
        var text = _view.Editor.Lyrics; var project = _view.Projects.Current.Id;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _analysisCancel = cancel;
        try
        {
            var analysis = await Task.Run(() => { using var lexicon = new MusicRhymeLexicon(MusicRhymeCorpus.DirectoryPath, cancel.Token);
                return MusicRhymeAnalyzer.Analyze(text, lexicon.Lookup, cancel.Token); }, cancel.Token);
            if (_view.Editor.Lyrics == text && _view.Projects.Current.Id == project && !cancel.IsCancellationRequested)
            { _analysis = analysis; _view.Editor.SetRhymeAnalysis(analysis, _options.Color); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); }
        finally { if (_analysisCancel == cancel) _analysisCancel = null; }
    }
    private async void Click()
    {
        if (_disposed) return;
        if (_active is not null) { _view.Status.AppendLog(_l("Music.Rhyme.Running")); return; }
        if (_visual is not null && History.Any(s => s.Request.Id == _visual)) { Open(_visual); return; }
        try
        {
            if (_storage is null || _models is null) throw new InvalidOperationException("Music.Rhyme.Unavailable");
            if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true) throw new InvalidOperationException("Music.Poetry.OtherOperation");
            var selection = _view.Editor.CaptureSelection(); var scope = MusicRhymeAnalyzer.Scope(selection, _options.SelectionOnly);
            var choice = MusicRhymePreferences.Choices(_storage).Single(c => c.Candidate.Id == _options.ModelId);
            var model = choice.Model ?? throw new InvalidDataException("Music.Poetry.ModelMissing");
            var root = _storage.Models.Locations.FirstOrDefault()?.Path ?? "";
            var request = new MusicRhymeRequest(Guid.NewGuid().ToString("N"), _view.Projects.Current.Id, selection.Text,
                scope.Start, scope.Length, _options.ModelId, model.Path, root, _l("Music.Rhyme.Language"));
            await RunAsync(new() { Request = request, Original = request.Text }, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); }
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
        _active = step.Request.Id; _activeProject = step.Request.ProjectId; _visual = _active;
        _pulse.Start(); ButtonState();
        try
        {
            if (_models is null) throw new InvalidOperationException("Music.Rhyme.Unavailable");
            var progress = new InlineProgress<RagBookProgress>(p => _view.Dispatcher.Invoke(() => {
                if (!_disposed) _view.Status.AppendLog(_l("Music.Rhyme.Stage." + (p.Stage == "Ready" ? "ReadyWorker" : p.Stage))
                    + (p.Total > 0 ? $" · {p.Done}/{p.Total}" : ""));
            }));
            await new MusicRhymeWork(_models).RunAsync(step, next => _view.Dispatcher.Invoke(() => Accept(next)), progress, token, restored, _l("Music.Rhyme.Title"));
        }
        finally { _active = _activeProject = null; _pulse.Stop(); ButtonState(); }
    }
    internal MusicRhymeStep Accept(MusicRhymeStep incoming)
    {
        if (!_drafts.TryGetValue(incoming.Request.ProjectId, out var history)) _drafts.Add(incoming.Request.ProjectId, history = []);
        var index = history.FindIndex(s => s.Request.Id == incoming.Request.Id);
        if (index >= 0)
        {
            var edited = history[index]; incoming = incoming with {
                Original = edited.OriginalEdited ? edited.Original : incoming.Original, OriginalEdited = edited.OriginalEdited,
                Program = edited.ProgramEdited ? edited.Program : incoming.Program, ProgramEdited = edited.ProgramEdited,
                Model = edited.ModelEdited ? edited.Model : incoming.Model, ModelEdited = edited.ModelEdited };
            history[index] = incoming;
        }
        else history.Add(incoming);
        if (_view.Projects.Current.Id == incoming.Request.ProjectId && _view.Editor.Lyrics == incoming.Request.Source)
        { _analysis = incoming.Analysis; _view.Editor.SetRhymeAnalysis(_analysis, _options.Color); }
        Changed?.Invoke(); ButtonState(); return incoming;
    }
    public void Edit(string project, string id, int column, string text)
    {
        var history = _drafts.GetValueOrDefault(project); var index = history?.FindIndex(s => s.Request.Id == id) ?? -1;
        if (index < 0 || text.Length > 400_000) return;
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
        if (HistoryFor(project).Count == 0) return;
        if (_window is not null && _window.Project != project) _window.Close();
        if (_window is null)
        { var host = Window.GetWindow(_view); _window = new(this, project, _l, host?.Resources) { Owner = host }; _window.Closed += (_, _) => _window = null; _window.Show(); }
        _window.Show(id); _window.Activate();
    }
    private void ButtonState()
    {
        if (_disposed) return;
        var button = _view.TextActions.RhymeButton;
        var running = _active is not null && _visual is not null;
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
        var palette = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var color in MusicRhymePreferences.Palette)
        {
            var swatch = new Button { Width = 25, Height = 25, Margin = new(2), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                BorderThickness = new(color == _options.Color ? 3 : 1), ToolTip = color };
            AutomationProperties.SetAutomationId(swatch, "Music.Rhyme.Color." + color[1..]);
            swatch.Click += (_, _) => Save(_options with { Color = color }); palette.Children.Add(swatch);
        }
        menu.Items.Add(new MenuItem { Header = palette, StaysOpenOnClick = true });
        Check("Music.Rhyme.SelectionOnly", _options.SelectionOnly, value => Save(_options with { SelectionOnly = value }));
        Check("Music.Rhyme.AutoAnalyze", _options.AutoAnalyze, value => Save(_options with { AutoAnalyze = value }));
        var models = new MenuItem { Header = _l("Music.Rhyme.Model") }; menu.Items.Add(models);
        if (_storage is not null) foreach (var choice in MusicRhymePreferences.Choices(_storage))
        {
            var item = new MenuItem { Header = choice.Candidate.DisplayName ?? choice.Candidate.FileName, IsCheckable = true,
                IsChecked = choice.Candidate.Id == _options.ModelId, IsEnabled = choice.Model is not null };
            item.Click += (_, _) => Save(_options with { ModelId = choice.Candidate.Id }); models.Items.Add(item);
        }
        var catalog = new MenuItem { Header = _l("Music.Rhyme.ModelCatalog") }; catalog.Click += (_, _) => ShowModelCatalog(); models.Items.Add(new Separator()); models.Items.Add(catalog);
        button.ContextMenu = menu;
        void Check(string key, bool value, Action<bool> change)
        { var item = new MenuItem { Header = _l(key), IsCheckable = true, IsChecked = value }; item.Click += (_, _) => change(item.IsChecked); menu.Items.Add(item); }
    }
    private void ShowModelCatalog()
    {
        if (_storage is null) return;
        var choices = MusicRhymePreferences.Choices(_storage);
        var picker = new MusicPoetryModelWindow(choices, choices.FirstOrDefault(c => c.Candidate.Id == _options.ModelId)?.Model?.Path,
            _l, model => Save(_options with { ModelId = MusicPoetryModelCatalog.Find(model)!.Id }), download: async (candidate, progress, token) => {
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
    private void Save(MusicRhymeOptions options)
    {
        try { MusicRhymePreferences.Default.Save(options); _options = options;
            if (!options.AutoAnalyze) { _debounce.Stop(); _analysisCancel?.Cancel(); }
            _view.Editor.SetRhymeAnalysis(_analysis, options.Color); Changed?.Invoke(); }
        catch (Exception e) { _view.Status.AppendLog(Error(e.Message)); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _analysisCancel?.Cancel(); _pulse.Stop(); _debounce.Stop(); _window?.Close();
        _view.TextActions.RhymeRequested -= Click; _view.Editor.LyricsChanged -= TextChanged; _view.Projects.LyricsContextChanged -= ProjectChanged;
        _lifetime.Dispose();
    }
}
