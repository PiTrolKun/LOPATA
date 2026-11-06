using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

/// <summary>WPF game surface; a bounded simulation step and vector drawing share the UI thread.</summary>
public sealed partial class LivingArcadeGameHost : FrameworkElement, IDisposable
{
    private readonly Dictionary<string, SolidColorBrush> _colors = new(StringComparer.Ordinal);
    private readonly LivingArcadeSpriteRenderer _sprites = new();
    private readonly StreamGeometry _triangle = Polygon([new(0, -1), new(1, 1), new(-1, 1)]);
    private readonly StreamGeometry _star = MakeStar();
    private readonly HwndSourceHook _keyboardHook;
    private HwndSource? _source;
    private LivingArcadeSimulation? _simulation;
    private bool _running;
    private bool _subscribed;
    private bool _focusPending;
    private bool _padZero;
    private bool _escape;
    private bool _released;
    private bool _renderFailed;
    private bool _graphicsReleased;
    private long _lastFrame;
    private double _hitFlash;
    private double _lastHealth = 100;

    public LivingArcadeAudio Audio { get; } = new();
    public string BackgroundColor { get; set; } = "#09121F";
    public LivingArcadeSnapshot? Snapshot => _simulation?.Snapshot;
    public bool IsRunning => _running;
    public event Action<LivingArcadeSnapshot>? FrameAdvanced;
    public event Action? PauseRequested;
    public event Action<Exception>? RenderingFailed;

    public LivingArcadeGameHost()
    {
        Focusable = true;
        ClipToBounds = true;
        _keyboardHook = KeyboardMessage;
        Loaded += (_, _) =>
        {
            _source = PresentationSource.FromVisual(this) as HwndSource;
            _source?.AddHook(_keyboardHook);
            if (_graphicsReleased && _simulation is not null)
            {
                try { PrepareSprites(_simulation); PrepareScenery(); _graphicsReleased = false; }
                catch (Exception error) when (error is not OutOfMemoryException)
                { _renderFailed = true; RenderingFailed?.Invoke(error); }
            }
            if (_running) { Subscribe(); FocusKeyboard(); }
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            ReleaseGraphics();
            _source?.RemoveHook(_keyboardHook); _source = null;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible && _running) RequestPause(); };
        PreviewMouseDown += (_, args) => { FocusKeyboard(); args.Handled = true; };
        PreviewKeyDown += (_, args) =>
        {
            var key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (key == Key.Escape) _escape = true;
            if (IsGameKey(key)) args.Handled = true;
        };
        PreviewKeyUp += (_, args) => { if (IsGameKey(args.Key)) args.Handled = true; };
    }

    internal LivingArcadeGameHost(LivingArcadeLibrary library) : this()
    { _sprites = new(library); _bossLibrary = library; }

    public void SetSimulation(LivingArcadeSimulation? simulation)
    {
        Stop();
        if (simulation is not null) PrepareSprites(simulation);
        else _sprites.Clear();
        if (_simulation is not null) _simulation.Sound -= Audio.Play;
        _simulation = simulation;
        if (_simulation is not null)
        {
            _simulation.Sound += Audio.Play;
            _lastHealth = _simulation.Snapshot.Health;
            PrepareScenery();
        }
        else ClearScenery();
        _renderFailed = false; _graphicsReleased = false; _hitFlash = 0; _colors.Clear(); ClearInput(); InvalidateVisual();
    }

    public void Start()
    {
        if (_released || _simulation is null) return;
        if (_sprites.PreparedCount == 0) PrepareSprites(_simulation);
        if (!IsSceneryPrepared) PrepareScenery();
        _graphicsReleased = false;
        _running = true; _simulation.SetPaused(false);
        _lastFrame = Stopwatch.GetTimestamp(); _focusPending = true;
        ClearInput(); Subscribe(); Audio.Resume(); FocusKeyboard();
    }

    public void Stop()
    {
        _running = false; _simulation?.SetPaused(true);
        if (_subscribed) { CompositionTarget.Rendering -= RenderFrame; _subscribed = false; }
        ClearInput(); _lastFrame = 0;
        Audio.Pause(Snapshot is { IsComplete: true } or { IsDefeated: true });
        InvalidateVisual();
    }

    public void ClearInput() { _padZero = false; _escape = false; }
    public void FocusKeyboard()
    {
        if (!IsLoaded || Window.GetWindow(this)?.IsActive != true) return;
        Focus(); Keyboard.Focus(this); _focusPending = !IsKeyboardFocusWithin;
    }

    public LivingArcadeInput ReadInput()
    {
        if (!_running || !IsKeyboardFocusWithin || Window.GetWindow(this)?.IsActive != true) return default;
        return new(Down(0x41) || Down(0x25), Down(0x44) || Down(0x27),
            Down(0x57) || Down(0x26), Down(0x53) || Down(0x28), Down(0x46) || Down(0x60) || _padZero);
    }

    private IntPtr KeyboardMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsKeyboardFocusWithin && Window.GetWindow(this)?.IsActive == true && msg is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            var scan = (lParam.ToInt64() >> 16) & 0xFF;
            var extended = (lParam.ToInt64() & (1L << 24)) != 0;
            // Physical NumPad 0, including NumLock off; main Insert is extended and does not trigger F.
            if (scan == 0x52 && !extended) _padZero = msg is 0x0100 or 0x0104;
        }
        return IntPtr.Zero;
    }

    private static bool IsGameKey(Key key) => key is Key.A or Key.D or Key.W or Key.S or Key.F
        or Key.Left or Key.Right or Key.Up or Key.Down or Key.NumPad0 or Key.Insert or Key.Escape;
    private void Subscribe()
    {
        if (_subscribed || !IsLoaded) return;
        CompositionTarget.Rendering += RenderFrame; _subscribed = true;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        if (!_running || _released || _simulation is null) return;
        if (!IsVisible || Window.GetWindow(this)?.IsActive != true) { RequestPause(); return; }
        if (_focusPending) FocusKeyboard();
        if (_escape || (!_focusPending && !IsKeyboardFocusWithin)) { RequestPause(); return; }
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastFrame == 0 ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds, 0, .1);
        _lastFrame = now;
        try
        {
            _simulation.Advance(seconds, ReadInput());
            var snapshot = _simulation.Snapshot;
            if (snapshot.Health < _lastHealth) _hitFlash = .20;
            _lastHealth = snapshot.Health; _hitFlash = Math.Max(0, _hitFlash - seconds);
            Audio.Update(); InvalidateVisual(); FrameAdvanced?.Invoke(snapshot);
            if (snapshot.IsComplete || snapshot.IsDefeated) Stop();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Stop(); RenderingFailed?.Invoke(error); }
    }

    private void RequestPause() { Stop(); PauseRequested?.Invoke(); }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : 640,
        double.IsFinite(availableSize.Height) ? availableSize.Height : 600);
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_released || _graphicsReleased || _renderFailed || ActualWidth < 1 || ActualHeight < 1) return;
        try { Draw(context, Snapshot); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _renderFailed = true;
            Stop();
            Dispatcher.InvokeAsync(() => RenderingFailed?.Invoke(error));
        }
    }

    private void Draw(DrawingContext dc, LivingArcadeSnapshot? snapshot)
    {
        var width = ActualWidth; var viewportHeight = ActualHeight;
        Fill(dc, 0, 0, width, viewportHeight, BackgroundColor);
        if (snapshot is null) return;
        // The simulation metric is unchanged; the optional camera projects that same world for every object.
        var height = Math.Min(width * .88, viewportHeight);
        var roadWidth = height;
        var left = (width - roadWidth) / 2;
        dc.PushTransform(new TranslateTransform(0, (viewportHeight - height) / 2));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        if (_generatedBackground.IsPrepared)
            _generatedBackground.Draw(dc, width, height, left, roadWidth, snapshot.Progress,
                ViewMode == LivingArcadeViewMode.Perspective);
        else if (ViewMode == LivingArcadeViewMode.Perspective)
            _perspectiveBackground.Draw(dc, width, height, left, roadWidth, snapshot.Progress);
        else _background.Draw(dc, width, height, left, roadWidth, snapshot.Progress);
        DrawSceneActors(dc, snapshot, left, roadWidth, height);
        // Projectiles are foreground objects: the first shot must not disappear behind its own hero sprite.
        foreach (var shot in snapshot.Shots)
            DrawProjectile(dc, shot, left, roadWidth, height);
        if (snapshot.ActiveEffect.Length != 0 && snapshot.EffectSeconds > 0)
            Fill(dc, left, height - 4, roadWidth, 4, snapshot.ActiveEffect.Contains("slow", StringComparison.OrdinalIgnoreCase) ? "#50AAF5" : "#EEB54A");
        if (_hitFlash > 0)
        {
            dc.PushOpacity(_hitFlash * 2);
            Fill(dc, 0, 0, width, 9, "#E6414E"); Fill(dc, 0, height - 9, width, 9, "#E6414E");
            Fill(dc, 0, 0, 9, height, "#E6414E"); Fill(dc, width - 9, 0, 9, height, "#E6414E"); dc.Pop();
        }
        dc.Pop(); dc.Pop();
    }

    private void DrawEntity(DrawingContext dc, LivingArcadeEntityView entity, double left, double width, double height, bool pickup)
    {
        if (entity.Y is < -.2 or > 1.2) return;
        var center = ProjectWorld(entity.X, entity.Y, left, width, height);
        var depth = DepthScale(entity.Y);
        var radius = Math.Max(ViewMode == LivingArcadeViewMode.Perspective ? 4 : 7, entity.Radius * width * depth);
        var color = entity.Flash > 0 ? "#FFFFFF" : entity.Color;
        dc.PushOpacity(.27);
        if (ViewMode == LivingArcadeViewMode.Perspective)
            dc.DrawEllipse(Brush("#000000"), null, center + new Vector(2 * depth, radius * .65), radius * 1.15, radius * .38);
        else Disc(dc, center + new Vector(2, radius * .4), radius * 1.15, "#000000");
        dc.Pop();
        if (entity.Telegraph)
        {
            dc.PushOpacity(.2 + .15 * Math.Sin(entity.Age * 20));
            Disc(dc, center, radius * 1.8, "#FFB34D"); dc.Pop();
            Line(dc, center + new Vector(0, radius * 1.3), center + new Vector(0, radius * 3.2), 2, "#FFB34D");
        }
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(LivingArcadeSpriteRenderer.AnimationTransform(entity.Visual, entity.Age, radius));
        var illustrated = _sprites.DrawBody(dc, entity.Visual, entity.Age, radius, entity.Flash > 0);
        if (!illustrated) DrawProceduralBody(dc, entity.Shape, radius, color);
        if (pickup)
        {
            DrawPickupGlyph(dc, entity.Id, radius);
        }
        else if (!illustrated)
        {
            Disc(dc, new(-radius * .3, -radius * .18), Math.Max(2, radius * .16), "#121724");
            Disc(dc, new(radius * .3, -radius * .18), Math.Max(2, radius * .16), "#121724");
        }
        _sprites.DrawLayers(dc, entity.Visual, entity.Age, radius, entity.Flash > 0);
        _sprites.DrawDecoration(dc, entity.Visual, radius, entity.Flash > 0);
        dc.Pop(); dc.Pop();
        if (entity.IsBoss) dc.DrawEllipse(null, new Pen(Brush(entity.BossPhase == 3 ? "#FF757F" : "#FFB34D"),
            Math.Max(2, radius * .035)), center, radius * 1.08, radius * 1.08);
        if (entity.ShieldMode == "full") dc.DrawEllipse(null,
            new Pen(Brush("#76E9FF"), Math.Max(2, radius * .07)), center, radius * 1.18, radius * 1.18);
        else if (entity.ShieldMode == "rotating")
        {
            if (ViewMode == LivingArcadeViewMode.Perspective) DrawProjectedShield(dc, entity, left, width, height, radius);
            else DrawShield(dc, center, radius, entity.WeakSpotAngleDegrees, entity.WeakSpotWidthDegrees);
        }
        if (!pickup && (entity.HealthFraction < .999 || entity.ExperienceValue >= 5))
        {
            Fill(dc, center.X - radius, center.Y - radius - 8 * depth, radius * 2, Math.Max(2, 4 * depth), "#2A313C");
            Fill(dc, center.X - radius, center.Y - radius - 8 * depth, radius * 2 * Math.Clamp(entity.HealthFraction, 0, 1), Math.Max(2, 4 * depth),
                entity.IsBoss || entity.ExperienceValue >= 5 ? "#FFB34D" : "#6FD08B");
        }
    }

    private void DrawProceduralBody(DrawingContext dc, string shape, double radius, string color)
    {
        if (shape == "diamond") dc.PushTransform(new RotateTransform(45));
        if (shape is "triangle" or "star")
        {
            dc.PushTransform(new ScaleTransform(radius, radius));
            dc.DrawGeometry(Brush(color), null, shape == "star" ? _star : _triangle); dc.Pop();
        }
        else if (shape == "circle") Disc(dc, new(), radius, color);
        else Fill(dc, -radius, -radius, radius * 2, radius * 2, color);
        if (shape == "diamond") dc.Pop();
    }

    private void DrawShield(DrawingContext dc, Point center, double radius, double openingDegrees, double openingWidth)
    {
        var start = (openingDegrees + openingWidth / 2 - 90) * Math.PI / 180;
        var end = (openingDegrees - openingWidth / 2 - 90) * Math.PI / 180;
        var r = radius * 1.18;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(center + new Vector(Math.Cos(start) * r, Math.Sin(start) * r), false, false);
            path.ArcTo(center + new Vector(Math.Cos(end) * r, Math.Sin(end) * r), new(r, r), 0,
                360 - openingWidth > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(null, new Pen(Brush("#76E9FF"), Math.Max(2, radius * .07)), geometry);
    }

    private void DrawShovel(DrawingContext dc, Point p, double scale, double aim, bool defeated)
    {
        var metal = defeated ? "#5F6B77" : "#9AD8DF";
        var wood = "#BA7D48";
        dc.PushOpacity(.35); Disc(dc, p + new Vector(0, 17 * scale), 22 * scale, "#000000"); dc.Pop();
        dc.PushTransform(new TranslateTransform(p.X, p.Y)); dc.PushTransform(new ScaleTransform(scale, scale));
        Line(dc, new(-6, 12), new(-11, 27), 5, wood); Line(dc, new(6, 12), new(11, 27), 5, wood);
        Line(dc, new(-12, -1), new(-24, 7), 5, metal); Line(dc, new(12, -1), new(21, -7), 5, metal);
        Fill(dc, -4, -33, 8, 49, wood); Fill(dc, -9, -40, 18, 5, metal);
        dc.PushTransform(new TranslateTransform(0, -3)); dc.PushTransform(new ScaleTransform(17, -19));
        dc.DrawGeometry(Brush(metal), null, _triangle); dc.Pop(); dc.Pop();
        Disc(dc, new(-6, -7), 2.4, "#111C2C"); Disc(dc, new(6, -7), 2.4, "#111C2C");
        var gun = new Point(0, 0); var direction = new Vector(Math.Sin(aim), -Math.Cos(aim));
        Line(dc, new(21, -7), gun, 5, metal);
        Line(dc, gun, gun + direction * 23, 7, "#364155");
        Line(dc, gun + direction * 14, gun + direction * 26, 3, "#F1D375");
        dc.Pop(); dc.Pop();
    }

    private static Point World(double x, double y, double left, double width, double height) => new(left + x * width, y * height);
    private void Fill(DrawingContext dc, double x, double y, double width, double height, string color)
    { if (width > 0 && height > 0) dc.DrawRectangle(Brush(color), null, new(x, y, width, height)); }
    private void Disc(DrawingContext dc, Point center, double radius, string color) => dc.DrawEllipse(Brush(color), null, center, radius, radius);
    private void Line(DrawingContext dc, Point a, Point b, double width, string color) => dc.DrawLine(new Pen(Brush(color), width), a, b);
    private SolidColorBrush Brush(string value)
    {
        if (_colors.TryGetValue(value, out var cached)) return cached;
        var color = Colors.White;
        if (value.Length == 7 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            color = Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        var brush = new SolidColorBrush(color); brush.Freeze();
        if (_colors.Count < 128) _colors[value] = brush;
        return brush;
    }

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        geometry.Freeze(); return geometry;
    }
    private static StreamGeometry MakeStar() => Polygon(Enumerable.Range(0, 10).Select(i =>
    { var angle = i * Math.PI / 5 - Math.PI / 2; var r = i % 2 == 0 ? 1 : .47; return new Point(Math.Cos(angle) * r, Math.Sin(angle) * r); }).ToArray());

    public void ReleaseGraphics() { Stop(); _graphicsReleased = true; _colors.Clear(); _sprites.Clear(); ClearScenery(); }
    public void Dispose()
    {
        if (_released) return;
        _released = true; Stop();
        _source?.RemoveHook(_keyboardHook); _source = null;
        if (_simulation is not null) _simulation.Sound -= Audio.Play;
        _simulation = null; Audio.Dispose(); _colors.Clear(); _sprites.Clear(); ClearScenery();
    }
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
