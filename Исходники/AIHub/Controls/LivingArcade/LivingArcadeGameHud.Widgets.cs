using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHud
{
    private static readonly Brush Background = Brush("#F20C1524");
    private static readonly Brush Well = Brush("#E7101E30");
    private static readonly Brush Edge = Brush("#3A4C65");
    private static readonly Brush ReadyEdge = Brush("#477A89");
    private static readonly Brush Foreground = Brush("#F1F5FB");
    private static readonly Brush Muted = Brush("#ACBBD0");
    private static readonly Brush Cyan = Brush("#76D9E9");
    private static readonly Brush Red = Brush("#BE3648");
    private static readonly Brush Blue = Brush("#2869BD");
    private static readonly Brush Gold = Brush("#FFD27D");
    private static readonly Brush Warning = Brush("#FFAD9D");

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Border Panel(string id, UIElement child, Thickness padding)
    {
        var panel = new Border
        {
            Background = Background, BorderBrush = Edge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = padding, Child = child,
            SnapsToDevicePixels = true, UseLayoutRounding = true
        };
        AutomationProperties.SetAutomationId(panel, "Arcade.Hud." + id);
        return panel;
    }

    private static TextBlock Text(string id, double size, Brush color, FontWeight weight = default, bool wrap = false)
    {
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = size,
            FontWeight = weight == default ? FontWeights.Normal : weight,
            Foreground = color, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(text, "Arcade.Hud." + id);
        return text;
    }

    private static TextBlock Detail(string id, Brush color, int lines = 2)
    {
        var text = Text(id, 11, color, FontWeights.Normal, true);
        text.LineHeight = 15;
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        text.MaxHeight = 15 * lines;
        text.Margin = new Thickness(0, 5, 0, 0);
        text.Visibility = Visibility.Collapsed;
        return text;
    }

    private static Border Divider() => new()
    {
        Background = Edge, Height = 1, Margin = new Thickness(0, 9, 0, 9)
    };

    private static void SetText(TextBlock text, string value)
    {
        text.Text = value;
        text.ToolTip = value;
        AutomationProperties.SetName(text, value);
    }

    private static void Optional(TextBlock text, string value)
    {
        SetText(text, value);
        text.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A deterministic, theme-independent meter; values never enter a pixel-width calculation.</summary>
    private sealed class Meter
    {
        internal Border Surface { get; }
        private readonly ColumnDefinition _filled = new();
        private readonly ColumnDefinition _empty = new();
        private readonly TextBlock _label;

        internal Meter(string id, string textId, Brush color, double height, double fontSize, FontWeight weight)
        {
            var root = new Grid { ClipToBounds = true };
            var track = new Grid { Background = Well };
            track.ColumnDefinitions.Add(_filled);
            track.ColumnDefinitions.Add(_empty);
            var fill = new Border { Background = color };
            AutomationProperties.SetAutomationId(fill, "Arcade.Hud." + id + ".Fill");
            track.Children.Add(fill);
            root.Children.Add(track);
            _label = Text(textId, fontSize, Foreground, weight);
            _label.TextAlignment = TextAlignment.Center;
            _label.Margin = new Thickness(4, 0, 4, 0);
            root.Children.Add(_label);
            Surface = new Border
            {
                Height = height, Child = root, Background = Well, BorderBrush = Edge,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                SnapsToDevicePixels = true, UseLayoutRounding = true
            };
            AutomationProperties.SetAutomationId(Surface, "Arcade.Hud." + id);
        }

        internal void Update(double fraction, string label, string name, string help)
        {
            var safe = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
            _filled.Width = new GridLength(safe, GridUnitType.Star);
            _empty.Width = new GridLength(1 - safe, GridUnitType.Star);
            SetText(_label, label);
            AutomationProperties.SetName(_label, name);
            AutomationProperties.SetName(Surface, name);
            AutomationProperties.SetHelpText(Surface, help);
            Surface.ToolTip = name;
        }
    }
}
