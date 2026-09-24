using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

internal static class LiteraryImportIcons
{
    public static Button Create(string glyph, string label, Action action)
    {
        var button = LiteraryUi.Button(glyph, action);
        button.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        button.FontSize = 16;
        button.Width = button.MinWidth = 34;
        button.Height = button.MinHeight = 34;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(8, 0, 0, 0);
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
        return button;
    }
}
