using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

/// <summary>Reusable two-axis bubble level. Geometry never changes stored axis values.</summary>
public sealed class ModelBubbleControl : FrameworkElement
{
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(ModelBubbleControl),
        new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(ModelBubbleControl),
        new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public double X { get; private set; }
    public double Y { get; private set; }
    public bool CompositionEnabled { get; private set; } = true;
    public event Action<double, double>? PositionChanged;
    public ModelBubbleControl()
    {
        Focusable = true; ClipToBounds = true; Cursor = System.Windows.Input.Cursors.Hand;
        MaxWidth = MaxHeight = 360;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(RingBrushProperty, "LineBrush"); SetResourceReference(AccentBrushProperty, "AccentBrush");
        AutomationProperties.SetAutomationId(this, "Music.Tuning.Bubble");
    }
    public void SetPosition(double x, double y, bool compositionEnabled)
    { X = x; Y = y; CompositionEnabled = compositionEnabled; InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new BubblePeer(this);
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    private sealed class BubblePeer(ModelBubbleControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ModelBubbleControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
    public static Point ToDisk(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y); var maximum = Math.Max(Math.Abs(x), Math.Abs(y));
        return length == 0 ? new() : new(x * maximum / length, y * maximum / length);
    }
    public static Point FromDisk(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y); var maximum = Math.Max(Math.Abs(x), Math.Abs(y));
        if (maximum == 0) return new();
        var scale = Math.Min(1, length) / maximum; return new(x * scale, y * scale);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 340;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 340;
        var side = Math.Max(0, Math.Min(340, Math.Min(width, height))); return new(side, side);
    }
    private (Point Center, double Radius) Geometry() => (new(ActualWidth / 2, ActualHeight / 2), Math.Max(1, Math.Min(ActualWidth, ActualHeight) / 2 - 12));
    protected override void OnRender(DrawingContext drawing)
    {
        var (center, radius) = Geometry(); var color = (AccentBrush as SolidColorBrush)?.Color ?? Colors.RoyalBlue;
        var liquid = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
        var pen = new Pen(RingBrush, 1); var axis = new Pen(AccentBrush, 1.2) { DashStyle = DashStyles.Dot };
        drawing.DrawEllipse(liquid, new Pen(AccentBrush, IsKeyboardFocused ? 2.5 : 1.5), center, radius, radius);
        foreach (var fraction in new[] { .25, .5, .75 }) drawing.DrawEllipse(null, pen, center, radius * fraction, radius * fraction);
        drawing.DrawLine(CompositionEnabled ? axis : pen, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        drawing.DrawLine(axis, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        drawing.DrawEllipse(AccentBrush, null, center, 2, 2);
        var disk = ToDisk(CompositionEnabled ? X : 0, Y); var travel = Math.Max(1, radius - 13);
        var bubble = new Point(center.X + disk.X * travel, center.Y - disk.Y * travel);
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)), new Pen(AccentBrush, 2), bubble, 12, 12);
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), null, new(bubble.X - 3, bubble.Y - 4), 3, 3);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (!IsEnabled) return;
        Focus(); CaptureMouse(); Move(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    { base.OnMouseMove(e); if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Move(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e); var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .025;
        var x = X; var y = Y;
        switch (e.Key) {
            case Key.Left: if (CompositionEnabled) x -= step; break;
            case Key.Right: if (CompositionEnabled) x += step; break;
            case Key.Up: y += step; break;
            case Key.Down: y -= step; break;
            case Key.Home: if (CompositionEnabled) x = 0; y = 0; break;
            default: return;
        }
        PositionChanged?.Invoke(Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1)); e.Handled = true;
    }
    private void Move(Point pointer)
    {
        var (center, radius) = Geometry(); var travel = Math.Max(1, radius - 13);
        var position = FromDisk((pointer.X - center.X) / travel, (center.Y - pointer.Y) / travel);
        PositionChanged?.Invoke(CompositionEnabled ? position.X : X, position.Y);
    }
}
