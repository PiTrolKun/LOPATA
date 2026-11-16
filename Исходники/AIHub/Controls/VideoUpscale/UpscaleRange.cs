using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.VideoEditor;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Line = System.Windows.Shapes.Line;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace AIHub.Controls.VideoUpscale;

/// <summary>A movable, resizable preview interval with thumbnail or waveform display.</summary>
internal sealed class UpscaleRange : Canvas
{
    private readonly TextBlock[] _ticks = new TextBlock[9];
    private readonly TextBlock _clipLabel = CreateLabel(12, "TextPrimaryBrush");
    private readonly TranslateTransform _playheadOffset = new();
    private readonly Line _playhead;
    private readonly bool _compact;
    private double _duration = 1, _start, _length = 1, _grab, _originalStart, _originalLength;
    private double _minLength = .1, _maxLength = 5;
    private VideoWaveform? _waveform;
    private StreamGeometry? _waveformGeometry;
    private double _waveformWidth;
    internal VideoWaveform? Waveform
    {
        get => _waveform;
        set
        {
            _waveform = value;
            _waveformGeometry = null;
            InvalidateVisual();
        }
    }
    private int _drag;
    internal event Action<double, double>? Changed;
    internal List<ImageSource> Thumbnails { get; } = [];
    internal string ClipName { get => _clipLabel.Text; set => _clipLabel.Text = value; }
    private double _position;
    internal double Position
    {
        get => _position;
        set { _position = value; UpdatePlayhead(ActualWidth); }
    }
    internal UpscaleRange(bool compact = false)
    {
        _compact = compact;
        Height = compact ? 76 : 104;
        if (compact)
        {
            _position = double.NaN;
            SetResourceReference(BackgroundProperty, "PanelBrush");
            GotKeyboardFocus += (_, _) => InvalidateVisual();
            LostKeyboardFocus += (_, _) => InvalidateVisual();
        }
        Focusable = true;
        Cursor = System.Windows.Input.Cursors.SizeWE;
        for (var i = 0; i < _ticks.Length; i++)
        {
            var tick = CreateLabel(11, "TextSecondaryBrush");
            tick.Width = 58;
            SetTop(tick, 1);
            _ticks[i] = tick;
            Children.Add(tick);
        }
        SetLeft(_clipLabel, 6);
        SetTop(_clipLabel, 27);
        Children.Add(_clipLabel);
        _playhead = new Line {
            Y1 = 18, Y2 = Height, Stroke = Brushes.OrangeRed, StrokeThickness = 1.5,
            RenderTransform = _playheadOffset, IsHitTestVisible = false
        };
        if (compact) _playhead.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextPrimaryBrush");
        Children.Add(_playhead);
        UpdateTickText();
    }

    internal void Set(double duration, double start, double length, double minLength = .1, double maxLength = 5)
    {
        _minLength = minLength;
        _maxLength = maxLength;
        duration = Math.Max(.001, duration);
        if (_duration != duration)
        {
            _duration = duration;
            _waveformGeometry = null;
            UpdateTickText();
            UpdatePlayhead(ActualWidth);
        }
        _start = start;
        _length = length;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var width = arrangeSize.Width;
        for (var i = 0; i < _ticks.Length; i++)
        {
            var x = width * i / (_ticks.Length - 1);
            SetLeft(_ticks[i], i == _ticks.Length - 1 ? Math.Max(0, x - 44) : x + 3);
            if (_compact)
            {
                var spacing = width < 240 ? 4 : width < 460 ? 2 : 1;
                _ticks[i].Opacity = i % spacing == 0 ? 1 : 0;
            }
        }
        _clipLabel.Width = Math.Max(1, width - 12);
        UpdatePlayhead(width);
        return base.ArrangeOverride(arrangeSize);
    }

    private static TextBlock CreateLabel(double size, string brushKey)
    {
        var label = new TextBlock {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = size,
            TextAlignment = TextAlignment.Left, FlowDirection = System.Windows.FlowDirection.LeftToRight,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return label;
    }

    private void UpdateTickText()
    {
        var format = _duration >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss";
        for (var i = 0; i < _ticks.Length; i++)
            _ticks[i].Text = TimeSpan.FromSeconds(_duration * i / (_ticks.Length - 1)).ToString(format);
    }

    private void UpdatePlayhead(double width)
    {
        _playhead.Visibility = double.IsFinite(_position) ? Visibility.Visible : Visibility.Hidden;
        if (double.IsFinite(_position)) _playheadOffset.X = Math.Clamp(_position / _duration * width, 0, width);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var width = ActualWidth; if (width < 1) return;
        var line = TryFindResource("LineBrush") as Brush ?? Brushes.SlateGray;
        var body = new Rect(0, 25, width, Height - 27);
        var track = _compact ? Background ?? Brushes.DimGray
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(41, 75, 124));
        dc.DrawRectangle(track, new Pen(line, 1), body);
        for (var i = 0; i < _ticks.Length; i++) {
            var x = width * i / (_ticks.Length - 1);
            dc.DrawLine(new Pen(line, 1), new Point(x, 19), new Point(x, 25));
        }
        if (_compact)
        {
            DrawWaveformTrack(dc, body);
            return;
        }
        dc.PushClip(new RectangleGeometry(new Rect(1, 46, Math.Max(0, width - 2), 55)));
        if (Thumbnails.Count > 0) for (var i = 0; i < Thumbnails.Count; i++) {
            var cell = new Rect(i * width / Thumbnails.Count, 46, width / Thumbnails.Count + 1, 55);
            dc.DrawImage(Thumbnails[i], cell);
        }
        dc.Pop();
        var selected = SelectionBounds();
        dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 55, 220, 95)), new Pen(Brushes.LightGreen, 2), selected);
        dc.DrawRectangle(Brushes.LightGreen, null, new Rect(selected.Left, selected.Top, 4, selected.Height));
        dc.DrawRectangle(Brushes.LightGreen, null, new Rect(selected.Right - 4, selected.Top, 4, selected.Height));
    }
    private void DrawWaveformTrack(DrawingContext dc, Rect body)
    {
        var selected = SelectionBounds();
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        if (_length > 0)
            dc.DrawRectangle(TryFindResource("StepBadgeBrush") as Brush, null, selected);

        if (_waveform is { Count: > 0 } wave)
        {
            if (_waveformGeometry is null || _waveformWidth != body.Width)
            {
                var geometry = new StreamGeometry();
                using (var draw = geometry.Open())
                {
                    var center = body.Top + body.Height / 2;
                    for (var x = 1d; x < body.Width; x += 2)
                    {
                        var peak = wave.Peak((x - 1) / body.Width * _duration, (x + 1) / body.Width * _duration);
                        var height = Math.Max(.5, Math.Sqrt(peak) * (body.Height / 2 - 3));
                        draw.BeginFigure(new Point(x, center - height), false, false);
                        draw.LineTo(new Point(x, center + height), true, false);
                    }
                }
                geometry.Freeze();
                _waveformGeometry = geometry;
                _waveformWidth = body.Width;
            }
            var waveBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.LightSlateGray;
            dc.DrawGeometry(null, new Pen(waveBrush, 1), _waveformGeometry);
        }

        if (_length <= 0) return;
        dc.DrawRectangle(null, new Pen(accent, IsKeyboardFocused ? 2.5 : 1.5), selected);
        var grip = Math.Min(4, selected.Width / 3);
        dc.DrawRectangle(accent, null, new Rect(selected.Left, selected.Top, grip, selected.Height));
        dc.DrawRectangle(accent, null, new Rect(selected.Right - grip, selected.Top, grip, selected.Height));
    }

    private Rect SelectionBounds()
    {
        var width = ActualWidth; var size = Math.Min(width, Math.Max(_compact ? 0 : 10, _length / _duration * width));
        return new Rect(Math.Clamp(_start / _duration * width, 0, Math.Max(0, width - size)), 25, size, Height - 27);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || ActualWidth <= 0) return;
        Focus(); var x = e.GetPosition(this).X; var bounds = SelectionBounds(); var left = bounds.Left; var right = bounds.Right;
        var grip = _compact ? Math.Min(5, bounds.Width / 3) : 5;
        _drag = Math.Abs(x - left) <= grip ? 1 : Math.Abs(x - right) <= grip ? 2 : 3;
        if (_drag == 3 && (x < left || x > right)) _start = Math.Clamp(x / ActualWidth * _duration, 0, Math.Max(0, _duration - _length));
        _grab = x; _originalStart = _start; _originalLength = _length; CaptureMouse(); Publish(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || ActualWidth <= 0) return;
        var delta = (e.GetPosition(this).X - _grab) / ActualWidth * _duration;
        var minimum = Math.Min(_minLength, _duration);
        if (_drag == 1) {
            var end = _originalStart + _originalLength;
            var earliest = Math.Max(0, end - _maxLength);
            _start = Math.Clamp(_originalStart + delta, earliest, Math.Max(earliest, end - minimum));
            _length = end - _start;
        } else if (_drag == 2) _length = Math.Clamp(_originalLength + delta, minimum, Math.Max(minimum, Math.Min(_maxLength, _duration - _start)));
        else _start = Math.Clamp(_originalStart + delta, 0, Math.Max(0, _duration - _length));
        Publish(); e.Handled = true;
    }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (!_compact || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
        {
            base.OnKeyDown(e);
            return;
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : .1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Left or Key.Right)
        {
            var endLimit = Math.Max(_minLength, Math.Min(_maxLength, _duration - _start));
            _length = Math.Clamp(_length + (e.Key == Key.Right ? step : -step), _minLength, endLimit);
            Publish();
            e.Handled = true;
            return;
        }
        var maximum = Math.Max(0, _duration - _length);
        _start = e.Key switch
        {
            Key.Home => 0,
            Key.End => maximum,
            Key.Left => Math.Max(0, _start - step),
            _ => Math.Min(maximum, _start + step)
        };
        Publish();
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); e.Handled = true; }
    private void Publish() { InvalidateVisual(); Changed?.Invoke(_start, _length); }
}
