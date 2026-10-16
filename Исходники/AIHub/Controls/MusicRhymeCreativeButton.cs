using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Controls;

/// <summary>A normal accessible toggle with a bounded, visible-only decorative animation.</summary>
internal sealed class MusicRhymeCreativeButton : ToggleButton, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Artwork _artwork;
    private readonly Func<string, string> _l;
    private Window? _window;
    private bool _subscribed, _disposed;
    internal bool IsAnimating => _timer.IsEnabled;
    internal event Action<bool>? ToggledByUser;

    public MusicRhymeCreativeButton(Func<string, string> l)
    {
        _l = l;
        Width = Height = 44; Padding = new Thickness(0); ClipToBounds = false;
        Background = Brushes.Transparent; BorderThickness = new Thickness(0);
        Cursor = System.Windows.Input.Cursors.Hand;
        Focusable = true; IsThreeState = false;
        AutomationProperties.SetAutomationId(this, "Music.Rhyme.Creative");
        AutomationProperties.SetName(this, l("Music.Rhyme.Creative"));
        AutomationProperties.SetHelpText(this, l("Music.Rhyme.Creative.Hint"));
        // Separate translucent layers keep the icon bright and follow live theme resources.
        var templateRoot = new FrameworkElementFactory(typeof(Grid));
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        fill.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
        fill.SetValue(OpacityProperty, .025); templateRoot.AppendChild(fill);
        var frame = new FrameworkElementFactory(typeof(Border), "PART_CreativeFrame");
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
        frame.SetValue(OpacityProperty, .2); templateRoot.AppendChild(frame);
        templateRoot.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = templateRoot };
        _artwork = new Artwork(this); Content = _artwork;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Tick;
        Loaded += LoadedButton; Unloaded += UnloadedButton; IsVisibleChanged += VisibilityChanged;
        IsEnabledChanged += VisibilityChanged;
        Checked += StateChanged; Unchecked += StateChanged;
        MouseEnter += MouseChanged; MouseLeave += MouseChanged; IsKeyboardFocusedChanged += Redraw;
        UpdateTooltip();
    }

    protected override void OnToggle()
    {
        if (_disposed) return;
        base.OnToggle();
        // Mouse, keyboard and the native UI Automation Toggle pattern all use OnToggle.
        // Programmatic IsChecked synchronization deliberately does not save preferences.
        ToggledByUser?.Invoke(IsChecked == true);
    }

    private void StateChanged(object sender, RoutedEventArgs args)
    { UpdateTooltip(); UpdateAnimation(); }
    private void UpdateTooltip() => ToolTip = _l("Music.Rhyme.Creative") + " · "
        + _l(IsChecked == true ? "Music.Rhyme.Creative.On" : "Music.Rhyme.Creative.Off")
        + "\n" + _l("Music.Rhyme.Creative.Hint");
    private void Redraw(object sender, DependencyPropertyChangedEventArgs args) => _artwork.InvalidateVisual();
    private void MouseChanged(object sender, System.Windows.Input.MouseEventArgs args) => _artwork.InvalidateVisual();
    private void LoadedButton(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        if (!_subscribed) { SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged; _subscribed = true; }
        var window = Window.GetWindow(this);
        if (_window != window)
        {
            DetachWindow(); _window = window;
            if (_window is not null) { _window.StateChanged += WindowStateChanged; _window.Closed += WindowClosed; }
        }
        UpdateAnimation();
    }
    private void UnloadedButton(object sender, RoutedEventArgs args) { Stop(); DetachPreferences(); DetachWindow(); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateAnimation();
    private void WindowStateChanged(object? sender, EventArgs args) => UpdateAnimation();
    private void WindowClosed(object? sender, EventArgs args) => Dispose();
    private void AnimationPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(new Action(UpdateAnimation));
    }
    private void UpdateAnimation()
    {
        var animate = !_disposed && IsLoaded && IsVisible && IsEnabled && IsChecked == true
            && _window?.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;
        if (animate && !_timer.IsEnabled) { _clock.Restart(); _timer.Start(); }
        else if (!animate) Stop();
        _artwork.InvalidateVisual();
    }
    private void Tick(object? sender, EventArgs args) => _artwork.InvalidateVisual();
    private void Stop() { _timer.Stop(); _clock.Reset(); _artwork.InvalidateVisual(); }
    private void DetachPreferences()
    { if (_subscribed) { SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged; _subscribed = false; } }
    private void DetachWindow()
    {
        if (_window is null) return;
        _window.StateChanged -= WindowStateChanged; _window.Closed -= WindowClosed; _window = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Stop(); DetachPreferences(); DetachWindow();
        _timer.Tick -= Tick;
        Loaded -= LoadedButton; Unloaded -= UnloadedButton; IsVisibleChanged -= VisibilityChanged;
        IsEnabledChanged -= VisibilityChanged; Checked -= StateChanged; Unchecked -= StateChanged;
        MouseEnter -= MouseChanged; MouseLeave -= MouseChanged; IsKeyboardFocusedChanged -= Redraw;
        ToggledByUser = null;
    }

    private sealed class Artwork : FrameworkElement
    {
        private readonly MusicRhymeCreativeButton _owner;
        private static readonly BitmapSource Image = LoadImage();
        private static readonly BitmapSource GrayImage = MakeGrayImage();
        private static readonly Brush AlphaMask = MakeAlphaMask();
        private static readonly Brush Glow = MakeGlow();
        private static readonly Brush Gold = MakeBrush(Color.FromRgb(255, 208, 60));
        private static readonly Brush Bright = MakeBrush(Color.FromRgb(255, 242, 171));
        private static readonly Geometry FourPointStar = Star(4);
        private static readonly Geometry FivePointStar = Star(5);
        public Artwork(MusicRhymeCreativeButton owner) { _owner = owner; ClipToBounds = false; }
        protected override Size MeasureOverride(Size availableSize) => new(44, 44);
        protected override void OnRender(DrawingContext dc)
        {
            var active = _owner.IsChecked == true;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (active)
            {
                var pulse = _owner.IsAnimating ? .78 + .15 * Math.Sin(_owner._clock.Elapsed.TotalSeconds * 3.5) : .85;
                dc.PushOpacity(pulse); dc.DrawEllipse(Glow, null, center, 25, 25); dc.Pop();
            }
            var imageBounds = new Rect(center.X - 18, center.Y - 18, 36, 36);
            if (active) dc.DrawImage(Image, imageBounds);
            else
            {
                // Gray8 has no alpha; the original image supplies its untouched transparency.
                dc.PushOpacity(_owner.IsEnabled ? .48 : .28); dc.PushOpacityMask(AlphaMask);
                dc.DrawImage(GrayImage, imageBounds); dc.Pop(); dc.Pop();
            }
            if (active)
            {
                if (_owner.IsAnimating) DrawParticles(dc, center, _owner._clock.Elapsed.TotalSeconds);
                else DrawStar(dc, new Point(center.X + 15, center.Y - 13), 3.1, 0, 1, FourPointStar);
            }
            if (_owner.IsKeyboardFocused || _owner.IsMouseOver)
            {
                var outline = active ? Gold : _owner.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
                dc.PushOpacity(_owner.IsKeyboardFocused ? .95 : .45);
                dc.DrawRoundedRectangle(null, new Pen(outline, _owner.IsKeyboardFocused ? 1.5 : 1),
                    new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 9, 9);
                dc.Pop();
            }
        }
        private static void DrawParticles(DrawingContext dc, Point center, double time)
        {
            // Exactly six deterministic particles; there is no retained particle collection to grow.
            for (var i = 0; i < 6; i++)
            {
                var age = (time / 1.9 + i / 6.0) % 1;
                var side = i % 2 == 0 ? -1 : 1;
                var x = center.X + side * (8 + age * (13 + i % 3 * 3));
                var y = center.Y + 4 - age * (25 + i % 3 * 5);
                var opacity = Math.Sin(age * Math.PI) * .95;
                DrawStar(dc, new Point(x, y), (2.3 + i % 3 * .65) * (.7 + .3 * Math.Sin(age * Math.PI)),
                    side * age * 65, opacity, i % 2 == 0 ? FourPointStar : FivePointStar);
            }
        }
        private static void DrawStar(DrawingContext dc, Point at, double radius, double angle, double opacity, Geometry shape)
        {
            dc.PushOpacity(opacity); dc.PushTransform(new TranslateTransform(at.X, at.Y));
            dc.PushTransform(new RotateTransform(angle)); dc.PushTransform(new ScaleTransform(radius, radius));
            dc.DrawGeometry(Gold, null, shape); dc.DrawEllipse(Bright, null, new Point(), .13, .13);
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
        private static Geometry Star(int points)
        {
            var shape = new StreamGeometry();
            using (var context = shape.Open())
            {
                for (var i = 0; i < points * 2; i++)
                {
                    var angle = Math.PI * i / points - Math.PI / 2; var radius = i % 2 == 0 ? 1 : .3;
                    var point = new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                    if (i == 0) context.BeginFigure(point, true, true); else context.LineTo(point, true, false);
                }
            }
            shape.Freeze(); return shape;
        }
        private static BitmapSource LoadImage()
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128;
            bitmap.UriSource = new Uri("pack://application:,,,/AIHub;component/Assets/music-rhyme-creative.png");
            bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        private static BitmapSource MakeGrayImage()
        { var bitmap = new FormatConvertedBitmap(Image, PixelFormats.Gray8, null, 0); bitmap.Freeze(); return bitmap; }
        private static Brush MakeAlphaMask()
        { var brush = new ImageBrush(Image) { Stretch = Stretch.Fill }; brush.Freeze(); return brush; }
        private static Brush MakeGlow()
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(100, 255, 201, 39), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(32, 255, 201, 39), .58));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 201, 39), 1));
            brush.Freeze(); return brush;
        }
        private static Brush MakeBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    }
}
