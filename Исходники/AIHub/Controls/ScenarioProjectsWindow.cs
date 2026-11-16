using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using GroupBox = System.Windows.Controls.GroupBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

internal sealed record ScenarioProjectRow(string Id, string Name, DateTimeOffset CreatedAt, int Steps, bool Manual, bool Persistent);
internal sealed record ScenarioProjectHistoryItem(string Caption, Action? OpenResult, Action? RestoreSettings)
{
    public override string ToString() => Caption;
}

internal sealed record ScenarioProjectCommands(
    string Prefix, Func<string, string> Text, Func<bool> Busy, Func<ScenarioProjectRow> Current,
    Func<IReadOnlyList<ScenarioProjectRow>> List, Action Fix, Action New, Action<string> Open,
    Action<string> Rename, Func<string, string>? Details = null,
    Func<string, IReadOnlyList<ScenarioProjectHistoryItem>>? History = null, Func<string, string>? ErrorText = null);

public class ScenarioProjectsWindow : Window
{
    private readonly ScenarioProjectCommands _projects;
    private readonly DataGrid _manual, _automatic;
    private readonly ListBox? _history;
    private readonly Button? _openResult, _restoreSettings;
    private readonly TextBox _name = new() { MaxLength = 200 }, _details = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _current = MusicWishUi.Text("");
    private readonly Button _open;
    private bool _refreshing;
    private sealed record Row(string Id, string Name, string Created, int Steps);
    internal ScenarioProjectsWindow(ScenarioProjectCommands projects)
    {
        _projects = projects;
        _manual = Table(Id("Manual"));
        _automatic = Table(Id("Automatic"));
        MusicWishUi.PrepareWindow(this, projects.Text("Title"), Id("Window"), 820);
        MinWidth = 600;
        var root = new Grid { Margin = new(16) }; for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new() {
            Height = i is 2 or 3 ? new GridLength(1, GridUnitType.Star) : i == 4 ? new GridLength(projects.History is null ? 120 : 178) : GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(_current);
        var rename = new Grid(); rename.ColumnDefinitions.Add(new()); rename.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(_name, Id("Name")); rename.Children.Add(_name);
        var renameButton = Action("Rename", () => projects.Rename(_name.Text)); Grid.SetColumn(renameButton, 1); rename.Children.Add(renameButton);
        header.Children.Add(rename); root.Children.Add(header);
        var toolbar = new WrapPanel(); toolbar.Children.Add(Action("Fix", projects.Fix)); toolbar.Children.Add(Action("New", projects.New));
        _open = Action("Open", () => { if (Selected() is { } row) projects.Open(row.Id); }); toolbar.Children.Add(_open);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        AddTable(_manual, "Manual", 2); AddTable(_automatic, "Automatic", 3);
        if (projects.History is null)
        {
            AutomationProperties.SetName(_details, projects.Text("Attempts"));
            Grid.SetRow(_details, 4); root.Children.Add(_details);
        }
        else
        {
            var historyPanel = new DockPanel { Margin = new(0, 4, 0, 4) };
            var label = MusicWishUi.Text(projects.Text("Attempts"));
            DockPanel.SetDock(label, Dock.Top); historyPanel.Children.Add(label);
            var actions = new WrapPanel();
            _openResult = Action("OpenResult", () => SelectedAttempt()?.OpenResult?.Invoke(), refresh: false);
            _restoreSettings = Action("RestoreSettings", () => SelectedAttempt()?.RestoreSettings?.Invoke());
            actions.Children.Add(_openResult); actions.Children.Add(_restoreSettings);
            DockPanel.SetDock(actions, Dock.Bottom); historyPanel.Children.Add(actions);
            _history = new ListBox { DisplayMemberPath = nameof(ScenarioProjectHistoryItem.Caption) };
            _history.SetResourceReference(BackgroundProperty, "PanelBrush");
            _history.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(_history, Id("History"));
            AutomationProperties.SetLabeledBy(_history, label);
            _history.SelectionChanged += (_, _) => RefreshHistoryActions();
            historyPanel.Children.Add(_history);
            Grid.SetRow(historyPanel, 4); root.Children.Add(historyPanel);
        }
        var close = MusicWishUi.Button(projects.Text("Close"), Id("Close"), Close);
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; Grid.SetRow(close, 5); root.Children.Add(close);
        Content = root; Refresh();
        void AddTable(DataGrid table, string key, int row) {
            var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(6)));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            table.Columns.Add(new DataGridTextColumn { Header = Heading("Name"), Binding = new System.Windows.Data.Binding("Name"),
                Width = new(1, DataGridLengthUnitType.Star), MinWidth = 180, ElementStyle = textStyle });
            table.Columns.Add(new DataGridTextColumn { Header = Heading("Created"), Binding = new System.Windows.Data.Binding("Created"), Width = 180, ElementStyle = textStyle });
            table.Columns.Add(new DataGridTextColumn { Header = Heading("Steps"), Binding = new System.Windows.Data.Binding("Steps"), Width = 90, ElementStyle = textStyle });
            var group = new GroupBox { Header = projects.Text(key), Content = table, Margin = new(0, 6, 0, 6) };
            Grid.SetRow(group, row); root.Children.Add(group);
            table.SelectionChanged += (_, _) => { if (_refreshing) return; if (table.SelectedItem is not null) {
                _refreshing = true; (table == _manual ? _automatic : _manual).SelectedItem = null; _refreshing = false; } ShowDetails(); };
        }
        TextBlock Heading(string key) => new() { Text = projects.Text(key), TextWrapping = TextWrapping.NoWrap };
    }
    private string Id(string key) => _projects.Prefix + "." + key;

    private static DataGrid Table(string automationId)
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 70,
            RowHeight = 34, FontSize = 13, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        table.SetResourceReference(BackgroundProperty, "PanelBrush"); table.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        table.SetResourceReference(DataGrid.RowBackgroundProperty, "PanelBrush"); table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "LineBrush");
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("WindowBackgroundBrush")));
        header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        header.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(6)));
        table.ColumnHeaderStyle = header;
        AutomationProperties.SetAutomationId(table, automationId); return table;
    }
    private Row? Selected() => _manual.SelectedItem as Row ?? _automatic.SelectedItem as Row;
    private Button Action(string key, System.Action action, bool refresh = true)
    {
        var button = MusicWishUi.Button(_projects.Text(key), Id(key), () => {
            try { action(); if (refresh) Refresh(); }
            catch (Exception error)
            {
                var message = _projects.ErrorText?.Invoke(error.Message) ?? error.Message;
                System.Windows.MessageBox.Show(this, message, _projects.Text("Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
        button.IsEnabled = !_projects.Busy(); return button;
    }
    private void Refresh()
    {
        _refreshing = true; var list = _projects.List();
        static Row Make(ScenarioProjectRow project) => new(project.Id, project.Name, project.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), project.Steps);
        _manual.ItemsSource = list.Where(p => p.Manual).Select(Make).ToArray();
        _automatic.ItemsSource = list.Where(p => !p.Manual).Select(Make).ToArray();
        var current = _projects.Current();
        _current.Text = current.Name + " — " + _projects.Text(current.Persistent ? "Saved" : "Temporary");
        _name.Text = current.Name; _name.IsEnabled = !_projects.Busy(); _refreshing = false; ShowDetails();
    }
    private void ShowDetails()
    {
        _open.IsEnabled = !_projects.Busy() && Selected() is not null;
        try
        {
            var projectId = Selected()?.Id ?? _projects.Current().Id;
            if (_history is null) _details.Text = _projects.Details?.Invoke(projectId) ?? "";
            else
            {
                _history.ItemsSource = _projects.History?.Invoke(projectId);
                _history.SelectedIndex = _history.Items.Count > 0 ? 0 : -1;
                RefreshHistoryActions();
            }
        }
        catch (Exception error)
        {
            _details.Text = _projects.Text("ReadError") + " " + error.Message;
            if (_history is not null)
                _history.ItemsSource = new[] { new ScenarioProjectHistoryItem(_details.Text, null, null) };
        }
    }

    private ScenarioProjectHistoryItem? SelectedAttempt() => _history?.SelectedItem as ScenarioProjectHistoryItem;

    private void RefreshHistoryActions()
    {
        if (_openResult is not null) _openResult.IsEnabled = SelectedAttempt()?.OpenResult is not null;
        if (_restoreSettings is not null) _restoreSettings.IsEnabled = !_projects.Busy() && SelectedAttempt()?.RestoreSettings is not null;
    }
}
