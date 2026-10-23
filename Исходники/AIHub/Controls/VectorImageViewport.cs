using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using System.IO;
using System.Windows.Media.Imaging;
using AIHub.Services;
using AIHub.Services.VectorGraphics;

namespace AIHub.Controls;

/// <summary>Zooms around the pointer and pans a fitted preview without changing its aspect ratio.</summary>
internal sealed class VectorImageViewport : Grid
{
    private readonly FrameworkElement _surface;
    private Matrix _matrix = Matrix.Identity;
    private Point? _drag;
    private readonly Func<string>? _svg;
    private readonly double _aspect;
    private readonly string _hint, _failureHint;
    private readonly System.Windows.Controls.Image _detail = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private CancellationTokenSource? _frame;
    private int _generation;
    private VectorBrowserView? _browser;
    private bool _browserReady, _browserStarting;
    private CancellationTokenSource? _browserLoad;
    public VectorImageViewport(FrameworkElement surface, string hint, Func<string>? svg = null, double aspect = 1, string? failureHint = null)
    {
        _surface = surface; _svg = svg; _aspect = aspect; _hint = hint; _failureHint = failureHint ?? hint;
        Children.Add(surface); Children.Add(_detail); ClipToBounds = true;
        Background = surface is Border border ? border.Background : Brushes.Transparent;
        ToolTip = hint; AutomationProperties.SetAutomationId(this, "Vector.Viewport"); AutomationProperties.SetName(this, hint);
        Apply();
        PreviewMouseWheel += (_, e) =>
        {
            var point = e.GetPosition(this);
            var scale = Math.Clamp(_matrix.M11 * Math.Pow(1.2, e.Delta / 120d), .25, 32);
            var factor = scale / _matrix.M11;
            _matrix.ScaleAt(factor, factor, point.X, point.Y); Apply(); e.Handled = true;
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Reset(); e.Handled = true; return; }
            _drag = e.GetPosition(this); Cursor = Cursors.SizeAll; CaptureMouse(); e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_drag is not { } start) return;
            var point = e.GetPosition(this); _matrix.Translate(point.X - start.X, point.Y - start.Y);
            _drag = point; Apply();
        };
        PreviewMouseLeftButtonUp += (_, e) => { _drag = null; Cursor = Cursors.Arrow; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => { _drag = null; Cursor = Cursors.Arrow; };
        SizeChanged += (_, _) => Reset();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Unloaded += (_, _) => { _generation++; _frame?.Cancel(); _browserLoad?.Cancel(); _browser?.CloseView(); _browserReady = false; };
    }
    private void Reset() { _matrix = Matrix.Identity; Apply(); }
    private void Apply()
    {
        _surface.RenderTransform = new MatrixTransform(_matrix);
        AutomationProperties.SetItemStatus(this, (_matrix.M11 * 100).ToString("F0") + "%");
        RequestFrame();
    }
    private async void RequestFrame()
    {
        if (_svg is null || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_browserReady && _browser is { } browser)
        {
            try
            {
                await browser.SetViewportAsync(ActualWidth, ActualHeight, _aspect, _matrix);
                AutomationProperties.SetHelpText(this, "SVG browser viewport");
            }
            catch (Exception error) { ComponentAuditFailures.Report(error); ToolTip = _failureHint; }
            return;
        }
        if (_browserStarting) return;
        var generation = ++_generation; _frame?.Cancel();
        var cancel = new CancellationTokenSource(); _frame = cancel;
        _surface.Visibility = Visibility.Visible; _detail.Visibility = Visibility.Hidden;
        var width = ActualWidth; var height = ActualHeight; var matrix = _matrix;
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsWide = Math.Clamp((int)Math.Ceiling(width * dpi.DpiScaleX), 1, 8192);
        var pixelsHigh = Math.Clamp((int)Math.Ceiling(height * dpi.DpiScaleY), 1, 8192);
        var png = Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "ViewerFrames", Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await Task.Delay(180, cancel.Token);
            var svg = await Task.Run(() => VectorViewportFrame.Compose(_svg(), width, height, _aspect,
                matrix.M11, matrix.OffsetX, matrix.OffsetY), cancel.Token);
            await VectorRenderer.RenderFrameAsync(svg, png, pixelsWide, pixelsHigh, cancel.Token);
            var bitmap = await Task.Run(() =>
            {
                using var input = File.OpenRead(png); var image = new BitmapImage(); image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
            }, cancel.Token);
            if (generation != _generation || cancel.IsCancellationRequested || !IsLoaded) return;
            _detail.Source = bitmap; _detail.Visibility = Visibility.Visible; _surface.Visibility = Visibility.Hidden; ToolTip = _hint;
            AutomationProperties.SetHelpText(this, $"SVG viewport: {pixelsWide} × {pixelsHigh}");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("vector_viewport_render_failed", "Vector.Viewer", detail: error.GetType().Name);
            if (generation == _generation) ToolTip = _failureHint;
        }
        finally
        {
            if (_frame == cancel) _frame = null;
            cancel.Dispose();
            try { if (File.Exists(png)) File.Delete(png); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private async Task InitializeBrowserAsync()
    {
        if (_svg is null || _browserStarting || _browserReady) return;
        _browserStarting = true; _browserLoad = new();
        var browser = new VectorBrowserView(); _browser = browser; Children.Add(browser);
        browser.ViewportChanged += matrix =>
        {
            _matrix = matrix;
            AutomationProperties.SetItemStatus(this, (matrix.M11 * 100).ToString("F0") + "%");
        };
        try
        {
            var svg = await Task.Run(_svg, _browserLoad.Token);
            await browser.InitializeAsync(svg, _browserLoad.Token);
            if (!IsLoaded || _browserLoad.IsCancellationRequested) return;
            _browserReady = true; await browser.SetViewportAsync(ActualWidth, ActualHeight, _aspect, _matrix);
            browser.Visibility = Visibility.Visible; _surface.Visibility = Visibility.Hidden; _detail.Visibility = Visibility.Hidden;
            AutomationProperties.SetHelpText(this, "SVG browser viewport");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("vector_browser_unavailable", "Vector.Viewer", detail: error.ToString());
            browser.CloseView(); Children.Remove(browser); _browser = null;
        }
        finally { _browserStarting = false; _browserLoad.Dispose(); _browserLoad = null; }
        if (!_browserReady && IsLoaded) RequestFrame();
    }
}
