using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace AIHub.Controls.VideoUpscale;

/// <summary>A movable, independently resizable preview interval; never expands beyond five seconds.</summary>
internal sealed class UpscaleRange : FrameworkElement
{
    private double _duration = 1, _start, _length = 1, _grab, _originalStart, _originalLength;
    private int _drag;
    internal event Action<double, double>? Changed;
    internal List<ImageSource> Thumbnails { get; } = [];
    internal string ClipName { get; set; } = "";
    private double _position;
    internal double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    internal UpscaleRange() { Height = 104; Focusable = true; Cursor = System.Windows.Input.Cursors.SizeWE; }
    internal void Set(double duration, double start, double length)
    { _duration = Math.Max(.001, duration); _start = start; _length = length; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var width = ActualWidth; if (width < 1) return;
        var line = TryFindResource("LineBrush") as Brush ?? Brushes.SlateGray;
        var foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.WhiteSmoke;
        var muted = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.SlateGray;
        var body = new Rect(0, 25, width, 77);
        dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(41, 75, 124)), new Pen(line, 1), body);
        for (var i = 0; i <= 8; i++) {
            var x = width * i / 8;
            dc.DrawLine(new Pen(line, 1), new Point(x, 19), new Point(x, 25));
            var caption = TimeSpan.FromSeconds(_duration * i / 8).ToString(_duration >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
            Text(caption, i == 8 ? Math.Max(0, x - 44) : x + 3, 1, muted, 11, 58);
        }
        dc.PushClip(new RectangleGeometry(new Rect(1, 46, Math.Max(0, width - 2), 55)));
        if (Thumbnails.Count > 0) for (var i = 0; i < Thumbnails.Count; i++) {
            var cell = new Rect(i * width / Thumbnails.Count, 46, width / Thumbnails.Count + 1, 55);
            dc.DrawImage(Thumbnails[i], cell);
        }
        dc.Pop(); Text(ClipName, 6, 27, foreground, 12, Math.Max(1, width - 12));
        var selected = SelectionBounds();
        dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 55, 220, 95)), new Pen(Brushes.LightGreen, 2), selected);
        dc.DrawRectangle(Brushes.LightGreen, null, new Rect(selected.Left, selected.Top, 4, selected.Height));
        dc.DrawRectangle(Brushes.LightGreen, null, new Rect(selected.Right - 4, selected.Top, 4, selected.Height));
        var cursor = Math.Clamp(_position / _duration * width, 0, width);
        dc.DrawLine(new Pen(Brushes.OrangeRed, 1.5), new Point(cursor, 18), new Point(cursor, 104));
        void Text(string value, double x, double y, Brush brush, double size, double maximum) {
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = maximum, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(x, y));
        }
    }
    private Rect SelectionBounds()
    {
        var width = ActualWidth; var size = Math.Min(width, Math.Max(10, _length / _duration * width));
        return new Rect(Math.Clamp(_start / _duration * width, 0, Math.Max(0, width - size)), 25, size, 77);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || ActualWidth <= 0) return;
        Focus(); var x = e.GetPosition(this).X; var bounds = SelectionBounds(); var left = bounds.Left; var right = bounds.Right;
        _drag = Math.Abs(x - left) < 5 ? 1 : Math.Abs(x - right) < 5 ? 2 : 3;
        if (x < left - 5 || x > right + 5) _start = Math.Clamp(x / ActualWidth * _duration, 0, Math.Max(0, _duration - _length));
        _grab = x; _originalStart = _start; _originalLength = _length; CaptureMouse(); Publish(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || ActualWidth <= 0) return;
        var delta = (e.GetPosition(this).X - _grab) / ActualWidth * _duration; var minimum = Math.Min(.1, _duration);
        if (_drag == 1) {
            var end = _originalStart + _originalLength;
            _start = Math.Clamp(_originalStart + delta, Math.Max(0, end - 5), end - minimum); _length = end - _start;
        } else if (_drag == 2) _length = Math.Clamp(_originalLength + delta, minimum, Math.Min(5, _duration - _start));
        else _start = Math.Clamp(_originalStart + delta, 0, Math.Max(0, _duration - _length));
        Publish(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
    private void Publish() { InvalidateVisual(); Changed?.Invoke(_start, _length); }
}
