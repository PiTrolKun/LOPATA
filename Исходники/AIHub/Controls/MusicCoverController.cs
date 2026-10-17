using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

/// <summary>Music-page integration; durable generation lives in MusicCoverWork, not in the chooser window.</summary>
public sealed partial class MusicCoverController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicGenerationJobs _jobs;
    private readonly MusicCoverStore _store;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private StorageSettings? _storage;
    private Func<IMusicRhymeModel>? _models;
    private Func<string, string> _l = key => key;
    private MusicCoverWindow? _window;
    private CancellationTokenSource? _cancel;
    private MusicCoverInput? _active;
    private bool _disposed, _lit;
    public string? Error { get; private set; }
    public bool Busy => _active is not null || _preparation is not null;
    public bool IsWorkingFor(string artist, string title) => _active is not null
        && MusicCoverStore.Key(_active.Artist, _active.Title) == MusicCoverStore.Key(artist, title);
    public event Action? Changed;
    internal MusicCoverController(MusicWorkspaceControl view, MusicGenerationJobs jobs, MusicCoverStore? store = null)
    {
        _view = view; _jobs = jobs; _store = store ?? MusicCoverStore.Default;
        _view.TextActions.CoverRequested += Click;
        _view.Generation.OptionsChanged += Refresh;
        _view.Editor.ValidityChanged += EditorChanged;
        _view.Session.GenerationCompleted += MusicCompleted;
        _view.IsVisibleChanged += VisibilityChanged;
        _view.Loaded += ViewLoaded;
        _view.TextActions.CoverButton.ContextMenu = new ContextMenu();
        _view.TextActions.CoverButton.ContextMenuOpening += MenuOpening;
        if (ApplicationBackgroundOperations.Current is { } background) background.Changed += BackgroundChanged;
        _blink.Tick += (_, _) => {
            if (Window.GetWindow(_view)?.WindowState == WindowState.Minimized) { _view.TextActions.CoverButton.Opacity = 1; return; }
            _lit = !_lit; _view.TextActions.CoverButton.Opacity = _lit ? 1 : .45;
        };
        Refresh();
    }
    public void Configure(StorageSettings storage, Func<IMusicRhymeModel> models, int connections = 0)
    { _storage = storage; _models = models; _downloadConnections = connections; _preparation?.DownloadConnections(connections); Refresh(); }
    public void Localize(Func<string, string> l) { _l = l; Refresh(); _window?.Refresh(); }
    public MusicCoverEntry Current => _store.Read(_view.Generation.Options.Artist, _view.Generation.Options.Title);
    public MusicCoverSelection? CaptureSelection(string artist, string title)
    {
        var entry = _store.Read(artist, title);
        if (!entry.IsEnabled || entry.Selected is null) return null;
        _ = MusicCoverStore.ReadArtwork(entry.Selected); return entry.Selected;
    }
    private void EditorChanged(object? sender, EventArgs e) => Refresh();
    private void VisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => Refresh();
    private void ViewLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void BackgroundChanged()
    { if (!_disposed && !_view.Dispatcher.HasShutdownStarted) _view.Dispatcher.BeginInvoke(Refresh); }
    private bool CanGenerate => !string.IsNullOrWhiteSpace(_view.Generation.Options.Title) && !string.IsNullOrWhiteSpace(_view.Editor.Lyrics);
    private void Refresh()
    {
        if (_disposed) return;
        var button = _view.TextActions.CoverButton;
        try
        {
            var entry = Current;
            if (entry.IsEnabled) button.Background = Brushes.DarkGreen;
            else button.SetResourceReference(Control.BackgroundProperty, "SecondaryButtonBackgroundBrush");
            var hint = _l(_preparation is not null ? "Generation.Preparation.Title" : Busy ? "Music.Cover.Generating" : entry.IsEnabled && entry.Selected is null && CanGenerate
                ? "Music.Cover.ReadyToGenerate" : entry.IsEnabled ? "Music.Cover.Enabled" : "Music.Cover.Disabled");
            button.ToolTip = hint; AutomationProperties.SetName(button, hint);
            var pulse = entry.IsEnabled && entry.Selected is null && CanGenerate && !Busy && _view.IsVisible && SystemParameters.ClientAreaAnimation;
            if (pulse) _blink.Start(); else { _blink.Stop(); button.Opacity = 1; }
            CheckPreparationWhenEnabled();
        }
        catch (Exception error) { _blink.Stop(); button.Opacity = 1; Error = error.Message; button.ToolTip = Error; }
        Changed?.Invoke();
    }
    private async void Click()
    {
        try
        {
            if (_preparation is not null) { _preparation.Activate(); return; }
            if (Busy) { Open(_active!.Artist, _active.Title); return; }
            var entry = Current;
            if (entry.IsEnabled && entry.Selected is null && CanGenerate)
            { Open(entry.Artist, entry.Title); await GenerateAsync(); }
            else { _store.SetEnabled(entry.Artist, entry.Title, !entry.IsEnabled); Refresh(); }
        }
        catch (Exception error) { Report(error); }
    }
    private void MenuOpening(object sender, ContextMenuEventArgs args)
    {
        var menu = new ContextMenu();
        void Item(string key, bool enabled, Action action)
        {
            var item = new MenuItem { Header = _l(key), IsEnabled = enabled };
            item.Click += (_, _) => { try { action(); } catch (Exception error) { Report(error); } }; menu.Items.Add(item);
        }
        Item("Music.Cover.Preview", !Busy && CanGenerate, () => { Open(); _ = GenerateHandledAsync(); });
        Item("Music.Cover.Open", true, () => Open());
        Item("Music.Cover.Toggle", !Busy, () => { var entry = Current; _store.SetEnabled(entry.Artist, entry.Title, !entry.IsEnabled); Refresh(); });
        _view.TextActions.CoverButton.ContextMenu = menu;
        args.Handled = true; menu.PlacementTarget = _view.TextActions.CoverButton; menu.IsOpen = true;
    }
    public void Open(string? artist = null, string? title = null)
    {
        var options = _view.Generation.Options;
        artist ??= _active?.Artist ?? options.Artist; title ??= _active?.Title ?? options.Title;
        if (_window is not null && (_window.Artist != artist || _window.SongTitle != title)) { _window.Close(); _window = null; }
        if (_window is null)
        {
            _window = new(this, artist, title, _l, Window.GetWindow(_view)?.Resources) { Owner = Window.GetWindow(_view) };
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible) _window.Show(); else _window.Activate();
        var operation = ApplicationBackgroundOperations.Current?.FindOperation(MusicCoverWork.Kind);
        if (operation is { Phase: BackgroundOperationPhase.Completed } && operation.Project == MusicCoverStore.Key(artist, title))
            ApplicationBackgroundOperations.Current?.Acknowledge(operation.Id);
    }
    public MusicCoverRun? Latest(string artist, string title) => _store.Latest(artist, title);
    public MusicCoverEntry Entry(string artist, string title) => _store.Read(artist, title);
    internal async Task ChooseAsync(MusicCoverRun run, string path)
    {
        if (Busy || ApplicationBackgroundOperations.Current?.BlocksNewWork == true) throw new InvalidOperationException("Music.Poetry.OtherOperation");
        using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        var selection = _store.Confirm(run.Input, path);
        var applied = true;
        var id = MatchingJob(run.Input.Artist, run.Input.Title, run.Input.JobId);
        if (id is not null)
        {
            var playing = _view.Player.CurrentTrack;
            if (playing?.JobId == id) _view.Player.Clear();
            try { applied = await MusicCoverMetadata.ApplyAsync(_jobs, id, selection, ApplicationBackgroundOperations.ExitToken); }
            finally { if (playing?.JobId == id) _view.Player.Select(playing); }
        }
        Error = applied ? null : _l("Music.Cover.ApplyPending"); Refresh();
    }
    private string? MatchingJob(string artist, string title, string? captured)
    {
        foreach (var id in new[] { _view.Projects.DisplayedLyricsJobId, captured, _view.Player.CurrentTrack?.JobId }.Where(id => id is not null).Distinct())
        {
            var job = _jobs.Load(id!);
            if (MusicCoverStore.Key(job.Artist, job.Title) == MusicCoverStore.Key(artist, title)) return id;
        }
        return null;
    }
    private void Report(Exception error)
    { Error = _l(error.Message); _view.Status.AppendLog(_l("Music.Cover.Error") + " " + Error); Refresh(); }
    public void Dispose()
    {
        _disposed = true; _blink.Stop(); _cancel?.Cancel(); _preparation?.Close(); _window?.Close();
        _view.TextActions.CoverRequested -= Click; _view.Generation.OptionsChanged -= Refresh;
        _view.Editor.ValidityChanged -= EditorChanged; _view.IsVisibleChanged -= VisibilityChanged;
        _view.Loaded -= ViewLoaded;
        _view.Session.GenerationCompleted -= MusicCompleted; _view.TextActions.CoverButton.ContextMenuOpening -= MenuOpening;
        if (ApplicationBackgroundOperations.Current is { } background) background.Changed -= BackgroundChanged;
    }
}
