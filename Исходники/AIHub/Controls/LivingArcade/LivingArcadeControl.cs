using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using GameHost = AIHub.Controls.LivingArcade.LivingArcadeGameHost;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using GameHud = AIHub.Controls.LivingArcade.LivingArcadeGameHud;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>The game stays in the existing navigation surface; the model queue owns durable generation.</summary>
public sealed partial class LivingArcadeControl : UserControl, IDisposable
{
    private Func<string, string> _l;
    private readonly LivingArcadeRunStore _store;
    private readonly LivingArcadeGeneration _generation;
    private readonly LivingArcadeModelPaths _paths;
    private readonly CancellationToken _lifetime;
    private readonly Func<string> _language;
    private readonly Grid _root = new();
    private readonly TextBlock _status = LiteraryUi.Text("");
    private readonly TextBlock _hud = LiteraryUi.Text("", true);
    private readonly GameHud _gameHud;
    private readonly Grid _gameFooter = new() { Visibility = Visibility.Collapsed };
    private readonly ContentControl _body = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private GameHost? _host;
    private LivingArcadeSimulation? _simulation;
    private string? _runId;
    private string? _shownOperationId;
    private bool _playing, _paused, _transitioning, _disposed;
    private bool _generating, _warming, _ready;
    private double _musicVolume = .18, _effectsVolume = .55;
    private DateTime _lastHud;
    public event Action? HomeRequested;

    public LivingArcadeControl(Func<string, string> localize, LivingArcadeRunStore store,
        LivingArcadeGeneration generation, LivingArcadeModelPaths paths,
        Func<string> language, CancellationToken lifetime)
    {
        _l = localize; _store = store; _generation = generation; _paths = paths;
        _language = language; _lifetime = lifetime;
        _gameHud = new(key => _l(key), _language);
        InitializePauseShortcut();
        AutomationProperties.SetAutomationId(this, "Arcade.Page");
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new());
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var home = MakeButton("Arcade.Home", () => { SuspendForNavigation(); HomeRequested?.Invoke(); }, "Arcade.Home");
        home.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        _gameHud.StageHeader.Visibility = Visibility.Collapsed;
        header.Children.Add(_gameHud.StageHeader);
        header.Children.Add(home);
        _root.Children.Add(header);
        Grid.SetRow(_hud, 1); _root.Children.Add(_hud);
        Grid.SetRow(_body, 2); _root.Children.Add(_body);
        Grid.SetRow(_actions, 3); _actions.Margin = new Thickness(0, 10, 0, 8); _root.Children.Add(_actions);
        Grid.SetRow(_gameFooter, 3); _root.Children.Add(_gameFooter);
        _status.FontSize = 12; _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0, 3, 0, 0);
        _status.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = _status });
        Grid.SetRow(_status, 4); _root.Children.Add(_status);
        Content = _root;
        _generation.StatusChanged += OnGenerationStatus;
        _generation.RunUpdated += OnRunUpdated;
        Unloaded += (_, _) => SuspendForNavigation();
        ShowMenu();
    }

    public void Configure(Func<string, string> localize)
    {
        _l = localize;
        if (!_playing) ShowMenuCore(false);
        else if (_simulation is not null)
        { _gameHud.Update(_simulation.Snapshot); BuildGameFooter(); }
    }

    public async Task WarmAsync(BackgroundOperationState? restored = null, CancellationToken? token = null)
    {
        if (_warming || _generating || _disposed) return;
        _warming = true;
        _ready = false; OnGenerationStatus("Arcade.Ai.Warming");
        try
        {
            await _generation.WarmAsync(_paths, _language(), token ?? _lifetime, restored);
            _ready = true; OnGenerationStatus("Arcade.Ready");
        }
        catch (OperationCanceledException) { OnGenerationStatus("Arcade.PausedGeneration"); }
        catch (Exception error) { ShowError(error); }
        finally { _warming = false; }
        if (!_playing) ShowMenuCore(false);
        if (_ready && _pendingStartId is { } pending) StartRun(pending);
    }

    public async Task RestoreGenerationAsync(string runId, BackgroundOperationState restored, CancellationToken token)
    {
        if (DiscardDeletedOperation(runId, restored)) return;
        _runId = runId; _ready = true;
        await PrepareAheadAsync(restored, token);
    }

    public async Task RestoreFirstEventsAsync(string runId, BackgroundOperationState restored, CancellationToken token)
    {
        if (DiscardDeletedOperation(runId, restored)) return;
        _runId = runId; _ready = true;
        await PrepareFirstEventsAsync(runId, restored, token);
    }

    public void ShowPendingOperation(BackgroundOperationState state)
    {
        if (_disposed) return;
        _shownOperationId = state.Id;
        if (!_playing) ShowMenuCore(false);
        _status.Text = _l(state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing
            ? "Arcade.Busy" : "Arcade.PausedGeneration");
    }

    private bool DiscardDeletedOperation(string runId, BackgroundOperationState state)
    {
        if (!_store.IsDeleted(runId)) return false;
        ApplicationBackgroundOperations.Current?.DiscardPending(state.Id);
        ShowMenu();
        _status.Text = _l("LivingArcade.DeletedRun");
        return true;
    }

    private async Task PrepareFirstEventsAsync(string id, BackgroundOperationState? restored = null, CancellationToken? token = null)
    {
        if (_generating || _disposed) return;
        _generating = true;
        var succeeded = false;
        try
        {
            await _generation.PrepareFirstEventsAsync(_store, id, _paths, _language(), token ?? _lifetime, restored);
            succeeded = true;
        }
        catch (OperationCanceledException) { OnGenerationStatus("Arcade.PausedGeneration"); }
        catch (Exception error) { ShowError(error); }
        finally { _generating = false; }
        if (succeeded && !_disposed && _pendingStartId is { } pending) StartRun(pending);
    }

    private async Task PrepareAheadAsync(BackgroundOperationState? restored = null, CancellationToken? token = null)
    {
        if (_generating || _runId is null || _disposed) return;
        _generating = true;
        var id = _runId;
        int? levelNumber = null;
        var succeeded = false;
        try {
            levelNumber = _store.Load(id)?.CurrentLevel;
            await _generation.PrepareAheadAsync(_store, id, _paths, _language(), token ?? _lifetime, restored);
            succeeded = true;
        }
        catch (OperationCanceledException) { OnGenerationStatus("Arcade.PausedGeneration"); }
        catch (Exception error) { ShowError(error); }
        finally { _generating = false; }
        try {
            if (succeeded && _pendingStartId is { } pending && !_disposed) StartRun(pending);
            else if (succeeded && _playing && !_disposed && (_runId != id || _store.Load(id)?.CurrentLevel != levelNumber))
                _ = PrepareAheadAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OnGenerationStatus(string key) => Dispatcher.InvokeAsync(() =>
    {
        if (!_disposed) { _status.Text = _l(key); RefreshPreparationControl(); }
    });

    private void OnRunUpdated(string id) => Dispatcher.InvokeAsync(() =>
    {
        if (_disposed) return;
        if (_pendingStartId == id && !_generating) StartRun(id);
        if (!_playing || _runId != id || _host is not { } host || _simulation is null) return;
        try
        {
            var run = _store.Load(id);
            if (run is null || run.CurrentLevel != _simulation.Snapshot.GameLevel
                || !run.Levels.TryGetValue(run.CurrentLevel, out var level)) return;
            // Only replace the temporary decoration; never restart the battle when the artist finishes.
            if (!host.HasGeneratedScenery && level.Scenery is not null)
            {
                host.SetBackgroundScene(run.Seed, run.CurrentLevel, level.Scenery);
                if (_body.Content is Grid surface)
                    surface.Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(host.BackgroundColor));
            }
            if (!host.HasGeneratedBossArt && level.BossArt is not null) host.SetBossArt(level.BossArt);
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException)
        { ShowError(error); }
    });

    private void ShowError(Exception error)
    {
        ComponentAuditFailures.Report(error);
        Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            var key = error.Message.StartsWith("Arcade.", StringComparison.Ordinal) || error.Message.StartsWith("LivingArcade.", StringComparison.Ordinal)
                ? error.Message : "Arcade.Failed";
            _status.Text = _l(key);
        });
    }

    private Button MakeButton(string key, Action action, string id, bool primary = false)
    {
        var b = LiteraryUi.Button(_l(key), action, primary);
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    public void SuspendForNavigation()
    {
        SaveExperience();
        _pendingStartId = null;
        _host?.Stop();
        _host?.Audio.Pause();
        if (_playing) _paused = true;
        RefreshGamePauseControls();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SaveExperience();
        _disposed = true;
        _generation.StatusChanged -= OnGenerationStatus;
        _generation.RunUpdated -= OnRunUpdated;
        DetachPreparationObserver();
        DetachPauseShortcutWindow();
        _host?.Dispose();
    }
}
