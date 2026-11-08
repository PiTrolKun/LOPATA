using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using AIHub.Services.OfficeEditor;
using TextBox = System.Windows.Controls.TextBox;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeWorkbookView
{
    private TextBox? _cellEditor, _pickEditor;
    private int _referenceStart, _referenceLength;
    private string? _referenceText;
    private WorkbookAddress _anchor;
    private bool _picking;
    private ReferenceOutline? _outline;

    private void InstallFormulaPicking()
    {
        _grid.PreviewTextInput += (_, e) =>
        {
            if (e.Text != "=" || _cellEditor?.IsKeyboardFocusWithin == true || _book?.CanEdit(_current) != true) return;
            e.Handled = true;
            StartExpression("=", 1);
        };
        _grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var editor = _formula.IsKeyboardFocusWithin ? _formula : _cellEditor;
            if (editor?.IsKeyboardFocusWithin != true || !editor.Text.StartsWith('=') ||
                CellUnderPointer(e) is not {} target || editor == _cellEditor && target == _current) return;
            var reuse = ReferenceAtCaret(editor);
            if (!reuse) { _referenceStart = editor.SelectionStart; _referenceLength = editor.SelectionLength; }
            if (!reuse || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _anchor = target;
            _pickEditor = editor;
            PutReference(target);
            e.Handled = true;
            _picking = Mouse.Capture(_grid, CaptureMode.SubTree);
        };
        _grid.PreviewMouseMove += (_, e) =>
        {
            if (!_picking || e.LeftButton != MouseButtonState.Pressed || CellUnderPointer(e) is not {} target) return;
            PutReference(target); e.Handled = true;
        };
        _grid.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_picking) return;
            if (CellUnderPointer(e) is {} target) PutReference(target);
            _picking = false; Mouse.Capture(null); e.Handled = true;
        };
        _grid.LostMouseCapture += (_, _) => _picking = false;
        _grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_) => _outline?.InvalidateVisual()));
    }

    private WorkbookAddress? CellUnderPointer(System.Windows.Input.MouseEventArgs e)
    {
        var hit = _grid.InputHitTest(e.GetPosition(_grid)) as DependencyObject;
        while (hit != null && hit is not DataGridCell)
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : (hit as FrameworkContentElement)?.Parent;
        return hit is DataGridCell { DataContext: OfficeWorkbookRows.GridRow row } cell
            ? AddressOf(row, cell.Column) : null;
    }

    private bool ReferenceAtCaret(TextBox editor) => ReferenceEquals(editor,_pickEditor) &&
        editor.Text == _referenceText && editor.SelectionLength == 0 && editor.CaretIndex == _referenceStart + _referenceLength;

    private void PutReference(WorkbookAddress target)
    {
        var editor = _pickEditor!;
        var first = new WorkbookAddress(Math.Min(_anchor.Row,target.Row), Math.Min(_anchor.Column,target.Column));
        var last = new WorkbookAddress(Math.Max(_anchor.Row,target.Row), Math.Max(_anchor.Column,target.Column));
        var reference = first == last ? first.ToString() : $"{first}:{last}";
        if (_outline == null && AdornerLayer.GetAdornerLayer(_grid) is {} layer)
        {
            _outline = new ReferenceOutline(this,layer); layer.Add(_outline);
        }
        _outline?.SetRange(first,last);
        if (editor.Text.Substring(_referenceStart,_referenceLength) != reference)
        {
            editor.Select(_referenceStart,_referenceLength);
            editor.SelectedText = reference;
        }
        _referenceLength = reference.Length;
        editor.Select(_referenceStart+_referenceLength,0);
        _referenceText = editor.Text;
    }

    private void ResetReference()
    {
        if (_picking) { _picking = false; Mouse.Capture(null); }
        _pickEditor = null; _referenceText = null;
        _outline?.Detach(); _outline = null;
    }

    private void StartExpression(string expression, int caret)
    {
        ResetReference();
        _formula.Focus(); _formula.Text = expression; _formula.Select(caret,0);
    }

    private sealed class ReferenceOutline(OfficeWorkbookView owner, AdornerLayer layer) : Adorner(owner._grid)
    {
        private WorkbookAddress _first, _last;
        private readonly Pen _pen = new(System.Windows.Media.Brushes.DodgerBlue, 2);
        private readonly SolidColorBrush _fill = new(System.Windows.Media.Color.FromArgb(38,64,152,240));
        internal void Detach() => layer.Remove(this);
        internal void SetRange(WorkbookAddress first, WorkbookAddress last)
        {
            IsHitTestVisible = false;
            if (_first == first && _last == last) return;
            _first = first; _last = last; InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            // Walk only the cells WPF has realized, even for large dragged ranges.
            void Paint(DependencyObject parent)
            {
                for (int i=0; i<VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent,i);
                    if (child is DataGridCell { IsVisible:true, DataContext:OfficeWorkbookRows.GridRow row } cell && cell.Column != null)
                    {
                        var a = owner.AddressOf(row,cell.Column);
                        if (a.Row < _first.Row || a.Row > _last.Row || a.Column < _first.Column || a.Column > _last.Column) continue;
                        var local = new Rect(cell.RenderSize);
                        if (VisualTreeHelper.GetClip(cell) is {} clip) local.Intersect(clip.Bounds);
                        if (!local.IsEmpty) dc.DrawRectangle(_fill,_pen,cell.TransformToAncestor(owner._grid).TransformBounds(local));
                    }
                    else if (child is Visual) Paint(child);
                }
            }
            dc.PushClip(new RectangleGeometry(new Rect(owner._grid.RenderSize)));
            Paint(owner._grid); dc.Pop();
        }
    }
}
