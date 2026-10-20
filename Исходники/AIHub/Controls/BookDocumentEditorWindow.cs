using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services.Books;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Small block editor over a detached document. Unedited sections/assets never round-trip through plain text.</summary>
public sealed class BookDocumentEditorWindow : Window
{
    private readonly BookReviewDocument _document;
    private readonly Func<string, string> _l;
    private readonly ListBox _blocks = new() { DisplayMemberPath = nameof(BlockRow.Label), ItemTemplate = null };
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 17, Padding = new Thickness(16), UndoLimit = 200 };
    private readonly TextBox _search = new() { Width = 220, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Stack<Change> _undo = new(), _redo = new();
    private BlockRow? _selected;
    private bool _loading;
    private sealed record BlockRow(string Id, string Section, string Kind, string Text)
    { public string Label => Section + " · " + (Text.Length > 100 ? Text[..100] + "…" : Text).Replace('\r', ' ').Replace('\n', ' '); }
    private sealed record Change(string Id, string Before, string After);
    public BookReviewDocument? EditedDocument { get; private set; }

    public BookDocumentEditorWindow(Window owner, BookReviewDocument document, Func<string, string> localize, string? outputFormat = null)
    {
        _document = document.Clone(); _l = localize; Owner = owner;
        MusicWishUi.PrepareWindow(this, T("Title"), "BookConverter.Editor.Window", 1120);
        Resources.MergedDictionaries.Insert(0, owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/BookConversionResources.xaml", UriKind.Relative) });
        Height = 790; MinWidth = 740; MinHeight = 480;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        if (!string.IsNullOrWhiteSpace(outputFormat))
        {
            Title = string.Format(T("ResultTitle"), outputFormat.ToUpperInvariant());
            top.Children.Add(MusicWishUi.Text(string.Format(T("ResultHint"), outputFormat.ToUpperInvariant()), true));
        }
        top.Children.Add(MusicWishUi.Text(T("Hint")));
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) }; top.Children.Add(tools);
        tools.Children.Add(Action("Undo", Undo)); tools.Children.Add(Action("Redo", Redo));
        AutomationProperties.SetAutomationId(_search, "BookConverter.Editor.FindText");
        AutomationProperties.SetName(_search, T("FindHint")); _search.ToolTip = T("FindHint");
        tools.Children.Add(_search); tools.Children.Add(Action("Find", Find));
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Find(); e.Handled = true; } };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var apply = Action("Apply", () => { Commit(); EditedDocument = _document.Clone(); DialogResult = true; });
        buttons.Children.Add(apply);
        var cancel = Action("Cancel", () => { DialogResult = false; }); cancel.IsCancel = true;
        buttons.Children.Add(cancel); footer.Children.Add(buttons);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(290) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(6) }); body.ColumnDefinitions.Add(new());
        root.Children.Add(body); body.Children.Add(_blocks);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(_text, 2); body.Children.Add(_text);
        AutomationProperties.SetAutomationId(_blocks, "BookConverter.Editor.Blocks");
        AutomationProperties.SetAutomationId(_text, "BookConverter.Editor.Text");
        _text.TextChanged += (_, _) => { if (!_loading) _redo.Clear(); };
        _blocks.SelectionChanged += (_, _) => SelectBlock();
        RefreshRows();
        var hasText = _blocks.Items.Count > 0; _text.IsEnabled = hasText; apply.IsEnabled = hasText;
        if (hasText) _blocks.SelectedIndex = 0; else _status.Text = T("NoText");
    }

    private string T(string key) => _l("BookConverter.Editor." + key);
    private Button Action(string key, Action action) => MusicWishUi.Button(T(key), "BookConverter.Editor." + key, action);
    private void RefreshRows()
    {
        _blocks.ItemsSource = _document.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => new BlockRow(b.Id, b.SectionTitle, b.Kind.ToString(), b.Text)).ToArray();
    }
    private void SelectBlock()
    {
        if (_loading) return;
        Commit(); _selected = _blocks.SelectedItem as BlockRow;
        _loading = true;
        try
        {
            _text.Text = _selected is null ? "" : CurrentText(_selected.Id); ResetUndo();
            if (_selected is not null) _status.Text = T("Section") + ": " + _selected.Section;
        }
        finally { _loading = false; }
    }
    private string CurrentText(string id) => _document.Blocks.First(b => b.Id == id).Text;
    private void Commit()
    {
        if (_loading || _selected is null) return;
        var old = CurrentText(_selected.Id);
        if (old == _text.Text) return;
        _document.UpdateBlockText(_selected.Id, _text.Text);
        _undo.Push(new Change(_selected.Id, old, _text.Text)); _redo.Clear();
        _status.Text = T("Changed");
    }
    private void Undo()
    {
        if (_text.CanUndo) { _text.Undo(); return; }
        Commit();
        if (!_undo.TryPop(out var change)) return;
        _document.UpdateBlockText(change.Id, change.Before); _redo.Push(change); ShowBlock(change.Id);
    }
    private void Redo()
    {
        if (_text.CanRedo) { _text.Redo(); return; }
        if (!_redo.TryPop(out var change)) return;
        _document.UpdateBlockText(change.Id, change.After); _undo.Push(change); ShowBlock(change.Id);
    }
    private void ShowBlock(string id)
    {
        _loading = true;
        try
        {
            RefreshRows(); _selected = _blocks.Items.Cast<BlockRow>().FirstOrDefault(b => b.Id == id);
            _blocks.SelectedItem = _selected; _text.Text = _selected is null ? "" : CurrentText(id); ResetUndo();
            if (_selected is not null) _blocks.ScrollIntoView(_selected);
        }
        finally { _loading = false; }
    }
    private void Find()
    {
        var query = _search.Text.Trim(); if (query.Length == 0) return;
        Commit(); var rows = _blocks.Items.Cast<BlockRow>().ToArray(); if (rows.Length == 0) return;
        var start = Math.Max(0, _blocks.SelectedIndex);
        for (var offset = 0; offset < rows.Length; offset++)
        {
            var row = rows[(start + offset) % rows.Length];
            var text = CurrentText(row.Id);
            var from = offset == 0 ? Math.Min(text.Length, _text.SelectionStart + _text.SelectionLength) : 0;
            var index = text.IndexOf(query, from, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0 && offset == rows.Length - 1 && row.Id == _selected?.Id) index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) continue;
            ShowBlock(row.Id); _text.Focus(); _text.Select(index, query.Length); _text.ScrollToLine(_text.GetLineIndexFromCharacterIndex(index)); return;
        }
        // Wrap within the starting block after all following blocks were checked.
        var first = CurrentText(rows[start].Id).IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (first >= 0) { ShowBlock(rows[start].Id); _text.Focus(); _text.Select(first, query.Length); return; }
        _status.Text = T("NotFound");
    }
    private void ResetUndo() { _text.IsUndoEnabled = false; _text.IsUndoEnabled = true; }
}
