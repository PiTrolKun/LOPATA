using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Path = System.Windows.Shapes.Path;

namespace AIHub.Controls;

internal sealed class MusicPoetryButtonIndicator
{
    private readonly Button _button;
    private readonly Path _icon;
    private readonly SolidColorBrush _red = new(Color.FromRgb(239, 68, 68));
    private bool _open, _working;

    public MusicPoetryButtonIndicator(Button button)
    {
        _button = button; _icon = (Path)((Viewbox)button.Content).Child;
        button.Loaded += (_, _) => Refresh();
        button.IsVisibleChanged += (_, _) => Refresh();
        button.Unloaded += (_, _) => _red.BeginAnimation(SolidColorBrush.ColorProperty, null);
    }

    public void SetState(bool open, bool working)
    {
        if (_open == open && _working == working) return;
        _open = open; _working = working; Refresh();
    }

    private void Refresh()
    {
        _red.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (!_open)
        {
            _icon.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
            _button.ClearValue(Control.BorderBrushProperty);
            return;
        }
        _icon.Stroke = _red; _button.BorderBrush = _red;
        if (!_working && _button.IsLoaded && _button.IsVisible && SystemParameters.ClientAreaAnimation)
            _red.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_red.Color, Colors.Transparent,
                TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}
