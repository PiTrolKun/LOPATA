using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AIHub.Services.MusicLibrary;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

/// <summary>Edits staged records only. Closing this window cannot mutate the catalog or source files.</summary>
internal sealed class LyricImportWindow : Window
{
    private readonly List<LyricEntry> _entries;
    private readonly ListBox _list = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    private readonly LyricEntryFields _fields;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, string> _l;
    private int _index;
    private bool _switching;
    public LyricEntry[]? Entries { get; private set; }
    public LyricImportWindow(Window owner, Func<string, string> localize, IEnumerable<LyricEntry> entries)
    {
        _entries = entries.ToList();
        if (_entries.Count == 0) throw new ArgumentException("No records to review.", nameof(entries));
        _l = localize; Owner = owner;
        MusicWishUi.PrepareWindow(this, T("ReviewImport"), "Music.Library.Import.Window", 1140);
        Resources.MergedDictionaries.Insert(0, owner.Resources);
        Height = 820; MinHeight = 600; MinWidth = 800;
        var body = new DockPanel { Margin = new(18) }; Content = body;
        var hint = MusicWishUi.Text(T("ReviewImportHint")); DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
        var footer = new StackPanel { Margin = new(0, 8, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(_error);
        AutomationProperties.SetAutomationId(_error, "Music.Library.Import.Error");
        var buttons = new WrapPanel(); footer.Children.Add(buttons);
        if (_entries.Count > 1) buttons.Children.Add(MusicWishUi.Button(T("FillEmpty"), "Music.Library.Import.FillEmpty", FillEmpty));
        var accept = MusicWishUi.Button(T("ConfirmImport"), "Music.Library.Import.Confirm", Confirm);
        accept.IsDefault = true; buttons.Children.Add(accept);
        var cancel = MusicWishUi.Button(T("CancelImport"), "Music.Library.Import.Cancel", () => DialogResult = false);
        cancel.IsCancel = true; buttons.Children.Add(cancel);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) }); body.Children.Add(grid);
        AutomationProperties.SetAutomationId(_list, "Music.Library.Import.Entries");
        _list.Margin = new(0, 0, 14, 0);
        _list.SetResourceReference(ListBox.BackgroundProperty, "PanelBrush"); _list.SetResourceReference(ListBox.ForegroundProperty, "TextPrimaryBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); _list.ItemTemplate = new DataTemplate { VisualTree = title };
        _list.ItemsSource = _entries; grid.Children.Add(_list);
        _fields = new(localize); Grid.SetColumn(_fields, 1); grid.Children.Add(_fields);
        _fields.Load(_entries[0]); _list.SelectedIndex = 0;
        _list.SelectionChanged += (_, _) => {
            if (_switching) return;
            var target = _list.SelectedIndex;
            if (target < 0) return;
            if (!Capture()) { RefreshList(_index); return; }
            _index = target; RefreshList(_index); _fields.Load(_entries[_index]);
        };
    }
    private string T(string key) => _l("Music.Library." + key);
    private bool Capture()
    {
        try { var entry = _fields.Read(); entry.Validate(); _entries[_index] = entry; _error.Text = ""; return true; }
        catch (Exception error) when (error is System.IO.InvalidDataException or ArgumentException)
        { _error.Text = _l(error.Message); return false; }
    }
    private void RefreshList(int index)
    {
        _switching = true;
        try { _list.Items.Refresh(); _list.SelectedIndex = index; }
        finally { _switching = false; }
    }
    private void FillEmpty()
    {
        if (!Capture()) return;
        var source = _entries[_index];
        for (var i = 0; i < _entries.Count; i++)
            if (i != _index) _entries[i] = LyricLibraryDraft.FillEmptyMetadata(_entries[i], source);
        RefreshList(_index);
    }
    private void Confirm()
    {
        if (!Capture()) return;
        try { foreach (var entry in _entries) entry.Validate(); }
        catch (System.IO.InvalidDataException error) { _error.Text = _l(error.Message); return; }
        Entries = _entries.ToArray(); DialogResult = true;
    }
}
