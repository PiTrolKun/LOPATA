using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.ImageLayers;
using Pixel = System.Drawing.Point;
using PixelPoint = System.Drawing.PointF;
using PixelRect = System.Drawing.Rectangle;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Controls.ImageLayers;

internal enum LayerCanvasTool { Hand, Region, Points, Pencil, Markers, EdgeBrush, Lasso }
internal enum LayerCanvasView { Original, Light, Dark }

internal sealed partial class LayerCanvas : FrameworkElement
{
    private static readonly Brush Cyan = FrozenBrush(Color.FromRgb(36, 221, 205));
    private static readonly Brush ScopeBrush = FrozenBrush(Color.FromArgb(50, 49, 177, 245));
    private static readonly Brush MatDark = FrozenBrush(Color.FromRgb(27, 30, 38));
    private static readonly Brush MatLight = FrozenBrush(Color.FromRgb(235, 237, 241));
    private BitmapSource? _sourceBitmap, _proof, _matteBitmap;
    private string? _matteKey;
    private PixelRect _matteBounds;
    private LayerMarker[] _markers = [];
    private PixelRect _proofBounds;
    private LayerSource? _source;
    private LayerMask? _mask;
    private Guid? _layer;
    private LayerPointLayout? _pointLayout;
    private List<Pixel[]> _rings = [];
    private PixelPoint[][] _outline = [];
    private int[][] _handles = [];
    private StreamGeometry? _boundary;
    private LayerCanvasView _view;
    private PixelRect? _scope;
    private PixelRect? _focusArea;
    private Vector _pan;
    private double _zoom = 1;
    private int _spacing = 8;
    private LayerCanvasTool _tool = LayerCanvasTool.Region;

    internal LayerCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Unloaded += (_, _) => StopLayerFocus();
    }

    internal LayerCanvasTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            var leavingLasso = _tool == LayerCanvasTool.Lasso;
            ClearGesture();
            _tool = value;
            if (leavingLasso) ResetLasso();
        }
    }
    internal bool ReadOnly { get; set; }
    internal bool ViewLocked { get; set; }
    internal event Action<PixelRect>? RegionSelected;
    internal event Action<LayerContourEdit>? ContourEdited;
    internal event Action<int>? ZoomChanged;
    internal event Action<LayerMarker>? MarkerPlaced;

    internal void ShowSource(LayerSource? source, PixelRect? focusArea = null, bool drawSource = true)
    {
        _source = source;
        _focusArea = focusArea;
        _sourceBitmap = drawSource && source is not null ? Bitmap(source.Pixels, source.Width, source.Height) : null;
        _mask = null;
        _layer = null;
        _pointLayout = null;
        _selectedPoints.Clear();
        ClearGesture();
        ResetLasso();
        _rings.Clear();
        _outline = [];
        _handles = [];
        _boundary = null;
        _proof = null;
        _matteKey = null;
        _matteBitmap = null;
        _markers = [];
        _edgeStrokes = [];
        _scope = null;
        Fit();
    }

    internal void ShowLayer(Guid? layer, LayerMask? mask, LayerPointLayout? points, int spacing, LayerCanvasView view,
        (LayerMatteReference Reference, LayerMatte Image)? matte = null)
    {
        var changedMask = !ReferenceEquals(_mask, mask);
        if (_layer != layer || changedMask) StopLayerFocus();
        var controlsChanged = changedMask || _spacing != spacing || _pointLayout != points;
        if (_layer != layer || controlsChanged)
        {
            _selectedPoints.Clear();
            ClearGesture();
            _layer = layer;
            _spacing = spacing;
            _pointLayout = points;
        }
        if (changedMask)
        {
            _mask = mask;
            _rings = _source is not null && mask?.Any == true ? LayerContours.Trace(mask, _source.Width) : [];
            _outline = _rings.Select(LayerContourSmoothing.Smooth).ToArray();
            _boundary = _outline.Length == 0 ? null : Geometry(_outline);
        }
        if (controlsChanged)
            _handles = mask is null ? [] : LayerPointLayout.Resolve(mask, spacing, _rings, points);
        if (matte?.Reference.Key != _matteKey || matte is not null && _matteBitmap is null && view != LayerCanvasView.Original)
        {
            _matteKey = matte?.Reference.Key;
            _matteBitmap = matte is { } data && view != LayerCanvasView.Original ? LoadMatte(data.Image) : null;
        }
        if (matte is { } item) _matteBounds = item.Reference.Bounds;
        _view = view;
        InvalidateVisual();
    }

    private static BitmapSource LoadMatte(LayerMatte image)
    {
        using var stream = new System.IO.MemoryStream(image.Png, writable: false);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        return bitmap;
    }

    internal void ShowMarkers(LayerMarker[] markers)
    {
        _markers = markers;
        InvalidateVisual();
    }

    internal void ShowProof(BitmapSource image, PixelRect area)
    {
        _proof = image;
        _proofBounds = area;
        InvalidateVisual();
    }

    internal void ShowRegion(PixelRect? area)
    {
        _scope = area;
        InvalidateVisual();
    }

    internal void Fit()
    {
        StopLayerFocus();
        _focusZoomLimit = 0;
        _zoom = 1;
        _pan = default;
        UpdateViewport();
    }

    internal void Zoom(double multiplier, Point? at = null)
    {
        if (_source is null || Scale <= 0) return;
        StopLayerFocus();
        var cursor = at ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var original = PixelAt(cursor);
        _zoom = Math.Clamp(_zoom * multiplier, .25, Math.Max(_focusZoomLimit, Math.Max(12, 10 / BaseScale)));
        _pan += cursor - ScreenAt(original);
        UpdateViewport();
    }

    private Rect Viewport => new(0, 0, ActualWidth, ActualHeight);
    private PixelRect FocusArea => _focusArea ?? _source?.Frame ?? new PixelRect(0, 0, 1, 1);
    private double BaseScale => FitScale(FocusArea);
    private double FitScale(PixelRect area) => Math.Max(.001, Math.Min(Math.Max(1, ActualWidth - 32) / area.Width,
        Math.Max(1, ActualHeight - 32) / area.Height));
    private double Scale => BaseScale * _zoom;
    private Point Origin => new((ActualWidth - FocusArea.Width * Scale) / 2 + _pan.X - FocusArea.X * Scale,
        (ActualHeight - FocusArea.Height * Scale) / 2 + _pan.Y - FocusArea.Y * Scale);
    private Point ScreenAt(PixelPoint point) => Origin + new Vector(point.X * Scale, point.Y * Scale);
    private PixelPoint PixelAt(Point position) => new((float)((position.X - Origin.X) / Scale), (float)((position.Y - Origin.Y) / Scale));
    private Rect ScreenRect(PixelRect area) => new(ScreenAt(new(area.X, area.Y)), new Size(area.Width * Scale, area.Height * Scale));
    private MatrixTransform SourceTransform()
    {
        var origin = Origin;
        return new(Scale, 0, 0, Scale, origin.X, origin.Y);
    }

    private void UpdateViewport()
    {
        InvalidateVisual();
        if (_viewportPeer is { } peer)
        {
            peer._zoom = _zoom;
            peer._pan = _pan;
            peer.InvalidateVisual();
        }
        if (_source is not null) ZoomChanged?.Invoke((int)Math.Round(Scale * 100));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        StopLayerFocus();
        base.OnRenderSizeChanged(sizeInfo);
        UpdateViewport();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle((Brush?)TryFindResource("WindowBackgroundBrush") ?? MatDark, null, Viewport);
        if (_source is null) return;
        drawing.PushClip(new RectangleGeometry(Viewport));
        var frame = ScreenRect(_source.Frame);
        drawing.DrawRectangle(_view == LayerCanvasView.Light ? MatLight : MatDark, null, frame);
        if (_view == LayerCanvasView.Original && _sourceBitmap is not null)
            drawing.DrawImage(_sourceBitmap, frame);
        else if (_matteBitmap is not null)
            drawing.DrawImage(_matteBitmap, ScreenRect(_matteBounds));
        else if (_sourceBitmap is not null && _boundary is not null)
            DrawCutout(drawing, _sourceBitmap, _boundary);
        if (_proof is not null) drawing.DrawImage(_proof, ScreenRect(_proofBounds));
        if (_view == LayerCanvasView.Original) DrawBoundary(drawing);
        if ((_rubberBand is { } end && _gestureStart is { } start ? PixelRectangle(start, end) : _scope) is { } scope
            && !scope.IsEmpty)
            drawing.DrawRectangle(ScopeBrush, new Pen(Cyan, 1.5) { DashStyle = DashStyles.Dash }, ScreenRect(scope));
        DrawGesture(drawing);
        DrawLasso(drawing);
        DrawEdgeGuides(drawing);
        foreach (var marker in _markers)
        {
            var point = ScreenAt(new(marker.X + .5f, marker.Y + .5f));
            drawing.DrawEllipse(marker.Foreground ? Cyan : Brushes.OrangeRed, new Pen(MatDark, 1.4), point, 5, 5);
        }
        drawing.Pop();
    }

    private void DrawBoundary(DrawingContext drawing)
    {
        if (_boundary is null) return;
        var scale = Scale;
        drawing.PushTransform(SourceTransform());
        drawing.DrawGeometry(null, new Pen(Brushes.Black, 3.6 / scale), _boundary);
        drawing.DrawGeometry(null, new Pen(Cyan, 1.6 / scale), _boundary);
        drawing.Pop();
        if (ReadOnly || Tool is not (LayerCanvasTool.Points or LayerCanvasTool.Pencil)) return;
        var viewport = Viewport;
        var outline = new Pen(MatDark, .8);
        foreach (var handle in VisibleControls())
        {
            var point = ScreenAt(Vertex(handle));
            if (!viewport.Contains(point)) continue;
            var selected = _selectedPoints.Contains(handle);
            var size = selected ? 7d : 5d;
            drawing.DrawRectangle(selected ? Cyan : Brushes.White, outline, new(point.X - size / 2, point.Y - size / 2, size, size));
        }
    }

    private static StreamGeometry Geometry(IEnumerable<PixelPoint[]> rings)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
            foreach (var ring in rings)
            {
                context.BeginFigure(new(ring[0].X, ring[0].Y), isFilled: true, isClosed: true);
                for (var i = 1; i < ring.Length; i++)
                {
                    var previous = ring[i - 1]; var next = ring[(i + 1) % ring.Length]; var at = ring[i];
                    var ax = at.X - previous.X; var ay = at.Y - previous.Y;
                    var bx = next.X - at.X; var by = next.Y - at.Y;
                    if (ax * by == ay * bx && ax * bx + ay * by >= 0) continue;
                    context.LineTo(new(at.X, at.Y), true, false);
                }
            }
        geometry.Freeze();
        return geometry;
    }

    private void DrawCutout(DrawingContext drawing, BitmapSource bitmap, StreamGeometry boundary)
    {
        drawing.PushTransform(SourceTransform());
        drawing.PushClip(boundary);
        drawing.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        drawing.Pop();
        drawing.Pop();
    }

    internal static BitmapSource Bitmap(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
