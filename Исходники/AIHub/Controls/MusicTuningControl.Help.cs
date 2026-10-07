using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class MusicTuningControl
{
    private static Grid HelpHeading(TextBlock heading, Button help)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(heading); Grid.SetColumn(help, 1); row.Children.Add(help); return row;
    }
    private static Button Help(string id, Func<string> content)
    {
        var button = new Button { Content = "?", Width = 24, Height = 24, MinWidth = 0, MinHeight = 0,
            Padding = new(0), Margin = new(4, 0, 0, 2) };
        AutomationProperties.SetAutomationId(button, "Music.Tuning." + id);
        button.Click += (_, _) => {
            var text = Text(); text.Text = content(); text.MaxWidth = 350;
            var border = new Border { Child = text, Padding = new(12), BorderThickness = new(1), CornerRadius = new(8) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, Child = border,
                AllowsTransparency = true, StaysOpen = false };
            popup.IsOpen = true;
        };
        return button;
    }
}
