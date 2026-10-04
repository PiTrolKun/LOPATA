using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private Button OutputSizeButton(bool enabled)
    {
        var selected = ImageOutputDimensions.Normalize(_settings.OutputLongestSide);
        var button = Icon("OutputSize", "▦⤢", () => Task.CompletedTask, "OutputSize", enabled);
        string Label(int value) => value == 0 ? L("OutputOriginal") : ImageOutputDimensions.Label(value);
        var size = ImageOutputDimensions.Fit(_width, _height, selected);
        button.ToolTip = L("OutputSize") + " · " + Label(selected) + " · " + size.Width + "×" + size.Height + "\n" + L("OutputSizeHint");
        var menu = new ContextMenu();
        foreach (var value in ImageOutputDimensions.Presets)
        {
            var dims = ImageOutputDimensions.Fit(_width, _height, value);
            var option = new MenuItem { Header = Label(value) + " · " + dims.Width + "×" + dims.Height,
                IsCheckable = true, IsChecked = value == selected };
            AutomationProperties.SetAutomationId(option, "Generation.OutputPreset." + value);
            option.Click += (_, _) => { _settings.OutputLongestSide = value; _saveSettings?.Invoke(); Render(); };
            menu.Items.Add(option);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => ShowMenu(button, menu);
        return button;
    }
}
