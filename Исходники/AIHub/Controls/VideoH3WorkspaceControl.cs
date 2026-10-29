using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>H3 settings and verified generated results; execution belongs to the host operation.</summary>
internal sealed partial class VideoH3WorkspaceControl : UserControl
{
    private Func<string, string> _l;
    private readonly VideoPreparationProject _project;
    private readonly Action _changed;
    private readonly VideoH3WorkspaceState _state;
    private VideoH3Settings S => _state.Settings;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly HashSet<string> _invalid = [];
    private bool _building, _externalBusy, _expanded;
    private string _language;
    private TextBlock _status = new(), _time = new();
    private StackPanel _settings = new(), _files = new();
    private Grid _body = new();
    private WrapPanel _navigation = new();
    private Button _cancelExport = new();
    private Button _generate = new(), _pause = new();
    private bool _generating, _generationAvailable, _pendingH3;
    internal bool GenerationAvailable { get => _generationAvailable; set { _generationAvailable = value; _generate.IsEnabled = value && !_externalBusy; } }
    private System.Windows.Controls.ListBox _timeline = new();
    internal event Action? BackRequested;
    internal event Action? HomeRequested;
    internal event Action? SaveRequested;
    internal event Action? SaveAsRequested;
    internal event Action? ExportRequested;
    internal event Action? ChangeModelRequested;
    internal event Action? CancelExportRequested;
    internal event Action? GenerateRequested;
    internal event Action? CancelGenerationRequested;
    internal event Action? PauseGenerationRequested;
    internal event Action<DateTimeOffset>? ScheduleGenerationRequested;
    internal event Action<string>? SendEditorRequested;
    internal VideoH3WorkspaceControl(Func<string, string> localize, VideoPreparationProject? project = null, Action? changed = null)
    {
        _l = localize; _project = project ?? new(); _changed = changed ?? (() => { });
        _state = VideoH3WorkspaceState.FromProject(_project);
        _state.Settings.RunMode = "automatic";
        _language = T("H3WorkspaceHome");
        AutomationProperties.SetAutomationId(this, "VideoH3.Workspace");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoH3WorkspaceResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Render();
        Unloaded += (_, _) => { ClosePlayback(); _progressTimer.Stop(); };
        Loaded += (_, _) => { if (_generating) _progressTimer.Start(); };
    }
    private string T(string key) => _l("VideoPrep." + key);
    internal void Configure(Func<string, string> localize)
    {
        _l = localize; var language = T("H3WorkspaceHome");
        if (language == _language) return;
        _language = language; Render();
    }
    internal bool TryCommit()
    {
        if (_externalBusy || _invalid.Count > 0) { Notify(T("H3SettingsInvalid")); return false; }
        try { VideoH3WorkspaceState.Validate(_state); }
        catch (InvalidDataException) { Notify(T("H3SettingsInvalid")); return false; }
        if (JsonSerializer.Serialize(_state) != JsonSerializer.Serialize(_project.Generator?.H3))
        { (_project.Generator ??= new()).H3 = VideoShots.Copy(_state); _changed(); }
        return true;
    }
    private void Changed()
    {
        if (_building) return;
        if (TryCommit()) Notify(T("H3SettingsPending"));
        ResizeCanvas(); RefreshRequestedSummary(); FilterResults();
    }
    internal void Notify(string message) => _status.Text = message;
    internal void SetBusy(bool busy)
    {
        _externalBusy = busy; _body.IsEnabled = !busy; _timeline.IsEnabled = !busy; _navigation.IsEnabled = !busy;
        _cancelExport.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) Notify(T("ExportWorking"));
    }
    internal void SetGenerating(bool busy, bool pending = false)
    {
        _generating = busy; _pendingH3 = pending; _externalBusy = busy || pending; _body.IsEnabled = true; _settings.IsEnabled = !_externalBusy; _files.IsEnabled = !_externalBusy;
        _generate.IsEnabled = GenerationAvailable && !_externalBusy;
        _navigation.IsEnabled = !_externalBusy; _cancelExport.Visibility = _externalBusy ? Visibility.Visible : Visibility.Collapsed;
        _cancelExport.Content = "⛔"; _cancelExport.ToolTip = T(_externalBusy ? "H3Cancel" : "FinishCancel");
        _pause.Visibility = _externalBusy ? Visibility.Visible : Visibility.Collapsed; _pause.Content = pending ? "▶" : "Ⅱ";
        _pause.ToolTip = T(pending ? "H3Resume" : "H3Pause");
        System.Windows.Automation.AutomationProperties.SetName(_pause, T(pending ? "H3Resume" : "H3Pause"));
        ProgressState(busy, pending);
        if (busy) Notify(T("H3Generating"));
    }
    private void Request(Action? action)
    { if (TryCommit()) action?.Invoke(); }
    private void Render()
    {
        ClosePlayback();
        _building = true; _expanded = false; _invalid.Clear();
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = new GridLength(110) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var title = Label("MiniMax H3"); title.FontSize = 28; title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        var change = ActionButton("H3ChangeModel", "ChangeModel", () => Request(ChangeModelRequested));
        change.BorderThickness = new Thickness(0); change.Background = System.Windows.Media.Brushes.Transparent;
        change.Content = "⇄"; change.ToolTip = T("H3ChangeModel"); AutomationProperties.SetName(change, T("H3ChangeModel")); heading.Children.Add(change); root.Children.Add(heading);
        var outer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _body = new Grid { MinWidth = 760 };
        foreach (var width in new[] { new GridLength(28, GridUnitType.Star), new GridLength(6), new GridLength(44, GridUnitType.Star), new GridLength(6), new GridLength(28, GridUnitType.Star) })
            _body.ColumnDefinitions.Add(new() { Width = width });
        _settings = new StackPanel(); _files = new StackPanel();
        var settingsHost = new DockPanel(); settingsHost.Children.Add(Scroll(_settings, "SettingsScroll"));
        AddBody(Panel(settingsHost), 0); AddBody(BuildPreview(), 2); AddBody(Panel(Scroll(_files, "FilesScroll")), 4);
        foreach (var col in new[] { 1, 3 })
        {
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            splitter.SetResourceReference(BackgroundProperty, "LineBrush"); AddBody(splitter, col);
        }
        outer.Content = _body; outer.SizeChanged += (_, _) => _body.Width = Math.Max(760, outer.ActualWidth - 18);
        Grid.SetRow(outer, 1); root.Children.Add(outer); BuildTimeline(root);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var nav = _navigation = new WrapPanel(); nav.Children.Add(ActionButton("H3WorkspaceBack", "Back", () => Request(BackRequested)));
        nav.Children.Add(ActionButton("H3WorkspaceHome", "Home", () => Request(HomeRequested))); DockPanel.SetDock(nav, Dock.Left); footer.Children.Add(nav);
        _cancelExport = ActionButton("FinishCancel", "CancelExport", () => { if (_generating || _pendingH3) CancelGenerationRequested?.Invoke(); else if (_copyCancel is not null) _copyCancel.Cancel(); else CancelExportRequested?.Invoke(); }); _cancelExport.Visibility = Visibility.Collapsed;

        _pause = ActionButton("H3Pause", "PauseGeneration", () => PauseGenerationRequested?.Invoke()); _pause.Visibility = Visibility.Collapsed;
        var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _generate = ActionButton("H3Generate", "Generate", () => Request(GenerateRequested));
        _generate.IsEnabled = GenerationAvailable; _generate.Style = (Style)FindResource("H3StartButton");
        _pause.Style = (Style)FindResource("H3PauseButton");
        _cancelExport.Style = (Style)FindResource("H3StopButton");
        _cancelExport.Content = "⛔"; _cancelExport.ToolTip = T("H3Cancel");
        System.Windows.Automation.AutomationProperties.SetName(_cancelExport, T("H3Cancel"));
        controls.Children.Add(_generate); controls.Children.Add(_pause); controls.Children.Add(_cancelExport);
        BuildScheduleMenu(); DockPanel.SetDock(controls, Dock.Right); footer.Children.Add(controls);
        _status = Label(T("H3WorkspaceNotice"));
        var information = new StackPanel(); information.Children.Add(_status);
        _progressSummary = Label(""); _progressSummary.FontSize = 12; AutomationProperties.SetAutomationId(_progressSummary, "VideoH3.Workspace.ProgressSummary"); information.Children.Add(_progressSummary);
        footer.Children.Add(information); Grid.SetRow(footer, 3); root.Children.Add(footer);
        BuildSettings(); BuildFiles();
        var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); layout.ColumnDefinitions.Add(new());
        _fluid = new H3FluidProgress { Margin = new Thickness(0, 0, 6, 0), ToolTip = T("H3ActivityHint") };
        AutomationProperties.SetAutomationId(_fluid, "VideoH3.Workspace.Progress");
        layout.Children.Add(_fluid); Grid.SetColumn(root, 1); layout.Children.Add(root);
        Content = layout; _building = false; RefreshRequestedSummary(); ReloadResults();
    }
    private void AddBody(UIElement element, int column) { Grid.SetColumn(element, column); _body.Children.Add(element); }
}
