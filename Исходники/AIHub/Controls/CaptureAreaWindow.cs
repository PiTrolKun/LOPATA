using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Window = System.Windows.Window;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

public sealed class CaptureAreaWindow : Window
{
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Rectangle _selection = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 40, 140, 255)) };
    private Point _start;
    private bool _dragging;
    public Int32Rect? SelectedArea { get; private set; }
    public Exception? Failure { get; private set; }
    public CaptureAreaWindow(BitmapSource screenshot, Int32Rect bounds, string hint, Func<Point>? physicalPointer = null)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Cursor = System.Windows.Input.Cursors.Cross;
        var root = new Grid(); root.Children.Add(new Image { Source = screenshot, Stretch = Stretch.Fill }); root.Children.Add(_canvas);
        _canvas.Children.Add(_selection);
        var text = new TextBlock { Text = hint, Foreground = Brushes.White, Background = Brushes.Black, Padding = new(12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        root.Children.Add(text); Content = root;
        void PlaceOnPhysicalDesktop()
        {
            var previous = SetThreadDpiAwarenessContext(new(-4));
            try
            {
                if (!SetWindowPos(new WindowInteropHelper(this).Handle, new(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        }
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                // This overlay spans monitors and has an explicitly fixed physical rectangle.
                // Applying a single monitor's suggested DPI rectangle would stretch the desktop.
                if (message == 0x02E0) handled = true;
                return 0;
            });
            PlaceOnPhysicalDesktop();
        };
        // WPF applies its startup/DPI sizing after SourceInitialized. Reassert the physical rectangle
        // once that layout has completed; the screenshot and selection canvas then share one scale.
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => { if (IsVisible) Guard(PlaceOnPhysicalDesktop); }));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        Point Pointer()
        {
            Point physical;
            if (physicalPointer is not null) physical = physicalPointer();
            else
            {
                if (!GetPhysicalCursorPos(out var point)) throw new System.ComponentModel.Win32Exception();
                physical = new(point.X, point.Y);
            }
            return new((physical.X - bounds.X) * _canvas.ActualWidth / screenshot.PixelWidth,
                (physical.Y - bounds.Y) * _canvas.ActualHeight / screenshot.PixelHeight);
        }
        _canvas.MouseLeftButtonDown += (_, _) => Guard(() => { _start = Pointer(); _dragging = true; _canvas.CaptureMouse(); Update(_start); });
        _canvas.MouseMove += (_, _) => { if (_dragging) Guard(() => Update(Pointer())); };
        _canvas.MouseLeftButtonUp += (_, _) => Guard(() =>
        {
            if (!_dragging) return; _dragging = false; _canvas.ReleaseMouseCapture(); var end = Pointer();
            var sx = screenshot.PixelWidth / _canvas.ActualWidth; var sy = screenshot.PixelHeight / _canvas.ActualHeight;
            var left = Math.Clamp((int)Math.Floor(Math.Min(_start.X, end.X) * sx), 0, screenshot.PixelWidth - 1);
            var top = Math.Clamp((int)Math.Floor(Math.Min(_start.Y, end.Y) * sy), 0, screenshot.PixelHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling(Math.Max(_start.X, end.X) * sx), left, screenshot.PixelWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(_start.Y, end.Y) * sy), top, screenshot.PixelHeight);
            if (right <= left || bottom <= top) return;
            SelectedArea = new(left, top, right - left, bottom - top); DialogResult = true;
        });
        // DPI awareness is attached when the HWND is created, not when it is later moved.
        // Creating a system-aware HWND first would let Windows scale it on the other monitor.
        var creationContext = SetThreadDpiAwarenessContext(new(-4));
        try { new WindowInteropHelper(this).EnsureHandle(); }
        finally { if (creationContext != 0) SetThreadDpiAwarenessContext(creationContext); }
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { Failure = error; _dragging = false; _canvas.ReleaseMouseCapture(); Close(); }
    }
    private void Update(Point end)
    {
        Canvas.SetLeft(_selection, Math.Min(_start.X, end.X)); Canvas.SetTop(_selection, Math.Min(_start.Y, end.Y));
        _selection.Width = Math.Abs(end.X - _start.X); _selection.Height = Math.Abs(end.Y - _start.Y);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPhysicalCursorPos(out NativePoint point);
}
