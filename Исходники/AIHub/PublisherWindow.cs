using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AIHub.Models;
using AIHub.Services;
using DataGrid = System.Windows.Controls.DataGrid;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub;

public sealed class PublisherWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly PublisherCoordinator _service;
    private readonly IPublisherVkClient _vk;
    private readonly IPublisherGitHubClient _github;
    private readonly DataGrid _table;
    private readonly TextBlock _status = new();
    private readonly WrapPanel _actions = PublisherUi.Buttons();
    private bool _busy;
    public PublisherWindow(Window owner, Func<string, string> text, PublisherCoordinator service,
        IPublisherVkClient vk, IPublisherGitHubClient github)
    {
        _text = text; _service = service; _vk = vk; _github = github;
        PublisherUi.Prepare(this, owner, text("Publisher.Title"));
        Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(text("Publisher.Description")));
        header.Children.Add(PublisherUi.Text(text("Publisher.RuntimeHelp")));
        _table = CreateTable(); AutomationProperties.SetAutomationId(_table, "Publisher.Directions");
        Column(_table, text("Publisher.From"), "Repository"); Column(_table, text("Publisher.Where"), "Destination");
        Column(_table, text("Publisher.Mode"), "Mode"); Column(_table, text("Publisher.LastSourceChange"), "SourceChange");
        Column(_table, text("Publisher.LastPublication"), "Publication"); Column(_table, text("Publisher.State"), "Status");
        body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        body.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _table.MinWidth = 780;
        _table.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        body.Content = _table; footer.Children.Add(_status); footer.Children.Add(_actions);
        Add("Publisher.Add", () => new PublisherWizardWindow(this, text, service, vk, github).ShowDialog());
        Add("Publisher.Releases", () => { if (Selected() is { } id) new PublisherReleasesWindow(this, text, service, id).ShowDialog(); });
        var menu = new ContextMenu();
        menu.SetResourceReference(BackgroundProperty, "PanelBrush");
        menu.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        void Menu(string key, Action action)
        {
            var item = new MenuItem { Header = text(key) };
            AutomationProperties.SetAutomationId(item, key);
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Menu("Publisher.Check", () => _ = RunAsync(async () => { if (Selected() is { } id) await service.CheckAsync(id); }));
        Menu("Publisher.OpenSource", () =>
        {
            if (Selected() is not { } id) return;
            var d = service.Snapshot().Directions.Single(item => item.Id == id);
            PublisherUi.OpenLink("https://github.com/" + d.Repository + "/releases");
        });
        Menu("Publisher.Mode", Configure);
        Menu("Publisher.ReplaceToken", ReplaceToken);
        Menu("Publisher.LastPost", () =>
        {
            if (Selected() is not { } id) return;
            var d = service.Snapshot().Directions.Single(item => item.Id == id);
            var post = d.Publications.Where(p => p.Status == "published").MaxBy(p => p.PublishedAt);
            if (post?.PostId is { } postId) PublisherUi.OpenLink(PublisherPostFormatter.PostUrl(d.CommunityId, postId));
        });
        var actions = PublisherUi.Button(text("Publisher.Actions"), "Publisher.Actions", () => menu.IsOpen = true);
        menu.PlacementTarget = actions; actions.ContextMenu = menu; _actions.Children.Add(actions);
        Add("Publisher.Delete", () => _ = RunAsync(async () =>
        {
            if (Selected() is { } id && PublisherUi.Confirm(this, text, text("Publisher.DeleteConfirm"))) await service.DeleteAsync(id);
        }));
        Add("Publisher.Close", Close);
        _service.Changed += Changed; Closed += (_, _) => _service.Changed -= Changed; Refresh();
    }
    private void Add(string key, Action action) => _actions.Children.Add(PublisherUi.Button(_text(key), key, action));
    private Guid? Selected()
    {
        if (_table.SelectedItem is DirectionRow row) return row.Id;
        _status.Text = _text("Publisher.SelectDirection"); return null;
    }
    private void Changed() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh); }
    private void Refresh()
    {
        var selected = (_table.SelectedItem as DirectionRow)?.Id;
        var rows = _service.Snapshot().Directions.Select(d =>
        {
            var post = d.Publications.Where(p => p.Status == "published").MaxBy(p => p.PublishedAt);
            var key = d.Publications.Any(p => p.Status == "uncertain") ? "Publisher.Uncertain" : d.StatusKey;
            return new DirectionRow(d.Id, d.Repository, d.CommunityName + " (" + d.CommunityId + ")",
                _text(d.AutoPublish ? "Publisher.Automatic" : "Publisher.Manual"), Date(d.LastSourceChange),
                Date(post?.PublishedAt), _text(key));
        }).ToArray();
        _table.ItemsSource = rows; _table.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
        if (_service.FailureKey is { } failure) _status.Text = _text(failure);
    }
    private static string Date(DateTimeOffset? time) => time?.ToLocalTime().ToString("g") ?? "—";
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; _actions.IsEnabled = false; _status.Text = _text("Publisher.Checking");
        try { await action(); _status.Text = ""; Refresh(); }
        catch (Exception error) { _status.Text = _text(error is PublisherException known ? known.Key : "Publisher.NetworkError"); }
        finally { _busy = false; _actions.IsEnabled = true; }
    }
    private void Configure()
    {
        if (Selected() is not { } id) return;
        var d = _service.Snapshot().Directions.Single(item => item.Id == id);
        var dialog = new Window(); PublisherUi.Prepare(dialog, this, _text("Publisher.Mode"), 580, 440);
        dialog.Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(_text("Publisher.AutoWarning")));
        var panel = new StackPanel(); body.Content = panel;
        var auto = new CheckBox { Content = PublisherUi.Text(_text("Publisher.AutoMode")), IsChecked = d.AutoPublish };
        panel.Children.Add(auto); panel.Children.Add(PublisherUi.Text(_text("Publisher.CheckInterval")));
        var interval = new ComboBox(); foreach (var n in new[] { 15, 60, 360 }) interval.Items.Add(new ComboBoxItem { Content = string.Format(_text("Publisher.Minutes"), n), Tag = n });
        interval.SelectedIndex = d.CheckMinutes == 15 ? 0 : d.CheckMinutes == 360 ? 2 : 1; panel.Children.Add(interval);
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Save"), "Publisher.Mode.Save", () =>
        {
            if (auto.IsChecked == true && !d.AutoPublish && !PublisherUi.Confirm(dialog, _text, _text("Publisher.AutoWarning"))) return;
            dialog.DialogResult = true;
        }));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Cancel"), "Publisher.Mode.Cancel", dialog.Close));
        if (dialog.ShowDialog() == true) _ = RunAsync(() => _service.ConfigureAsync(id, auto.IsChecked == true, (int)((ComboBoxItem)interval.SelectedItem).Tag));
    }
    private void ReplaceToken()
    {
        if (Selected() is not { } id) return;
        var dialog = new Window(); PublisherUi.Prepare(dialog, this, _text("Publisher.ReplaceToken"), 580, 400);
        dialog.Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(_text("Publisher.SecurityHelp")));
        var input = new PasswordBox { Margin = new(4), Padding = new(10) }; body.Content = input;
        PublisherUi.StyleSecret(input);
        AutomationProperties.SetName(input, _text("Publisher.Token"));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.CheckSave"), "Publisher.Token.Save", () => dialog.DialogResult = true));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Cancel"), "Publisher.Token.Cancel", dialog.Close));
        if (dialog.ShowDialog() == true)
        { var secret = input.Password; input.Clear(); _ = RunAsync(() => _service.ReplaceTokenAsync(id, secret)); }
        else input.Clear();
    }
    internal static DataGrid CreateTable()
    {
        var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        table.SetResourceReference(BackgroundProperty, "PanelBrush"); table.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        table.SetResourceReference(DataGrid.RowBackgroundProperty, "PanelBrush"); table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "LineBrush");
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelBrush")));
        headerStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        headerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8)));
        table.ColumnHeaderStyle = headerStyle; return table;
    }
    internal static void Column(DataGrid table, string title, string path)
    {
        var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        textStyle.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        table.Columns.Add(new DataGridTextColumn { Header = PublisherUi.Text(title), Binding = new Binding(path), MinWidth = 130,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = textStyle });
    }
    private sealed record DirectionRow(Guid Id, string Repository, string Destination, string Mode, string SourceChange, string Publication, string Status);
}
