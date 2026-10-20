using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.MusicLibrary;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class LyricLibraryWindow : Window
{
    private readonly LyricLibraryStore _store;
    private readonly Func<string, string> _l;
    private readonly ListBox _list = new();
    private readonly TextBox _query = new() { MinWidth = 120, Padding = new(6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _source = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    private readonly LyricEntryFields _fields;
    private readonly List<Button> _actions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private LyricEntry? _selected, _deleted;
    private bool _busy, _closing, _selecting;
    public LyricEntry? ImportedEntry { get; private set; }
    public LyricLibraryWindow(Window? owner, Func<string, string> localize, LyricLibraryStore? store = null,
        bool allowImport = true, LyricEntry? draft = null)
    {
        _l = localize; _store = store ?? LyricLibraryStore.Default;
        MusicWishUi.PrepareWindow(this, T("Library"), "Music.Library.Window", 1180);
        Height = 820; MinWidth = 780; MinHeight = 540;
        if (owner is not null) { Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources); }
        var body = new DockPanel { Margin = new(18) };
        var head = new WrapPanel(); DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        Add(head, "AddFiles", AddFiles); Add(head, "New", () => { if (LeaveEntry()) Select(new() { Title = T("Untitled") }); });
        Add(head, "ExportLibrary", Export); Add(head, "Delete", Delete); Add(head, "Restore", Restore);
        head.Children.Add(MusicWishUi.Text(T("PrivateHint")));
        var footer = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Top); footer.Children.Add(buttons);
        Add(buttons, "Save", () => Save(false)); Add(buttons, "SaveVersion", () => Save(true));
        if (allowImport) Add(buttons, "Insert", Insert);
        Add(buttons, "Close", Close); footer.Children.Add(_status);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 240 });
        grid.ColumnDefinitions.Add(new() { Width = new(1.7, GridUnitType.Star), MinWidth = 340 }); body.Children.Add(grid);
        var left = new DockPanel { Margin = new(0, 0, 14, 0) };
        var search = new DockPanel { Margin = new(0, 0, 0, 8) }; DockPanel.SetDock(search, Dock.Top); left.Children.Add(search);
        AutomationProperties.SetAutomationId(_query, "Music.Library.Query");
        var searchActions = new WrapPanel(); DockPanel.SetDock(searchActions, Dock.Bottom); search.Children.Add(searchActions);
        Add(searchActions, "Search", Refresh); Add(searchActions, "SmartSearch", () => _ = SmartSearchAsync()); search.Children.Add(_query);
        _list.SetResourceReference(ListBox.BackgroundProperty, "PanelBrush"); _list.SetResourceReference(ListBox.ForegroundProperty, "TextPrimaryBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(_list, "Music.Library.Results");
        _list.SelectionChanged += (_, _) => {
            if (_selecting || _list.SelectedItem is not LyricEntry entry) return;
            if (LeaveEntry()) Select(entry);
            else { _selecting = true; _list.SelectedItem = _selected; _selecting = false; }
        };
        left.Children.Add(_list); grid.Children.Add(left);
        var right = new DockPanel(); Grid.SetColumn(right, 1); grid.Children.Add(right);
        DockPanel.SetDock(_source, Dock.Top); right.Children.Add(_source);
        _fields = new(localize); right.Children.Add(_fields); Content = body;
        Refresh(); Select(draft ?? new() { Title = T("Untitled") });
        Closing += (_, args) => {
            if (_busy) { args.Cancel = true; _closing = true; _lifetime.Cancel(); }
            else if (!_closing && !LeaveEntry()) args.Cancel = true;
        };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private string T(string key) => _l("Music.Library." + key);
    private void Add(System.Windows.Controls.Panel panel, string key, Action action)
    {
        var button = MusicWishUi.Button(T(key), "Music.Library." + key, action);
        panel.Children.Add(button); _actions.Add(button);
    }
    private void Select(LyricEntry entry)
    {
        _selected = entry; _fields.Load(entry);
        _source.Text = T("Origin") + ": " + (entry.Origin.Contains("model history unknown", StringComparison.Ordinal)
            ? T("UnknownHistory") : entry.Origin);
        if (entry.Models.Length > 0) _source.Text += "\n" + string.Join("; ", entry.Models.Select(m => m.Name + " · " + m.Role));
    }
    private void Refresh()
    {
        try { _list.ItemsSource = _store.Search(_query.Text); _status.Text = string.Format(T("Count"), _store.Snapshot().Entries.Length); }
        catch (Exception error) { Error(error); }
    }
    private void Error(Exception error) => _status.Text = T("Failed") + " " + _l(error.Message);
    private bool LeaveEntry()
    {
        if (_selected is null || _fields.Read() == _selected) return true;
        var answer = MessageBox.Show(this, T("SaveChanges"), T("Library"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && Save(false);
    }
    private void Insert()
    {
        if (_selected is not { Revision: > 0 } entry) { _status.Text = T("ChooseStored"); return; }
        if (_fields.Read() != entry) { _status.Text = T("ChooseStored"); return; }
        var current = _store.Find(entry.Id);
        if (current is null || current.Revision != entry.Revision) { Error(new InvalidOperationException("Music.Library.Changed")); return; }
        ImportedEntry = current; DialogResult = true;
    }
}
