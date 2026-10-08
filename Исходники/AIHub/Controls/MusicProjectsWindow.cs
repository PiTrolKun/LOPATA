using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using GroupBox = System.Windows.Controls.GroupBox;

namespace AIHub.Controls;

public sealed class MusicProjectsWindow : Window
{
    private readonly MusicProjectController _projects;
    private readonly DataGrid _manual = Table("Manual"), _automatic = Table("Automatic");
    private readonly TextBox _name = new() { MaxLength = 200 }, _details = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _current = MusicWishUi.Text("");
    private readonly Button _open;
    private bool _refreshing;
    private sealed record Row(string Id, string Name, string Created, int Steps);
    public MusicProjectsWindow(MusicProjectController projects, Func<string, string> localize)
    {
        _projects = projects; MusicWishUi.PrepareWindow(this, projects.Text("Title"), "Music.Projects.Window", 820);
        MinWidth = 600;
        var root = new Grid { Margin = new(16) }; for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new() {
            Height = i is 2 or 3 ? new GridLength(1, GridUnitType.Star) : i == 4 ? new GridLength(120) : GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(_current);
        var rename = new Grid(); rename.ColumnDefinitions.Add(new()); rename.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(_name, "Music.Projects.Name"); rename.Children.Add(_name);
        var renameButton = Action("Rename", () => projects.Rename(_name.Text)); Grid.SetColumn(renameButton, 1); rename.Children.Add(renameButton);
        header.Children.Add(rename); root.Children.Add(header);
        var toolbar = new WrapPanel(); toolbar.Children.Add(Action("Fix", projects.Fix)); toolbar.Children.Add(Action("New", projects.New));
        _open = Action("Open", () => { if (Selected() is { } row) projects.Open(row.Id); }); toolbar.Children.Add(_open);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        AddTable(_manual, "Manual", 2); AddTable(_automatic, "Automatic", 3);
        AutomationProperties.SetName(_details, projects.Text("Attempts")); Grid.SetRow(_details, 4); root.Children.Add(_details);
        var close = MusicWishUi.Button(projects.Text("Close"), "Music.Projects.Close", Close);
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
    private static DataGrid Table(string key)
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
        AutomationProperties.SetAutomationId(table, "Music.Projects." + key); return table;
    }
    private Row? Selected() => _manual.SelectedItem as Row ?? _automatic.SelectedItem as Row;
    private Button Action(string key, System.Action action)
    {
        var button = MusicWishUi.Button(_projects.Text(key), "Music.Projects." + key, () => {
            try { action(); Refresh(); }
            catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, _projects.Text("Title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
        button.IsEnabled = !_projects.Busy; return button;
    }
    private void Refresh()
    {
        _refreshing = true; var list = _projects.List();
        Row Make(MusicProject project) => new(project.Id, _projects.Name(project), project.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), project.Steps.Length);
        _manual.ItemsSource = list.Where(p => p.Manual).Select(Make).ToArray();
        _automatic.ItemsSource = list.Where(p => !p.Manual).Select(Make).ToArray();
        _current.Text = _projects.Name(_projects.Current) + " — " + _projects.Text(_projects.Current.Persistent ? "Saved" : "Temporary");
        _name.Text = _projects.Name(_projects.Current); _name.IsEnabled = !_projects.Busy; _refreshing = false; ShowDetails();
    }
    private void ShowDetails()
    {
        _open.IsEnabled = !_projects.Busy && Selected() is not null;
        try {
            var project = Selected() is { } row ? _projects.List().Single(p => p.Id == row.Id) : _projects.Current;
            _details.Text = string.Join("\n", project.Steps.Reverse().Select(s =>
                string.Format(_projects.Text("Step"), s.Number, project.Steps.Length) + " · " + s.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                + " · " + _projects.Text(s.Outcome.ToString()) + (s.Message.Length > 0 ? " — " + s.Message : "")));
        }
        catch (Exception error) { _details.Text = _projects.Text("ReadError") + " " + error.Message; }
    }
}
