using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;
using ListViewItem = System.Windows.Controls.ListViewItem;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    public static readonly DependencyProperty AreSourcesEditableProperty = DependencyProperty.Register(
        nameof(AreSourcesEditable), typeof(bool), typeof(BookConversionWindow), new PropertyMetadata(true));
    public bool AreSourcesEditable => (bool)GetValue(AreSourcesEditableProperty);
    private const string SourceDragFormat = "AIHub.BookConverter.SourceRow";
    private sealed record SourceDrag(BookConversionWindow Owner, Entry Item);
    private Button? _removeSources;
    private CheckBox? _checkAll;
    private TextBlock? _sourceSelectionCount;
    private bool _changingChecks, _dragging;
    private Point _sourcePress;
    private Entry? _pressedSource, _dropSource;
    private DateTime _lastDragScroll;

    private void BuildSourceToolbar(Panel toolbar)
    {
        var add = Action("AddSources", () => Task.CompletedTask);
        var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
        menu.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        foreach (var (key, action) in new (string, Func<Task>)[] { ("AddFiles", AddFilesDialogAsync), ("AddFolder", AddFolderDialogAsync) })
        {
            var item = new MenuItem { Header = L(key) };
            item.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
            item.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(item, "BookConverter.Sources." + key);
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch (OperationCanceledException) { _status.Text = L("Cancelled"); }
                catch (Exception error) { _status.Text = L("Error"); _report.Text = Explain(error.Message); }
            };
            menu.Items.Add(item);
        }
        add.ToolTip = L("AddSourcesHint"); add.ContextMenu = menu;
        add.Click += (_, _) => { if (!_busy) menu.IsOpen = true; };
        _editButtons.Add(add); toolbar.Children.Add(add);
        _removeSources = Action("Remove", () =>
        {
            if (!_busy)
                foreach (var entry in _entries.Where(row => row.IsChecked).ToArray())
                { entry.PropertyChanged -= SourceEntryChanged; _entries.Remove(entry); }
            RefreshFormats(); return Task.CompletedTask;
        });
        _removeSources.ToolTip = L("RemoveMarkedHint");
        _editButtons.Add(_removeSources); toolbar.Children.Add(_removeSources);
        _checkAll = new CheckBox { Content = L("CheckAll"), Style = (Style)FindResource("BookSourceCheckStyle"), Margin = new Thickness(12, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_checkAll, "BookConverter.CheckAll");
        _checkAll.Click += (_, _) => SetAllSourcesChecked(_entries.Count > 0 && !_entries.All(row => row.IsChecked));
        toolbar.Children.Add(_checkAll);
        _sourceSelectionCount = MusicWishUi.Text(""); _sourceSelectionCount.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_sourceSelectionCount);
    }

    private void ConfigureSourceList()
    {
        _files.ItemsSource = _entries;
        _files.AllowDrop = true;
        AutomationProperties.SetAutomationId(_files, "BookConverter.Files");
        _files.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "InputBrush");
        _files.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        _files.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "LineBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_files, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_files, ScrollBarVisibility.Auto);
        Resources["BookSourceDragHint"] = L("ReorderHint");
        var header = new Style(typeof(GridViewColumnHeader));
        header.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new DynamicResourceExtension("PanelBrush")));
        header.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        header.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, new DynamicResourceExtension("LineBrush")));
        header.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(8)));
        header.Setters.Add(new Setter(IsHitTestVisibleProperty, false)); // Fixed responsive columns; no hidden horizontal overflow after header resizing.
        _files.Resources[typeof(GridViewColumnHeader)] = header;
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "", Width = 60, CellTemplate = (DataTemplate)FindResource("BookSourceMarkTemplate") });
        view.Columns.Add(new GridViewColumn { Header = L("File"), CellTemplate = (DataTemplate)FindResource("BookSourceNameTemplate") });
        view.Columns.Add(new GridViewColumn { Header = L("Status"), Width = 120, CellTemplate = (DataTemplate)FindResource("BookSourceStatusTemplate") });
        var rowStyle = new Style(typeof(ListViewItem), (Style)FindResource("BookConversionRowStyle"));
        rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(Entry.ToolTip))));
        _files.ItemContainerStyle = rowStyle; _files.View = view;
        _files.SizeChanged += (_, _) =>
        {
            var available = Math.Max(0, _files.ActualWidth - 44);
            view.Columns[2].Width = Math.Clamp(available * .22, 80, 130);
            view.Columns[1].Width = Math.Max(0, available - view.Columns[0].Width - view.Columns[2].Width);
        };
        _files.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressedSource = null;
            if (_busy || !IsSourceHandle(e.OriginalSource as DependencyObject)) return;
            _pressedSource = SourceContainer(e.OriginalSource as DependencyObject)?.DataContext as Entry;
            _sourcePress = e.GetPosition(_files);
        };
        _files.PreviewMouseLeftButtonUp += (_, _) => _pressedSource = null;
        _files.PreviewMouseMove += (_, e) =>
        {
            if (_busy || _dragging || _pressedSource is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(_files);
            if (Math.Abs(point.X - _sourcePress.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _sourcePress.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true; _pressedSource = null; e.Handled = true;
            try { System.Windows.DragDrop.DoDragDrop(_files, new DataObject(SourceDragFormat, new SourceDrag(this, item)), DragDropEffects.Move); }
            finally { _dragging = false; SetDropSource(null); }
        };
        _files.DragOver += SourceDragOver;
        _files.DragLeave += (_, _) => SetDropSource(null);
        _files.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(SourceDragFormat)) return; // Explorer files still bubble to the existing file/folder import.
            e.Handled = true;
            try
            {
                if (DraggedSource(e.Data) is not { } item) return;
                var point = e.GetPosition(_files);
                var container = SourceContainer(_files.InputHitTest(point) as DependencyObject);
                var insertion = container?.DataContext is Entry target ? _entries.IndexOf(target) + (e.GetPosition(container).Y >= container.ActualHeight / 2 ? 1 : 0) : point.Y < 40 ? 0 : _entries.Count;
                ReorderSource(item, insertion); e.Effects = DragDropEffects.Move;
            }
            finally { SetDropSource(null); }
        };
        _files.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_busy || Keyboard.Modifiers != ModifierKeys.Alt || _files.SelectedItem is not Entry item || key is not (Key.Up or Key.Down)) return;
            var index = _entries.IndexOf(item);
            ReorderSource(item, key == Key.Up ? Math.Max(0, index - 1) : Math.Min(_entries.Count, index + 2)); e.Handled = true;
        };
    }

    private Entry? DraggedSource(IDataObject data) => !_busy && data.GetData(SourceDragFormat) is SourceDrag drag && ReferenceEquals(drag.Owner, this) && _entries.Contains(drag.Item) ? drag.Item : null;
    private void SourceDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SourceDragFormat)) return;
        e.Handled = true; e.Effects = DraggedSource(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
        SetDropSource(e.Effects == DragDropEffects.Move ? SourceContainer(_files.InputHitTest(e.GetPosition(_files)) as DependencyObject)?.DataContext as Entry : null);
        if (e.Effects != DragDropEffects.Move || DateTime.UtcNow - _lastDragScroll < TimeSpan.FromMilliseconds(100)) return;
        var point = e.GetPosition(_files); var scroll = SourceScroll(_files);
        if (point.Y < 40) scroll?.LineUp(); else if (point.Y > _files.ActualHeight - 30) scroll?.LineDown();
        _lastDragScroll = DateTime.UtcNow;
    }
    private void SetDropSource(Entry? item)
    {
        if (ReferenceEquals(_dropSource, item)) return;
        if (_dropSource is not null) _dropSource.IsDropTarget = false;
        _dropSource = item; if (item is not null) item.IsDropTarget = true;
    }
    private void ReorderSource(Entry item, int insertion)
    {
        if (_busy || !_entries.Contains(item)) return;
        var from = _entries.IndexOf(item); if (insertion > from) insertion--;
        var to = Math.Clamp(insertion, 0, _entries.Count - 1);
        if (to != from) _entries.Move(from, to);
        _files.SelectedItem = item; _files.ScrollIntoView(item); _status.Text = L("OrderChanged");
    }
    private ListViewItem? SourceContainer(DependencyObject? value) => value is null ? null : ItemsControl.ContainerFromElement(_files, value) as ListViewItem;
    private static DependencyObject? SourceParent(DependencyObject value) => value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
    private static bool IsSourceHandle(DependencyObject? value)
    {
        while (value is not null)
        { if (value is FrameworkElement { Tag: "BookSourceDragHandle" }) return true; value = SourceParent(value); }
        return false;
    }
    private static ScrollViewer? SourceScroll(DependencyObject value)
    {
        if (value is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) if (SourceScroll(VisualTreeHelper.GetChild(value, i)) is { } found) return found;
        return null;
    }
    private void SourceEntryChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(Entry.IsChecked) && !_changingChecks) RefreshFormats(); }
    private void SetAllSourcesChecked(bool value)
    {
        if (_busy) return;
        _changingChecks = true;
        try { foreach (var item in _entries) item.IsChecked = value; }
        finally { _changingChecks = false; }
        RefreshFormats();
    }
    private void RefreshSourceSelection()
    {
        var count = _entries.Count(row => row.IsChecked);
        if (_checkAll is not null)
        { _checkAll.IsChecked = count == 0 ? false : count == _entries.Count ? true : null; _checkAll.IsEnabled = !_busy && _entries.Count > 0; }
        if (_sourceSelectionCount is not null) _sourceSelectionCount.Text = string.Format(L("CheckedCount"), count, _entries.Count);
    }
}
