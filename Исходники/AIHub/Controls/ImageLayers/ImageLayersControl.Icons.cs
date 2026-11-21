using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Button ToolCommand(string key, Action action, string? help = null, bool primary = false)
    {
        var button = Command(key, action, primary);
        var glyph = new Path
        {
            Data = Geometry.Parse(ToolGlyph(key)), Width = 18, Height = 18, Stretch = Stretch.Uniform,
            StrokeThickness = 1.65, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(1), IsHitTestVisible = false
        };
        glyph.SetBinding(Path.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        button.Content = glyph;
        button.MinWidth = 38;
        button.Padding = new(6, 4, 6, 4);
        var name = T(key).TrimEnd(' ', '▾');
        var description = help is null ? name : T(help);
        button.ToolTip = help is null ? name : name + " · " + description;
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, description);
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    private static string ToolGlyph(string key) => key switch
    {
        "Analyze" => "M2,6 V2 H6 M14,2 H18 V6 M18,14 V18 H14 M6,18 H2 V14 M3,10 H17",
        "Refine" => "M3,17 L13,7 L16,10 L6,20 Z M10,10 L13,13 M16,2 V6 M14,4 H18 M5,4 V8 M3,6 H7",
        "AdditionalTools" => "M2,4 H18 M2,10 H18 M2,16 H18 M6,2 V6 M14,8 V12 M9,14 V18",
        "Cancel" => "M5,5 H15 V15 H5 Z M2,2 H18 V18 H2 Z",
        "Hand" => "M7,11 V4 C7,2 10,2 10,4 V10 M10,8 V2 C10,0 13,0 13,2 V10 M13,8 V4 C13,2 16,2 16,4 V11 M16,8 C16,6 19,6 19,8 V14 C19,18 16,20 12,20 H10 C7,20 5,18 3,14 L1,11 C0,9 3,8 4,9 L7,12",
        "RegionTool" => "M2,6 V2 H6 M8.5,2 H11.5 M14,2 H18 V6 M18,8.5 V11.5 M18,14 V18 H14 M11.5,18 H8.5 M6,18 H2 V14 M2,11.5 V8.5",
        "Lasso" => "M6,15 C0,14 0,8 5,5 C9,1 17,2 19,7 C21,12 15,16 8,15 C4,15 5,11 8,12 C11,13 8,19 4,18",
        "Points" => "M4,15 L7,5 L16,7 L16,15 L4,15 M2,13 H6 V17 H2 Z M5,3 H9 V7 H5 Z M14,5 H18 V9 H14 Z M14,13 H18 V17 H14 Z",
        "Pencil" => "M2,18 L4,12 L14,2 L18,6 L8,16 Z M12,4 L16,8",
        "EdgeDetails" => "M2,18 C8,17 6,10 12,9 C16,8 16,4 18,2 M5,7 V3 M3,5 H7 M14,15 V11 M12,13 H16",
        "ReviewScene" => "M2,9 C6,3 14,3 18,9 C14,15 6,15 2,9 Z M13,9 A3,3 0 1 0 7,9 A3,3 0 1 0 13,9 M11,17 L14,20 L19,15",
        "CompleteFrame" => "M2,3 H18 V17 H2 Z M2,12 L7,7 L11,12 L14,9 L18,13 M9,17 H15",
        "SendToEditor" => "M9,4 H3 V18 H17 V12 M9,11 L18,2 M12,2 H18 V8",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
}
