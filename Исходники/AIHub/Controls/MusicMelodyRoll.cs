using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

/// <summary>One melody, no hidden chord/voice selection. Scroll and numeric controls complement mouse editing.</summary>
public sealed class MusicMelodyRoll : FrameworkElement
{
    private const double Row = 20, Left = 54;
    private double _beat = 72;
    private double _viewportTop;
    private MusicMelody _melody = new();
    private MusicMelodyNote? _drag;
    private Point _origin;
    private bool _resize;
    private MusicMelodyNote[]? _ghost;
    public HashSet<string> Selection { get; } = [];
    public int Snap { get; set; } = 120;
    public Action<MusicMelody>? Edited { get; set; }
    public Action? SelectionChanged { get; set; }
    public MusicMelodyRoll() {
        Focusable = true; Cursor = Cursors.Cross; Height = 128 * Row + 24;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "Music.Midi.Roll");
    }
    public void Set(MusicMelody melody) { _melody = melody; _ghost = null; Selection.IntersectWith(melody.Notes.Select(n => n.Id)); Resize(); }
    public void Zoom(double beat) { _beat = beat; Resize(); }
    public void Viewport(double top) { _viewportTop = top; InvalidateVisual(); }
    private void Resize() { Width = Math.Max(1200, Left + (_melody.EndTick / 480d + 8) * _beat); InvalidateVisual(); }
    private Rect Box(MusicMelodyNote n) => new(Left + n.Start / 480d * _beat, 24 + (127 - n.Pitch) * Row + 2,
        Math.Max(4, n.Length / 480d * _beat), Row - 4);
    private int Tick(double x) => Math.Max(0, (int)Math.Round((x - Left) / _beat * 480 / Snap) * Snap);
    protected override void OnRender(DrawingContext dc) {
        var input = TryFindResource("InputBrush") as Brush ?? Brushes.Black;
        var text = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.RoyalBlue;
        var line = TryFindResource("LineBrush") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(input, null, new Rect(RenderSize));
        for (var pitch = 0; pitch < 128; pitch++) {
            var y = 24 + (127 - pitch) * Row;
            if (new[] { 1, 3, 6, 8, 10 }.Contains(pitch % 12)) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 128,128,128)), null, new(0,y,ActualWidth,Row));
            dc.DrawLine(new Pen(line, .35), new(0,y), new(ActualWidth,y));
            if (pitch % 12 == 0) Label(NoteName(pitch), new(3,y+2));
        }
        var perBar = _melody.Numerator * 4d / _melody.Denominator;
        for (var beat = 0; beat < (ActualWidth - Left) / _beat; beat++) {
            var x = Left + beat * _beat; var bar = Math.Abs(beat % perBar) < .001;
            dc.DrawLine(new Pen(line, bar ? 1.2 : .5), new(x,0), new(x,ActualHeight));
            for (var tick = Snap; tick < 480; tick += Snap) {
                var sub = x + tick / 480d * _beat;
                dc.DrawLine(new Pen(line,.2),new(sub,24),new(sub,ActualHeight));
            }
        }
        foreach (var note in _ghost ?? _melody.Notes) dc.DrawRoundedRectangle(accent,
            Selection.Contains(note.Id) ? new Pen(text, 2) : null, Box(note), 3,3);
        dc.DrawRectangle(input,null,new Rect(0,_viewportTop,ActualWidth,24));
        for (var beat = 0; beat < (ActualWidth - Left) / _beat; beat++)
            Label((beat+1).ToString(CultureInfo.InvariantCulture),new(Left+beat*_beat+3,_viewportTop+2));
        void Label(string value, Point at) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
    }
    public static string NoteName(int pitch) => new[] { "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B" }[pitch % 12] + (pitch / 12 - 1);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
        base.OnMouseLeftButtonDown(e); Focus(); var p = e.GetPosition(this);
        if (p.Y < _viewportTop+24) return;
        var note = _melody.Notes.LastOrDefault(n => Box(n).Contains(p));
        if (note is null) {
            if (p.X < Left || p.Y < 24) return;
            var pitch = Math.Clamp(127 - (int)((p.Y - 24) / Row), 0,127);
            var added = new MusicMelodyNote(pitch, Tick(p.X), Snap);
            Selection.Clear(); Selection.Add(added.Id);
            Edited?.Invoke(_melody with { Notes = _melody.Notes.Append(added).ToArray() });
        } else {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { if (!Selection.Add(note.Id)) Selection.Remove(note.Id); }
            else if (!Selection.Contains(note.Id)) { Selection.Clear(); Selection.Add(note.Id); }
            _drag = note; _origin = p; _resize = Box(note).Right - p.X < 8; CaptureMouse();
        }
        SelectionChanged?.Invoke(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e); if (_drag is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this); var delta = (int)Math.Round((p.X - _origin.X) / _beat * 480 / Snap) * Snap;
        var pitch = -(int)Math.Round((p.Y - _origin.Y) / Row);
        _ghost = _melody.Notes.Select(n => !Selection.Contains(n.Id) ? n : _resize
            ? n with { Length = Math.Max(Snap, n.Length + delta) } : n with { Start = n.Start + delta, Pitch = n.Pitch + pitch }).ToArray();
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
        base.OnMouseLeftButtonUp(e); if (_drag is null) return;
        _drag = null; var next = _ghost; _ghost = null; ReleaseMouseCapture();
        if (next is not null) Edited?.Invoke(_melody with { Notes = next }); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _drag = null; _ghost = null; InvalidateVisual(); }
}
