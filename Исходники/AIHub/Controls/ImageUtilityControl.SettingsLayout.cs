using System.Windows;
using System.Windows.Controls;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private readonly Dictionary<StackPanel, WrapPanel> _settingFlows = [];
    private readonly Dictionary<StackPanel, StackPanel> _lastSettingGroups = [];

    private void ResetSettingsFlow(StackPanel panel) { _settingFlows.Remove(panel); _lastSettingGroups.Remove(panel); }
    private void EndSettingsFlow(StackPanel panel) => ResetSettingsFlow(panel);
    private StackPanel SettingGroup(StackPanel panel)
    {
        if (!_settingFlows.TryGetValue(panel, out var flow))
        {
            flow = new WrapPanel { Orientation = Orientation.Horizontal };
            _settingFlows[panel] = flow; panel.Children.Add(flow);
            flow.SizeChanged += (_, _) =>
            {
                var columns = Math.Max(1, (int)(flow.ActualWidth / 200));
                // Round down and leave a pixel for layout rounding at non-integer display scales.
                var width = Math.Max(180, Math.Floor((flow.ActualWidth - 12 * columns - 1) / columns));
                foreach (var child in flow.Children.OfType<StackPanel>()) child.Width = Math.Max(child.MinWidth, width);
            };
        }
        var group = new StackPanel { Width = 240, Margin = new(0, 0, 12, 6) };
        flow.Children.Add(group); _lastSettingGroups[panel] = group; return group;
    }
    private void SettingHint(StackPanel panel, string key)
    {
        var text = ImageUtilityUi.Text(L(key)); text.FontSize = 12; text.Margin = new(0, 2, 0, 4);
        if (_lastSettingGroups.TryGetValue(panel, out var group)) group.Children.Add(text);
        else panel.Children.Add(text);
    }
}
