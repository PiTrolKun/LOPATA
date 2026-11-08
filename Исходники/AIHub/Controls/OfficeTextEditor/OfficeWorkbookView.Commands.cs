using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeWorkbookView
{
    private sealed record CellEdit(WorkbookAddress Address, string Before, string After);
    private sealed class History
    {
        internal readonly List<List<CellEdit>> Undo = [], Redo = [];
    }
    private readonly Dictionary<string,History> _histories = [];
    private History SheetHistory => _histories.TryGetValue(_sheet!.Name,out var h) ? h : _histories[_sheet.Name] = new();

    private void BuildCellCommands()
    {
        var bar = new WrapPanel { Margin = new Thickness(3,0,3,3) };
        SetDock(bar,Dock.Top); Children.Add(bar);
        void Command(string id, Action action)
        {
            var button = new Button { Content = T("Cell"+id), Focusable = false, Margin = new Thickness(1) };
            AutomationProperties.SetAutomationId(button,"Office.Workbook."+id);
            button.Click += (_,_) => { if (_sheet?.IsGrid == true) action(); };
            bar.Children.Add(button);
        }
        Command("Undo", () => Replay(false)); Command("Redo", () => Replay(true));
        Command("Copy", CopyValues); Command("Paste",PasteValues); Command("Clear",ClearCells);
        foreach (var function in new[]{"SUM","AVERAGE","MIN","MAX"})
            Command(function, () =>
            {
                if (_book?.CanEdit(_current) != true) return;
                var template = "=" + function + "()";
                if (_cellEditor is { IsKeyboardFocusWithin: true, Text: "=" } box)
                { box.Text = template; box.Select(template.Length-1,0); return; }
                if (!(_formula.IsKeyboardFocusWithin && _formula.Text == "=") && !CommitBeforeSwitch()) return;
                StartExpression(template,template.Length-1);
            });
        _grid.PreviewKeyDown += (_,e) =>
        {
            if (Keyboard.FocusedElement is TextBox || _sheet?.IsGrid != true) return;
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) ClearCells();
            else if (Keyboard.Modifiers != ModifierKeys.Control) return;
            else if (e.Key == Key.Z) Replay(false);
            else if (e.Key == Key.Y) Replay(true);
            else if (e.Key == Key.C) CopyValues();
            else if (e.Key == Key.V) PasteValues();
            else return;
            e.Handled = true;
        };
    }

    private void Remember(List<CellEdit> edits)
    {
        var history = SheetHistory;
        history.Undo.Add(edits);
        if (history.Undo.Count > 100) history.Undo.RemoveAt(0);
        history.Redo.Clear();
    }

    private void Replay(bool redo)
    {
        if (!CommitBeforeSwitch()) return;
        var history = SheetHistory;
        var from = redo ? history.Redo : history.Undo;
        if (from.Count == 0) return;
        var edits = from[^1];
        if (!WriteCells(edits, redo)) return;
        from.RemoveAt(from.Count-1);
        (redo ? history.Undo : history.Redo).Add(edits);
    }

    private HashSet<WorkbookAddress> Selection()
    {
        var cells = _grid.SelectedCells.Where(c => c.Item is OfficeWorkbookRows.GridRow)
            .Select(c => AddressOf((OfficeWorkbookRows.GridRow)c.Item,c.Column)).ToHashSet();
        if (cells.Count == 0) cells.Add(_current);
        return cells;
    }

    private void ClearCells()
    {
        if (!CommitBeforeSwitch()) return;
        ChangeCells(Selection().Select(a => (a,"")));
    }

    internal string? SummarySelection()
    {
        if (!CommitBeforeSwitch()) return null;
        var cells = _grid.SelectedCells.Where(c => c.Item is OfficeWorkbookRows.GridRow)
            .Select(c => AddressOf((OfficeWorkbookRows.GridRow)c.Item, c.Column)).ToArray();
        if (cells.Length == 0) return null;
        return string.Join("\n", cells.OrderBy(c => c.Row).ThenBy(c => c.Column)
            .Select(c => _book!.CellAt(new(c.Row, c.Column)).Display));
    }

    private void CopyValues()
    {
        if (!CommitBeforeSwitch()) return;
        var cells = Selection();
        var top = cells.Min(a=>a.Row); var bottom = cells.Max(a=>a.Row);
        var left = cells.Min(a=>a.Column); var right = cells.Max(a=>a.Column);
        if ((long)(bottom-top+1)*(right-left+1) != cells.Count) { Notice(T("CellRectangle")); return; }
        var rows = Enumerable.Range(top,bottom-top+1).Select(r => string.Join("\t",Enumerable.Range(left,right-left+1)
            .Select(c => WorkbookClipboard.Quote(_book!.CellAt(new(r,c)).Display))));
        try { Clipboard.SetText(string.Join("\r\n",rows)); }
        catch (System.Runtime.InteropServices.ExternalException) { Notice(T("CellClipboardBusy")); }
    }

    private void PasteValues()
    {
        if (!CommitBeforeSwitch()) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            PasteTable(Clipboard.GetText());
        }
        catch (InvalidDataException) { Notice(T("WorkbookValueInvalid")); }
        catch (System.Runtime.InteropServices.ExternalException) { Notice(T("CellClipboardBusy")); }
    }

    internal void PasteTable(string text)
    {
        var selection = Selection();
        var start = new WorkbookAddress(selection.Min(a => a.Row), selection.Min(a => a.Column));
        var rows = WorkbookClipboard.Parse(text);
        ChangeCells(rows.SelectMany((row,r) => row.Select((value,c) =>
            (new WorkbookAddress(start.Row+r,start.Column+c),value))));
    }

    private void ChangeCells(IEnumerable<(WorkbookAddress Address,string Value)> values)
    {
        var changes = new List<CellEdit>();
        foreach (var (address,value) in values)
        {
            if (address.Row > WorkbookAddress.LastRow || address.Column > WorkbookAddress.LastColumn || !_book!.CanEdit(address))
            { Notice(T("CellCannotChange")); return; }
            var previous = _book.CellAt(address).Input;
            if (value != previous) changes.Add(new(address,previous,value));
        }
        if (changes.Count != 0 && WriteCells(changes,true)) Remember(changes);
    }

    private bool WriteCells(List<CellEdit> edits,bool forward)
    {
        try
        {
            foreach (var e in edits) OfficeWorkbook.ValidateInput(forward ? e.After : e.Before);
            var written = 0;
            try
            {
                foreach (var e in edits) { _book!.SetCell(e.Address,forward ? e.After : e.Before); written++; }
            }
            catch
            {
                foreach (var e in edits.Take(written).Reverse()) _book!.SetCell(e.Address,forward ? e.Before : e.After);
                throw;
            }
            if (_grid.ItemsSource is OfficeWorkbookRows rows)
            {
                if (_book!.LastUsedRow > rows.Count) RenderGrid();
                else rows.RefreshVisibleCells();
            }
            UpdateSheetNotice(); ShowCell(); Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        { Notice(T("WorkbookValueInvalid")); return false; }
    }
}
