using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>VACE settings and completed results; execution and recovery belong to the host.</summary>
internal sealed partial class VideoVaceWorkspaceControl : UserControl
{
    private Func<string, string> _localize;
    private readonly VideoPreparationProject _project;
    private readonly Action _changed;
    private readonly VideoVaceWorkspaceState _state;
    private VideoVaceSettings S => _state.Settings;
    private readonly HashSet<string> _invalid = [];
    private readonly Dictionary<string, string> _drafts = [];
    private bool _building, _busy, _generating, _pending, _expanded, _generationAvailable;
    private string _language;
    private StackPanel _settings = new(), _files = new();
    private Grid _body = new();
    private WrapPanel _navigation = new();
    private TextBlock _status = new();
    private Button _generate = new(), _pause = new(), _stop = new(), _changeModel = new();
    private bool IsBusy => _busy || _generating || _pending || _scheduled is not null;
    internal bool GenerationAvailable { get => _generationAvailable; set { _generationAvailable = value; RefreshControls(); } }
    internal event Action? BackRequested;
    internal event Action? HomeRequested;
    internal event Action? SaveRequested;
    internal event Action? SaveAsRequested;
    internal event Action? ExportRequested;
    internal event Action? CancelExportRequested;
    internal event Action? ChangeModelRequested;
    internal event Action? GenerateRequested;
    internal event Action? CancelGenerationRequested;
    internal event Action? PauseGenerationRequested;
    internal event Action<DateTimeOffset>? ScheduleGenerationRequested;
    internal event Action<string>? SendEditorRequested;

    internal VideoVaceWorkspaceControl(Func<string, string> localize, VideoPreparationProject? project = null, Action? changed = null)
    {
        _localize = localize; _project = project ?? new(); _changed = changed ?? (() => { });
        _state = VideoVaceWorkspaceState.FromProject(_project); _language = T("Home");
        AutomationProperties.SetAutomationId(this, "VideoVace.Workspace");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoH3WorkspaceResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); Render();
        Unloaded += (_, _) => { ClosePlayback(); _progressTimer.Stop(); _activeClock.Stop(); };
        Loaded += (_, _) => { if (_generating) { _activeClock.Start(); _progressTimer.Start(); } };
    }
    private string T(string key) => _localize("VideoPrep.VaceWorkspace." + key);
    internal void Configure(Func<string, string> localize)
    {
        _localize = localize; var language = T("Home");
        if (language == _language) return;
        _language = language; Render();
    }
    internal bool TryCommit()
    {
        if (IsBusy || _invalid.Count > 0) { Notify(T("SettingsInvalid")); return false; }
        try { VideoVaceWorkspaceState.Validate(_state); }
        catch (InvalidDataException) { Notify(T("SettingsInvalid")); return false; }
        var previous = _project.Generator?.Vace;
        if (previous is null || JsonSerializer.Serialize(S) != JsonSerializer.Serialize(previous.Settings)
            || _state.SelectedResultId != previous.SelectedResultId)
        {
            // The runner owns Runs. A settings edit must not overwrite newer inference receipts.
            var target = VideoVaceWorkspaceState.Ensure(_project);
            target.Settings = VideoShots.Copy(S); target.SelectedResultId = _state.SelectedResultId; _changed();
        }
        return true;
    }
    private void Changed()
    {
        if (_building) return;
        if (TryCommit()) Notify(T("SettingsPending"));
        ResizeCanvas(); RefreshResultInfo();
    }
    internal void Notify(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notify(message)); return; }
        _status.Text = message;
    }
    internal void SetBusy(bool busy) { _busy = busy; RefreshControls(); if (busy) Notify(T("ExportWorking")); }
    internal void SetGenerating(bool active, bool pending = false)
    {
        var fresh = active && !_generating && !_pending;
        _generating = active; _pending = pending;
        if (active) _scheduled = null;
        ProgressState(active, pending, fresh); RefreshControls();
        if (active) Notify(T("Generating"));
    }
    private void RefreshControls()
    {
        _settings.IsEnabled = !IsBusy; _files.IsEnabled = !IsBusy; _navigation.IsEnabled = !IsBusy;
        _changeModel.IsEnabled = !IsBusy; _timeline.IsEnabled = !IsBusy;
        _generate.IsEnabled = GenerationAvailable && !IsBusy;
        _pause.Visibility = _generating || _pending ? Visibility.Visible : Visibility.Collapsed;
        _pause.Content = _pending ? "▶" : "Ⅱ"; _pause.ToolTip = T(_pending ? "Resume" : "Pause");
        AutomationProperties.SetName(_pause, T(_pending ? "Resume" : "Pause"));
        _stop.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _stop.ToolTip = T("Stop"); AutomationProperties.SetName(_stop, T("Stop"));
    }
    private void Request(Action? action) { if (TryCommit()) action?.Invoke(); }
    private void Render()
    {
        ClosePlayback(); _building = true; _expanded = false; _invalid.Clear();
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = new GridLength(110) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var title = Label(T("Title")); title.FontSize = 28; title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        _changeModel = ActionButton("ChangeModel", "ChangeModel", () => Request(ChangeModelRequested));
        _changeModel.BorderThickness = new Thickness(0); _changeModel.Background = System.Windows.Media.Brushes.Transparent;
        _changeModel.Content = "⇄"; _changeModel.ToolTip = T("ChangeModel"); heading.Children.Add(_changeModel); root.Children.Add(heading);
        var outer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _body = new Grid { MinWidth = 760 };
        foreach (var width in new[] { new GridLength(28, GridUnitType.Star), new GridLength(6), new GridLength(44, GridUnitType.Star), new GridLength(6), new GridLength(28, GridUnitType.Star) })
            _body.ColumnDefinitions.Add(new() { Width = width });
        _settings = new StackPanel(); _files = new StackPanel();
        AddBody(Panel(Scroll(_settings, "SettingsScroll")), 0); AddBody(BuildPreview(), 2); AddBody(Panel(Scroll(_files, "FilesScroll")), 4);
        foreach (var column in new[] { 1, 3 })
        {
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            splitter.SetResourceReference(BackgroundProperty, "LineBrush"); AddBody(splitter, column);
        }
        outer.Content = _body; outer.SizeChanged += (_, _) => _body.Width = Math.Max(760, outer.ActualWidth - 18);
        Grid.SetRow(outer, 1); root.Children.Add(outer); BuildTimeline(root);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _navigation = new WrapPanel(); _navigation.Children.Add(ActionButton("Back", "Back", () => Request(BackRequested)));
        _navigation.Children.Add(ActionButton("Home", "Home", () => Request(HomeRequested))); DockPanel.SetDock(_navigation, Dock.Left); footer.Children.Add(_navigation);
        var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _generate = ActionButton("Generate", "Generate", () => { if (GenerationAvailable && !IsBusy) Request(GenerateRequested); }); _generate.Style = (Style)FindResource("H3StartButton");
        _pause = ActionButton("Pause", "PauseGeneration", () => PauseGenerationRequested?.Invoke()); _pause.Style = (Style)FindResource("H3PauseButton");
        _stop = ActionButton("Stop", "CancelGeneration", () =>
        { if (_copyCancel is not null) _copyCancel.Cancel(); else if (_generating || _pending || _scheduled is not null) CancelGenerationRequested?.Invoke(); else CancelExportRequested?.Invoke(); });
        _stop.Style = (Style)FindResource("H3StopButton"); _stop.Content = "⛔";
        controls.Children.Add(_generate); controls.Children.Add(_pause); controls.Children.Add(_stop); BuildScheduleMenu();
        DockPanel.SetDock(controls, Dock.Right); footer.Children.Add(controls);
        var information = new StackPanel(); _status = Label(T("Notice")); information.Children.Add(_status);
        _progressSummary = Label(""); _progressSummary.FontSize = 12; AutomationProperties.SetAutomationId(_progressSummary, "VideoVace.Workspace.ProgressSummary");
        information.Children.Add(_progressSummary); footer.Children.Add(information); Grid.SetRow(footer, 3); root.Children.Add(footer);
        BuildSettings(); BuildFiles();
        var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); layout.ColumnDefinitions.Add(new());
        _fluid = new H3FluidProgress { Margin = new Thickness(0, 0, 6, 0), ToolTip = T("ActivityHint") };
        AutomationProperties.SetAutomationId(_fluid, "VideoVace.Workspace.Progress"); layout.Children.Add(_fluid); Grid.SetColumn(root, 1); layout.Children.Add(root);
        Content = layout; _building = false; ReloadResults(); RefreshControls(); DrawProgress();
    }
    private void AddBody(UIElement element, int column) { Grid.SetColumn(element, column); _body.Children.Add(element); }
}
