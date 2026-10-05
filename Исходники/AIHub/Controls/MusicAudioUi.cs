using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;

namespace AIHub.Controls;

internal static class MusicAudioUi
{
    public static TextBlock Text(double size = 13)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    public static Button IconButton(string name, string geometry, Action action)
    {
        var button = new Button { Content = Icon(geometry), MinWidth = 0, MinHeight = 0, Width = 30, Height = 30,
            Padding = new(5), Margin = new(2) };
        AutomationProperties.SetAutomationId(button, "Music.Audio." + name);
        ToolTipService.SetShowOnDisabled(button, true); button.Click += (_, _) => action(); return button;
    }
    public static Viewbox Icon(string geometry)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextPrimaryBrush");
        return new Viewbox { Child = path, Width = 18, Height = 18 };
    }
    public static void Label(Button button, string text) { button.ToolTip = text; AutomationProperties.SetName(button, text); }
    public static string Time(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
    public static string Shorten(string path, double width, Func<string, double> measure)
    {
        if (measure(path) <= width) return path;
        const string dots = "...";
        if (measure(dots) > width) return "";
        // Prefer equal readable ends while preserving the full value in the tooltip.
        var low = 0; var high = Math.Max(0, path.Length - 1);
        while (low < high)
        {
            var count = (low + high + 1) / 2;
            var candidate = path[..((count + 1) / 2)] + dots + path[(path.Length - count / 2)..];
            if (measure(candidate) <= width) low = count; else high = count - 1;
        }
        return path[..((low + 1) / 2)] + dots + path[(path.Length - low / 2)..];
    }
    public static void PathText(TextBlock text, string path)
    {
        text.ToolTip = path;
        text.Text = Shorten(path, Math.Max(0, text.ActualWidth), value => new FormattedText(value,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(text.FontFamily, text.FontStyle,
                text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace);
    }
}
