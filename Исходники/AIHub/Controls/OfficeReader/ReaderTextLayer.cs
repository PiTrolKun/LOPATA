using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.OfficeReader;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.OfficeReader;

/// <summary>Selectable glyph overlay; no reflow or replacement of the original page.</summary>
internal sealed class ReaderTextLayer : FrameworkElement
{
    private ReaderTextSelection? _selection;
    private ReaderPageText _text = ReaderPageText.Empty;
    private int _page;
    private readonly DispatcherTimer _drag = new() { Interval = TimeSpan.FromMilliseconds(65) };
    internal ReaderPageText PageText => _text;
    internal int PageIndex => _page;

    internal ReaderTextLayer()
    {
        Focusable = true;
        AutomationProperties.SetAutomationId(this, "Office.Reader.TextLayer");
        _drag.Tick += (_, _) => MoveSelection();
        Unloaded += (_, _) => Reset();
    }

    internal void Bind(ReaderTextSelection selection, int page, ReaderPageText text, Func<string, string> localize)
    {
        Reset(); _selection = selection; _text = text; _page = page;
        selection.Changed += InvalidateVisual;
        Cursor = text.Runs.Count > 0 ? Cursors.IBeam : Cursors.Arrow;
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = localize("Reader.Copy"), InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => selection.CopyRequested?.Invoke();
        var all = new MenuItem { Header = localize("Reader.SelectAll"), InputGestureText = "Ctrl+A" };
        all.Click += (_, _) => selection.SelectAllRequested?.Invoke();
        menu.Items.Add(copy); menu.Items.Add(all);
        menu.Opened += (_, _) => { copy.IsEnabled = selection.HasSelection; all.IsEnabled = text.Runs.Count > 0; };
        ContextMenu = menu; InvalidateVisual();
    }

    internal void Reset()
    {
        _drag.Stop(); if (IsMouseCaptured) ReleaseMouseCapture();
        if (_selection is not null) _selection.Changed -= InvalidateVisual;
        _selection = null; _text = ReaderPageText.Empty; ContextMenu = null; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_selection is null) return;
        var selected = _selection.Range(_page, _text.Text.Length);
        if (selected.Length == 0) return;
        var brush = new SolidColorBrush(Color.FromArgb(95, 56, 126, 240)); brush.Freeze();
        foreach (var run in _text.Runs)
            if (run.Start < selected.Start + selected.Length && run.Start + run.Length > selected.Start)
                drawing.DrawRectangle(brush, null, new Rect(run.Left * ActualWidth, run.Top * ActualHeight,
                    Math.Max(1, run.Width * ActualWidth), Math.Max(2, run.Height * ActualHeight)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_selection is null || _text.Runs.Count == 0) return;
        Focus(); var point = e.GetPosition(this);
        var x = point.X / ActualWidth; var y = point.Y / ActualHeight;
        if (e.ClickCount == 2 && _text.Nearest(x, y) is { } word)
        { _selection.Begin(_page, word.WordStart, false); _selection.Extend(_page, word.WordEnd); }
        else
        {
            _selection.Begin(_page, _text.Hit(x, y), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            CaptureMouse(); _drag.Start();
        }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (IsMouseCaptured) { MoveSelection(); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { MoveSelection(); _drag.Stop(); ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _drag.Stop(); base.OnLostMouseCapture(e); }
    private void MoveSelection()
    {
        if (!IsMouseCaptured || Mouse.LeftButton != MouseButtonState.Pressed) { _drag.Stop(); return; }
        if (Window.GetWindow(this) is ReaderWindow window) window.DragTextSelection(this, Mouse.GetPosition(this));
    }
}
