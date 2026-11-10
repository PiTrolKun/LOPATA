using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.SpeechAssets;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;

namespace AIHub.Controls.SpeechAssets;

/// <summary>Same managed-project workflow as MusicProjectsWindow; audio stays in durable internal sessions.</summary>
internal sealed class SpeechAssetProjectsWindow : Window
{
    private readonly SpeechAssetsControl _host;
    private readonly DataGrid _manual = Table("Manual"), _automatic = Table("Automatic");
    private readonly TextBox _name = new() { MaxLength = 200 }, _details = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _current = new() { Margin = new(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _open;
    private bool _refreshing;
    private sealed record Row(string Id, string Name, string Created, int Steps);

    internal SpeechAssetProjectsWindow(SpeechAssetsControl host)
    {
        _host = host;
        MusicWishUi.PrepareWindow(this, host.ProjectText("Title"), "SpeechAssets.Projects.Window", 880);
        if (Window.GetWindow(host) is { } owner) Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SpeechAssets/SpeechAssetsResources.xaml", UriKind.Relative) });
        MinWidth = 640;
        var root = new Grid { Margin = new(16) };
        for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new() { Height = i is 2 or 3 ? new(1, GridUnitType.Star) : i == 4 ? new(140) : GridLength.Auto });
        var header = new StackPanel(); header.Children.Add(_current);
        var rename = new DockPanel(); var renameButton = Action("Rename", () => host.RenameProject(_name.Text));
        DockPanel.SetDock(renameButton, Dock.Right); rename.Children.Add(renameButton); rename.Children.Add(_name);
        AutomationProperties.SetAutomationId(_name, "SpeechAssets.Projects.Name"); header.Children.Add(rename); root.Children.Add(header);
        var toolbar = new WrapPanel(); toolbar.Children.Add(Action("Fix", host.FixProject)); toolbar.Children.Add(Action("New", host.NewProject));
        _open = Action("Open", () => { if (Selected() is { } row) host.OpenSavedSession(row.Id); }); toolbar.Children.Add(_open);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        AddTable(_manual, "Manual", 2); AddTable(_automatic, "Automatic", 3);
        AutomationProperties.SetAutomationId(_details, "SpeechAssets.Projects.History"); Grid.SetRow(_details, 4); root.Children.Add(_details);
        var close = new Button { Content = host.ProjectText("Close"), Margin = new(4), Padding = new(12, 7, 12, 7), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); Grid.SetRow(close, 5); root.Children.Add(close);
        Content = root; Refresh();

        void AddTable(DataGrid table, string key, int row)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
            foreach (var (title, property, width) in new[] { ("Name", "Name", 1d), ("Created", "Created", 180d), ("Steps", "Steps", 85d) })
                table.Columns.Add(new DataGridTextColumn { Header = host.ProjectText(title), Binding = new System.Windows.Data.Binding(property),
                    Width = title == "Name" ? new(1, DataGridLengthUnitType.Star) : new(width), ElementStyle = style });
            var group = new System.Windows.Controls.GroupBox { Header = host.ProjectText(key), Content = table, Margin = new(0, 6, 0, 6) };
            Grid.SetRow(group, row); root.Children.Add(group);
            table.SelectionChanged += (_, _) =>
            {
                if (_refreshing) return;
                if (table.SelectedItem is not null) { _refreshing = true; (table == _manual ? _automatic : _manual).SelectedItem = null; _refreshing = false; }
                ShowDetails();
            };
        }
    }
    private static DataGrid Table(string key)
    {
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 70, RowHeight = 34, FontSize = 13, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        table.SetResourceReference(BackgroundProperty, "PanelBrush"); table.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        table.SetResourceReference(DataGrid.RowBackgroundProperty, "PanelBrush"); table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "LineBrush");
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("WindowBackgroundBrush")));
        header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(6))); table.ColumnHeaderStyle = header;
        AutomationProperties.SetAutomationId(table, "SpeechAssets.Projects." + key); return table;
    }
    private Row? Selected() => _manual.SelectedItem as Row ?? _automatic.SelectedItem as Row;
    private Button Action(string key, System.Action action)
    {
        var button = new Button { Content = _host.ProjectText(key), Margin = new(4), Padding = new(12, 7, 12, 7), IsEnabled = !_host.IsWorking };
        AutomationProperties.SetAutomationId(button, "SpeechAssets.Projects." + key);
        button.Click += (_, _) => { try { action(); Refresh(); } catch (Exception error) { _details.Text = error.Message; } };
        return button;
    }
    private void Refresh()
    {
        _refreshing = true;
        var list = _host.Projects();
        Row Make(SpeechAssetSession p) => new(p.Id, _host.ProjectName(p), p.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), p.Runs.Count);
        _manual.ItemsSource = list.Where(p => p.Manual).Select(Make).ToArray();
        _automatic.ItemsSource = list.Where(p => !p.Manual).Select(Make).ToArray();
        _current.Text = _host.ProjectName(_host.CurrentProject) + " — " + _host.ProjectText(_host.CurrentProject.Persistent ? "Saved" : "Temporary");
        _name.Text = _host.ProjectName(_host.CurrentProject); _name.IsReadOnly = _host.IsWorking;
        _refreshing = false; ShowDetails();
    }
    private void ShowDetails()
    {
        _open.IsEnabled = !_host.IsWorking && Selected() is not null;
        var project = Selected() is { } row ? _host.Projects().Single(p => p.Id == row.Id) : _host.CurrentProject;
        _details.Text = string.Format(_host.T("Counts"), project.Rows.Count(r => r.Included), project.Rows.Count(r => r.Included && r.Takes.Any(t => t.Text == r.Text)),
            project.Rows.Count(r => r.Included && r.Confirmed), project.Rows.Count(r => r.Included && !r.Confirmed)) + "\n\n" +
            string.Join("\n", project.Runs.AsEnumerable().Reverse().Select(run => run.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + " · " +
                (run.State == "CompletedWithErrors" ? _host.T("RunCompletedWithErrors") : _host.ProjectText(run.State)) + " · " +
                run.Job.Voice.Id + " · " + run.Job.Voice.Options.Speaker + " · " + run.Job.Items.Length));
    }
}
