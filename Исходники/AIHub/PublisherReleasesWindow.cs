using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;

namespace AIHub;

public sealed class PublisherReleasesWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly PublisherCoordinator _service;
    private readonly Guid _id;
    private readonly CheckBox _all = new();
    private readonly DataGrid _table;
    private readonly TextBlock _status = new(), _queue = new();
    private readonly WrapPanel _buttons = PublisherUi.Buttons();
    private bool _busy;
    public PublisherReleasesWindow(Window owner, Func<string, string> text, PublisherCoordinator service, Guid id)
    {
        _text = text; _service = service; _id = id;
        PublisherUi.Prepare(this, owner, text("Publisher.Releases"));
        Content = PublisherUi.Frame(out var header, out var body, out var footer);
        _all.Content = PublisherUi.Text(text("Publisher.ShowAll"));
        AutomationProperties.SetAutomationId(_all, "Publisher.Releases.ShowAll");
        _all.Checked += (_, _) => Refresh(); _all.Unchecked += (_, _) => Refresh();
        header.Children.Add(_all); header.Children.Add(PublisherUi.Text(text("Publisher.SelectionHelp"))); header.Children.Add(_queue);
        _table = PublisherWindow.CreateTable(); _table.IsReadOnly = false;
        AutomationProperties.SetAutomationId(_table, "Publisher.Releases.List");
        var selectionStyle = new Style(typeof(CheckBox), (Style)FindResource(typeof(CheckBox)));
        selectionStyle.Setters.Add(new Setter(IsHitTestVisibleProperty, true));
        selectionStyle.Setters.Add(new Setter(FocusableProperty, true));
        _table.Columns.Add(new DataGridCheckBoxColumn { Header = text("Publisher.Select"), Binding = new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70, ElementStyle = selectionStyle, EditingElementStyle = selectionStyle });
        PublisherWindow.Column(_table, text("Publisher.Release"), "Title"); PublisherWindow.Column(_table, text("Publisher.Date"), "Date");
        PublisherWindow.Column(_table, text("Publisher.State"), "Status");
        foreach (var column in _table.Columns.Skip(1)) column.IsReadOnly = true;
        body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        body.Content = _table; footer.Children.Add(_status); footer.Children.Add(_buttons);
        Add("Publisher.PreviewPublish", () => Preview(gradual: false));
        Add("Publisher.Gradual", () => Preview(gradual: true));
        Add("Publisher.Check", () => _ = RunAsync(() => service.CheckAsync(id)));
        Add("Publisher.QueuePause", () => _ = RunAsync(() => service.QueueControlAsync(id, true)));
        Add("Publisher.QueueResume", () =>
        { if (PublisherUi.Confirm(this, text, text("Publisher.ResumeConfirm"))) _ = RunAsync(() => service.QueueControlAsync(id, false)); });
        Add("Publisher.QueueClear", () =>
        { if (PublisherUi.Confirm(this, text, text("Publisher.ClearConfirm"))) _ = RunAsync(() => service.QueueControlAsync(id, true, clear: true)); });
        Add("Publisher.Resolve", Resolve);
        Add("Publisher.Log", ShowLog);
        Add("Publisher.Close", Close);
        service.Changed += Changed; Closed += (_, _) => service.Changed -= Changed; Refresh();
    }
    private void Add(string key, Action action) => _buttons.Children.Add(PublisherUi.Button(_text(key), key, action));
    private PublisherDirection Direction() => _service.Snapshot().Directions.FirstOrDefault(d => d.Id == _id)
        ?? throw new PublisherException("Publisher.DirectionMissing");
    private void Changed() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh); }
    private void Refresh()
    {
        if (_table is null) return;
        var d = _service.Snapshot().Directions.FirstOrDefault(item => item.Id == _id); if (d is null) { Close(); return; }
        var selected = (_table.ItemsSource as ReleaseRow[])?.Where(r => r.Selected).Select(r => r.Id).ToHashSet() ?? [];
        var rows = d.Releases.Where(r => _all.IsChecked == true || (!d.InitialReleaseIds.Contains(r.Id)
            && !d.Publications.Any(p => p.ReleaseId == r.Id && p.Status == "published")))
            .OrderBy(r => r.PublishedAt).ThenBy(r => r.Id).Select(r =>
        {
            var attempt = d.Publications.LastOrDefault(p => p.ReleaseId == r.Id);
            var key = attempt?.Status switch { "published" => "Publisher.Published", "uncertain" => "Publisher.Uncertain",
                "sending" => "Publisher.Sending", "failed" => "Publisher.Failed", _ => d.Queue.Contains(r.Id) ? "Publisher.Queued" : "Publisher.NotPublished" };
            return new ReleaseRow { Id = r.Id, Title = r.Title, Date = r.PublishedAt.ToLocalTime().ToString("g"), Status = _text(key), Selected = selected.Contains(r.Id) };
        }).ToArray();
        _table.ItemsSource = rows;
        _queue.Text = string.Format(_text("Publisher.QueueSummary"), d.Queue.Count,
            _text(d.QueuePaused ? "Publisher.Paused" : "Publisher.Active"), d.NextPublication?.ToLocalTime().ToString("g") ?? "—");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; _buttons.IsEnabled = false; _table.IsEnabled = false;
        try { await action(); _status.Text = _text(Direction().StatusKey); Refresh(); }
        catch (Exception error) { _status.Text = _text(error is PublisherException known ? known.Key : "Publisher.NetworkError"); }
        finally { _busy = false; _buttons.IsEnabled = true; _table.IsEnabled = true; }
    }
    private void Preview(bool gradual)
    {
        _table.CommitEdit(DataGridEditingUnit.Cell, true); _table.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = (_table.ItemsSource as ReleaseRow[])?.Where(r => r.Selected).Select(r => r.Id).ToArray() ?? [];
        if (selected.Length == 0) { _status.Text = _text("Publisher.SelectReleases"); return; }
        var d = Direction();
        if (d.Publications.Any(p => selected.Contains(p.ReleaseId) && p.Status is "uncertain" or "sending"))
        { _status.Text = _text("Publisher.Uncertain"); return; }
        var repeat = d.Publications.Any(p => selected.Contains(p.ReleaseId) && p.Status == "published");
        if (repeat && !PublisherUi.Confirm(this, _text, _text("Publisher.RepeatConfirm"))) return;
        var dialog = new Window(); PublisherUi.Prepare(dialog, this, _text("Publisher.Preview"), 760, 700);
        dialog.Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(d.CommunityName + " ← " + d.Repository));
        var panel = new StackPanel(); body.Content = panel;
        foreach (var release in d.Releases.Where(r => selected.Contains(r.Id)).OrderBy(r => r.PublishedAt).ThenBy(r => r.Id))
        {
            var content = PublisherUi.Input("Publisher.Preview.Text." + release.Id, _text("Publisher.Preview"),
                PublisherPostFormatter.Format(d.Repository, release, _text("Publisher.Shortened")));
            content.IsReadOnly = true; content.TextWrapping = TextWrapping.Wrap; content.AcceptsReturn = true; panel.Children.Add(content);
        }
        var hours = new ComboBox(); foreach (var n in new[] { 1, 6, 12, 24, 48, 168 }) hours.Items.Add(new ComboBoxItem { Content = string.Format(_text("Publisher.Hours"), n), Tag = n });
        hours.SelectedIndex = 3;
        if (gradual)
        { footer.Children.Add(PublisherUi.Text(_text("Publisher.GradualHelp"))); footer.Children.Add(hours); }
        footer.Children.Add(PublisherUi.Button(_text(gradual ? "Publisher.StartQueue" : "Publisher.Publish"), "Publisher.Preview.Confirm", () => dialog.DialogResult = true));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Cancel"), "Publisher.Preview.Cancel", dialog.Close));
        if (dialog.ShowDialog() != true) return;
        _ = RunAsync(async () =>
        {
            if (gradual) await _service.EnqueueAsync(_id, selected, (int)((ComboBoxItem)hours.SelectedItem).Tag, repeat);
            else foreach (var release in d.Releases.Where(r => selected.Contains(r.Id)).OrderBy(r => r.PublishedAt).ThenBy(r => r.Id))
            {
                await _service.PublishAsync(_id, release.Id, _text("Publisher.Shortened"), repeat);
                if (Direction().Publications.LastOrDefault()?.Status != "published") break;
            }
        });
    }
    private void Resolve()
    {
        if (_table.SelectedItem is not ReleaseRow row) { _status.Text = _text("Publisher.SelectReleases"); return; }
        var d = Direction();
        if (!d.Publications.Any(p => p.ReleaseId == row.Id && p.Status == "uncertain")) { _status.Text = _text("Publisher.NothingUncertain"); return; }
        var dialog = new Window(); PublisherUi.Prepare(dialog, this, _text("Publisher.Resolve"), 680, 440);
        dialog.Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(_text("Publisher.ResolveHelp")));
        var input = PublisherUi.Input("Publisher.Resolve.Link", _text("Publisher.PostLink")); body.Content = input;
        var status = PublisherUi.Text(""); footer.Children.Add(status);
        long? postId = null;
        footer.Children.Add(PublisherUi.Button(_text("Publisher.ConfirmExisting"), "Publisher.Resolve.Existing", () =>
        {
            var match = Regex.Match(input.Text.Trim(), @"\Ahttps://vk\.(?:ru|com)/wall-" + d.CommunityId + @"_([1-9][0-9]*)\z");
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var id)) { status.Text = _text("Publisher.InvalidPostLink"); return; }
            postId = id; if (PublisherUi.Confirm(dialog, _text, _text("Publisher.ExistingConfirm"))) dialog.DialogResult = true;
        }));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.ConfirmAbsent"), "Publisher.Resolve.Absent", () =>
        { if (PublisherUi.Confirm(dialog, _text, _text("Publisher.AbsentConfirm"))) { postId = null; dialog.DialogResult = true; } }));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Cancel"), "Publisher.Resolve.Cancel", dialog.Close));
        if (dialog.ShowDialog() == true) _ = RunAsync(() => _service.ResolveAsync(_id, row.Id, postId));
    }
    private void ShowLog()
    {
        var dialog = new Window(); PublisherUi.Prepare(dialog, this, _text("Publisher.Log"), 680, 560);
        dialog.Content = PublisherUi.Frame(out _, out var body, out var footer);
        body.Content = PublisherUi.Text(string.Join("\n", Direction().Events.AsEnumerable().Reverse().Select(e =>
            e.Time.ToLocalTime().ToString("g") + " — " + _text(e.Key) + (e.ReleaseId is { } id ? " (#" + id + ")" : ""))));
        footer.Children.Add(PublisherUi.Button(_text("Publisher.Close"), "Publisher.Log.Close", dialog.Close)); dialog.ShowDialog();
    }
    public sealed class ReleaseRow
    {
        public long Id { get; set; }
        public bool Selected { get; set; }
        public string Title { get; set; } = "";
        public string Date { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
