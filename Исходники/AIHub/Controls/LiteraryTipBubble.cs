using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Cursors = System.Windows.Input.Cursors;

namespace AIHub.Controls;

internal sealed record LiteraryTip(string Role, string Title, string Body, bool Writer,
    string Id = "", string Category = "");

/// <summary>A readable speech balloon. Holding it pauses only this balloon.</summary>
internal sealed class LiteraryTipBubble : Grid
{
    private readonly TranslateTransform _drift = new();
    private readonly TextBlock _role = LiteraryUi.Text("");
    private readonly TextBlock _title = LiteraryUi.Text("", true);
    private readonly TextBlock _body = LiteraryUi.Text("");
    private readonly Border _badge = new();
    private readonly SpeechSurface _surface;
    private readonly double _period;
    private AnimationClock? _floatClock;
    private int _transition;
    private bool _active, _motion;
    public bool IsHeld { get; private set; }
    public long ReadyAfter { get; private set; }
    public LiteraryTip? Tip { get; private set; }
    public event EventHandler? TipShown;

    public LiteraryTipBubble(bool tailRight, double period, string holdHint)
    {
        _period = period;
        Focusable = true; Cursor = Cursors.Hand; ToolTip = holdHint;
        RenderTransform = _drift;
        _surface = new SpeechSurface(tailRight)
        {
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 5, Opacity = .2 }
        };
        _surface.SetResourceReference(SpeechSurface.FillProperty, "PanelBrush");
        _surface.SetResourceReference(SpeechSurface.OutlineProperty, "TextPrimaryBrush");
        Children.Add(_surface);
        var text = new StackPanel { Margin = new Thickness(15, 11, 15, 29), IsHitTestVisible = false };
        _role.Margin = new Thickness(0); _role.FontWeight = FontWeights.SemiBold;
        _role.SetResourceReference(TextBlock.FontSizeProperty, "UiSmallFontSize");
        _role.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _badge.Child = _role; _badge.CornerRadius = new CornerRadius(7);
        _badge.Padding = new Thickness(9, 3, 9, 3);
        _badge.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _badge.BorderThickness = new Thickness(1);
        _title.SetResourceReference(TextBlock.FontSizeProperty, "UiSectionFontSize");
        _title.Margin = new Thickness(0, 6, 0, 4);
        _body.Margin = new Thickness(0);
        _body.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.Children.Add(_badge); text.Children.Add(_title); text.Children.Add(_body);
        Children.Add(text);
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_active) return;
            Focus();
            if (CaptureMouse()) Hold();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) => { Release(); e.Handled = true; };
        LostMouseCapture += (_, _) => Release();
        KeyDown += (_, e) => { if (e.Key == Key.Space) { Hold(); e.Handled = true; } };
        KeyUp += (_, e) => { if (e.Key == Key.Space) { Release(); e.Handled = true; } };
        LostKeyboardFocus += (_, _) => Release();
        Unloaded += (_, _) => SetActive(false);
    }

    public void SetTip(LiteraryTip tip, bool animate)
    {
        if (IsHeld) return;
        if (animate) { ChangeTip(() => tip); return; }
        CancelTransition();
        ApplyTip(tip);
    }

    public void ChangeTip(Func<LiteraryTip?> nextTip)
    {
        if (IsHeld) return;
        CancelTransition();
        if (_motion && Tip is not null)
        {
            var transition = _transition;
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(650))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            fade.Completed += (_, _) =>
            {
                if (transition != _transition || IsHeld || !_active) return;
                // Take from the saved deck only once the old card is gone.
                // Catching its fade therefore does not consume an unseen tip.
                var tip = nextTip();
                if (tip is not null) ApplyTip(tip);
                FadeIn();
            };
            BeginAnimation(OpacityProperty, fade);
            return;
        }
        var next = nextTip();
        if (next is not null) ApplyTip(next);
    }

    private void ApplyTip(LiteraryTip tip)
    {
        Tip = tip;
        _role.Text = tip.Role; _title.Text = tip.Title; _body.Text = tip.Body;
        if (tip.Writer) _role.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        else _role.Foreground = System.Windows.Media.Brushes.White;
        _badge.SetResourceReference(Border.BorderBrushProperty, tip.Writer ? "TextSecondaryBrush" : "AccentBrush");
        _badge.SetResourceReference(Border.BackgroundProperty, tip.Writer ? "WindowBackgroundBrush" : "AccentBrush");
        AutomationProperties.SetName(this, $"{tip.Role}. {tip.Title}. {tip.Body}");
        TipShown?.Invoke(this, EventArgs.Empty);
    }

    private void FadeIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(850))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void CancelTransition()
    {
        _transition++;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    public void SetActive(bool active)
    {
        _active = active;
        var motion = active && SystemParameters.ClientAreaAnimation;
        if (motion && !_motion && IsHeld) return;
        if (_motion != motion)
        {
            _motion = motion;
            if (motion)
            {
                _floatClock = (AnimationClock)new DoubleAnimation(-3.3, 3.3, TimeSpan.FromSeconds(_period))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                }.CreateClock(true);
                _drift.ApplyAnimationClock(TranslateTransform.YProperty, _floatClock);
                if (IsHeld) _floatClock.Controller?.Pause();
                else FadeIn();
            }
            else
            {
                var heldOffset = IsHeld ? _drift.Y : 0;
                _floatClock?.Controller?.Remove(); _floatClock = null;
                _drift.ApplyAnimationClock(TranslateTransform.YProperty, null);
                _drift.Y = heldOffset;
                CancelTransition();
            }
        }
        if (!active) Release();
    }

    private void Hold()
    {
        if (!_active || IsHeld) return;
        IsHeld = true;
        _floatClock?.Controller?.Pause();
        // A caught card stays fully readable even if caught during its fade-out.
        CancelTransition();
    }

    private void Release()
    {
        if (!IsHeld) return;
        IsHeld = false;
        ReadyAfter = Environment.TickCount64 + 15000;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _floatClock?.Controller?.Resume();
        if (!_motion) _drift.Y = 0;
        (Parent as UIElement)?.InvalidateMeasure();
    }

    private sealed class SpeechSurface(bool tailRight) : FrameworkElement
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush),
            typeof(SpeechSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(nameof(Outline), typeof(Brush),
            typeof(SpeechSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            var right = Math.Max(2, ActualWidth - 2); var bottom = Math.Max(26, ActualHeight - 20);
            const double radius = 22;
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                g.BeginFigure(new Point(radius, 2), true, true);
                g.LineTo(new Point(right - radius, 2), true, false);
                g.QuadraticBezierTo(new Point(right, 2), new Point(right, radius), true, false);
                g.LineTo(new Point(right, bottom - radius), true, false);
                g.QuadraticBezierTo(new Point(right, bottom), new Point(right - radius, bottom), true, false);
                var tail = tailRight ? Math.Max(70, right - 44) : Math.Min(82, right - 24);
                g.LineTo(new Point(tail, bottom), true, false);
                g.QuadraticBezierTo(new Point(tail - 2, bottom + 9), new Point(tailRight ? tail + 9 : tail - 38, bottom + 18), true, false);
                g.LineTo(new Point(tail - 24, bottom), true, false);
                g.LineTo(new Point(radius, bottom), true, false);
                g.QuadraticBezierTo(new Point(2, bottom), new Point(2, bottom - radius), true, false);
                g.LineTo(new Point(2, radius), true, false);
                g.QuadraticBezierTo(new Point(2, 2), new Point(radius, 2), true, false);
            }
            shape.Freeze();
            dc.DrawGeometry(Fill, new System.Windows.Media.Pen(Outline, 1.5), shape);
        }
    }
}
