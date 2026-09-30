using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace AIHub.Controls;

/// <summary>Visual-only lifetime for the existing update button. It never requests or installs updates.</summary>
public sealed class ApplicationUpdateAttention : IDisposable
{
    private readonly Button _button;
    private readonly Func<bool> _animationsAllowed;
    private readonly ScaleTransform _scale = new(1, 1);
    private FrameworkElement? _glow;
    private bool _active, _listening, _disposed;
    public bool IsAnimating { get; private set; }

    public ApplicationUpdateAttention(Button button, Func<bool>? animationsAllowed = null)
    {
        _button = button; _animationsAllowed = animationsAllowed ?? (() => SystemParameters.ClientAreaAnimation);
        // Content remains the accessible name/tooltip; the template draws the compact core.
        button.RenderTransform = _scale; button.RenderTransformOrigin = new Point(.5, .5);
        button.Loaded += Loaded; button.Unloaded += Unloaded; button.IsVisibleChanged += VisibleChanged;
        if (button.IsLoaded) Loaded(button, new RoutedEventArgs());
    }

    private void Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _button.ApplyTemplate();
        _glow = _button.Template.FindName("PART_UpdateGlow", _button) as FrameworkElement;
        if (!_listening) { SystemParameters.StaticPropertyChanged += PreferencesChanged; _listening = true; }
        UpdateVisibility();
    }
    private void Unloaded(object sender, RoutedEventArgs e)
    { Stop(); UnsubscribePreferences(); }
    private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisibility();
    private void UpdateVisibility()
    {
        if (_disposed || !_button.IsLoaded || !_button.IsVisible) { Stop(); return; }
        if (_active) return;
        _active = true; Animate(appear: true);
    }

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            _button.Dispatcher.BeginInvoke(new Action(RefreshAnimationPreference));
    }
    public void RefreshAnimationPreference()
    {
        if (_disposed || !_active) return;
        StopClocks(); Animate(appear: false);
    }
    private void Animate(bool appear)
    {
        if (!_animationsAllowed()) { IsAnimating = false; return; }
        IsAnimating = true;
        if (appear)
        {
            var duration = TimeSpan.FromMilliseconds(650);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            _button.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.22, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.22, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        }
        _glow?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(900))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }
    private void StopClocks()
    {
        _button.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _glow?.BeginAnimation(UIElement.OpacityProperty, null);
        IsAnimating = false;
    }
    private void Stop() { StopClocks(); _active = false; }
    private void UnsubscribePreferences()
    { if (_listening) { SystemParameters.StaticPropertyChanged -= PreferencesChanged; _listening = false; } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Stop(); UnsubscribePreferences();
        _button.Loaded -= Loaded; _button.Unloaded -= Unloaded; _button.IsVisibleChanged -= VisibleChanged;
    }
}
