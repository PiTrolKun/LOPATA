using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class ModelExpertWindow
{
    private void RenderStudioSolver()
    {
        _parameters.Children.Add(MusicWishUi.Text(L("StudioSolver"), true));
        _parameters.Children.Add(MusicWishUi.Text(L("StudioSolverDescription")));
        var selector = new ComboBox
        {
            MinWidth = 240, MaxWidth = 440, Margin = new(0, 4, 0, 12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        selector.Items.Add(new ComboBoxItem { Content = L("Choice.Midpoint"), Tag = "midpoint" });
        selector.Items.Add(new ComboBoxItem
        {
            Content = L("Choice.Ab2"), Tag = "ab2", IsEnabled = MusicStudioRuntime.SelectedProfile.SupportsAb2,
            ToolTip = MusicStudioRuntime.SelectedProfile.SupportsAb2 ? L("StudioSolverDescription") : L("UnsupportedSolver")
        });
        selector.SelectedIndex = _draft.StudioSolver == "ab2" ? 1 : 0;
        AutomationProperties.SetAutomationId(selector, "Music.Expert.StudioSolver");
        AutomationProperties.SetName(selector, L("StudioSolver"));
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not ComboBoxItem { Tag: string solver }) return;
            _draft = _draft with { StudioSolver = solver };
            Changed();
        };
        _parameters.Children.Add(selector);
    }
}
