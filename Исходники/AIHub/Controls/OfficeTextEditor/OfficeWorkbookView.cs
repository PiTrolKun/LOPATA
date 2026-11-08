using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AIHub.Services.OfficeEditor;
using Control = System.Windows.Controls.Control;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Binding = System.Windows.Data.Binding;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeWorkbookView : DockPanel, IDisposable
{
    private const int SpareRows = 100, ColumnsPerPage = 64;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, CanUserReorderColumns = false, CanUserSortColumns = false,
        EnableColumnVirtualization = true, EnableRowVirtualization = true, SelectionUnit = DataGridSelectionUnit.Cell,
        SelectionMode = DataGridSelectionMode.Extended, RowHeaderWidth = 62 };
    private readonly TextBox _address = new() { Width = 105 };
    private readonly TextBox _formula = new();
    private readonly StackPanel _tabs = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private readonly TextBlock _notice = new() { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    private readonly Func<string, string> _localize;
    private OfficeWorkbook? _book;
    private WorkbookSheet? _sheet;
    private int _firstColumn = 1;
    private WorkbookAddress _current = new(1, 1);
    private int _savedRevision, _exportRevision;
    private bool _rendering, _formulaChanged;
    internal event Action? Changed;
    internal bool HasChanges => _book != null && _book.Revision != _savedRevision;
    private string T(string key) => _localize("OfficeEditor." + key);

    internal OfficeWorkbookView(Func<string, string> localize)
    {
        _localize = localize;
        AutomationProperties.SetAutomationId(this, "Office.Workbook.View");
        AutomationProperties.SetAutomationId(_grid, "Office.Workbook.Grid");
        var top = new DockPanel { Margin = new Thickness(3) };
        SetDock(top, Dock.Top); Children.Add(top);
        _address.ToolTip = T("WorkbookAddress");
        AutomationProperties.SetAutomationId(_address, "Office.Workbook.Address");
        _address.KeyDown += GoToAddress;
        top.Children.Add(_address);
        top.Children.Add(new TextBlock { Text = "ƒx", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        _formula.ToolTip = T("WorkbookValue");
        AutomationProperties.SetAutomationId(_formula, "Office.Workbook.Formula");
        _formula.TextChanged += (_, _) => { if (!_rendering) _formulaChanged = true; };
        _formula.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (CommitFormula()) _grid.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowCell(); _grid.Focus(); e.Handled = true; }
        };
        _formula.LostKeyboardFocus += (_, _) => CommitFormula();
        top.Children.Add(_formula);
        BuildCellCommands();
        InstallFormulaPicking();
        SetDock(_notice, Dock.Top); Children.Add(_notice); _notice.Visibility = Visibility.Collapsed;
        var tabs = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(tabs, "Office.Workbook.Tabs");
        AutomationProperties.SetName(tabs, T("WorkbookSheets"));
        SetDock(tabs, Dock.Bottom); Children.Add(tabs);
        Children.Add(_grid);
        _grid.SetResourceReference(BackgroundProperty, "ReaderPanel");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "ReaderPanel");
        _grid.SetResourceReference(DataGrid.ForegroundProperty, "ReaderText");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "ReaderLine");
        _grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "ReaderLine");
        foreach (var type in new[] { typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), typeof(System.Windows.Controls.Primitives.DataGridRowHeader) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ReaderPanel")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("ReaderText")));
            _grid.Resources[type] = style;
        }
        _grid.LoadingRow += (_, e) => e.Row.Header = (1 + e.Row.GetIndex()).ToString(CultureInfo.InvariantCulture);
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (_rendering || _grid.CurrentCell.Column == null || _grid.CurrentCell.Item is not OfficeWorkbookRows.GridRow row) return;
            _current = AddressOf(row, _grid.CurrentCell.Column);
            ShowCell();
        };
        _grid.BeginningEdit += (_, e) => e.Cancel = _book?.CanEdit(AddressOf((OfficeWorkbookRows.GridRow)e.Row.Item, e.Column)) != true;
        _grid.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox box && _book != null)
            { _cellEditor = box; box.Text = _book.CellAt(AddressOf((OfficeWorkbookRows.GridRow)e.Row.Item, e.Column)).Input; box.SelectAll(); }
        };
        _grid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit && e.EditingElement is TextBox box)
                e.Cancel = !Apply(AddressOf((OfficeWorkbookRows.GridRow)e.Row.Item, e.Column), box.Text);
            if (!e.Cancel) _cellEditor = null;
        };
    }

    internal async Task LoadWorkbookAsync(string path) => await LoadWorkbookAsync(await File.ReadAllBytesAsync(path));
    internal async Task LoadWorkbookAsync(byte[] bytes)
    {
        var firstName = T("WorkbookSheet") + "1";
        var loaded = await Task.Run(() => new OfficeWorkbook(bytes, firstName));
        _book?.Dispose();
        _book = loaded;
        _histories.Clear();
        _savedRevision = _exportRevision = _book.Revision;
        try { Select(_book.Sheets[0]); }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        ResetReference(); _histories.Clear();
        _book?.Dispose();
        _book = null;
    }

    internal void CommitEdit()
    {
        if (!CommitFormula()) throw new InvalidDataException("OfficeEditor.WorkbookValueInvalid");
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidDataException("OfficeEditor.WorkbookValueInvalid");
    }
    internal Task<byte[]> ExportWorkbookAsync()
    {
        // Draft checkpoints serialize accepted edits while the next cell may still be in progress.
        if (_book == null) throw new InvalidOperationException("OfficeEditor.Loading");
        _exportRevision = _book.Revision;
        return _book.ExportAsync();
    }
    internal void MarkSaved() => _savedRevision = _exportRevision;

    private WorkbookAddress AddressOf(OfficeWorkbookRows.GridRow row, DataGridColumn column)
        => new(row.Index + 1, _firstColumn + column.DisplayIndex);

    private void Select(WorkbookSheet sheet)
    {
        if (_book == null) return;
        _book.Select(sheet);
        _sheet = sheet; _firstColumn = 1; _current = new(1, 1);
        _tabs.Children.Clear();
        foreach (var item in _book.Sheets)
        {
            var button = new Button { Content = item.Name, FontWeight = item == sheet ? FontWeights.Bold : FontWeights.Normal,
                BorderThickness = item == sheet ? new Thickness(1, 1, 1, 3) : new Thickness(1) };
            AutomationProperties.SetAutomationId(button, "Office.Workbook.Tab." + item.Name);
            button.Click += (_, _) => { if (CommitBeforeSwitch()) Select(item); };
            _tabs.Children.Add(button);
        }
        var add = new Button { Content = "+", ToolTip = T("WorkbookAddSheet"), IsEnabled = !_book.Locked };
        AutomationProperties.SetAutomationId(add, "Office.Workbook.Add");
        AutomationProperties.SetName(add, T("WorkbookAddSheet"));
        add.Click += (_, _) =>
        {
            if (!CommitBeforeSwitch()) return;
            Select(_book.AddSheet(T("WorkbookSheet"))); Changed?.Invoke();
        };
        _tabs.Children.Add(add);
        UpdateSheetNotice();
        RenderGrid();
    }

    private bool CommitBeforeSwitch()
    {
        try { CommitEdit(); return true; }
        catch (InvalidDataException error) { Notice(_localize(error.Message)); return false; }
    }
    private void Notice(string message)
    {
        _notice.Text = message; _notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSheetNotice()
    {
        if (_book is null || _sheet is null) return;
        Notice(!_sheet.IsGrid ? T("WorkbookNonGrid") : _book.SheetLocked ? T("WorkbookProtected") :
            _book.HasStoredFormulaResults ? T("WorkbookStoredResults") :
            _book.HasUncalculatedFormulas ? T("WorkbookRecalculation") : "");
    }
    private void GoToAddress(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !WorkbookAddress.TryParse(_address.Text, out var address) || !CommitBeforeSwitch()) return;
        _current = address;
        _firstColumn = 1 + (address.Column - 1) / ColumnsPerPage * ColumnsPerPage;
        RenderGrid(); _grid.Focus(); e.Handled = true;
    }

    private void RenderGrid()
    {
        if (_book == null || _sheet == null) return;
        ResetReference();
        _rendering = true;
        try
        {
            _grid.ItemsSource = null; _grid.Columns.Clear();
            _grid.Visibility = _sheet.IsGrid ? Visibility.Visible : Visibility.Collapsed;
            _address.IsEnabled = _sheet.IsGrid;
            if (!_sheet.IsGrid) { _address.Clear(); _formula.Clear(); _formula.IsReadOnly = true; return; }
            int lastColumn = Math.Min(WorkbookAddress.LastColumn, _firstColumn + ColumnsPerPage - 1);
            for (var c = _firstColumn; c <= lastColumn; c++)
            {
                _grid.Columns.Add(new DataGridTextColumn { Header = WorkbookAddress.ColumnName(c), Width = _book.ColumnWidth(c),
                    Binding = new Binding("[" + (c - _firstColumn) + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit } });
            }
            var rows = new OfficeWorkbookRows(_book, Math.Min(WorkbookAddress.LastRow, Math.Max(_book.LastUsedRow, _current.Row) + SpareRows),
                _firstColumn, Apply);
            _grid.ItemsSource = rows;
            var item = rows[_current.Row - 1];
            var col = _grid.Columns[_current.Column - _firstColumn];
            _grid.CurrentCell = new(item, col); _grid.ScrollIntoView(item, col);
        }
        finally { _rendering = false; }
        ShowCell();
    }
    private void ShowCell()
    {
        if (_book == null) return;
        ResetReference();
        _rendering = true;
        try { _address.Text = _current.ToString(); _formula.Text = _book.CellAt(_current).Input;
            _formula.IsReadOnly = !_book.CanEdit(_current); _formulaChanged = false; }
        finally { _rendering = false; }
    }
    private bool CommitFormula()
    {
        if (!_formulaChanged || _rendering || _book == null) return true;
        if (!Apply(_current, _formula.Text)) return false;
        _formulaChanged = false;
        return true;
    }
    private bool Apply(WorkbookAddress address, string value)
    {
        if (_book == null) return false;
        try
        {
            var prior = _book.CellAt(address).Input;
            if (_book.SetCell(address, value))
            {
                Remember([new(address, prior, value)]);
                if (_grid.ItemsSource is OfficeWorkbookRows view) view.RefreshVisibleCells();
                UpdateSheetNotice();
                ShowCell(); Changed?.Invoke();
            }
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException)
        { Notice(T("WorkbookValueInvalid")); return false; }
    }
}
