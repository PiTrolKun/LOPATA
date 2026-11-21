using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.ImageLayers;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using ListBox = System.Windows.Controls.ListBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls.ImageLayers;

internal static partial class ImageLayersToolDialogs
{
    internal static Window LassoOptions(Window owner, Func<string, string> t, string? selectedLayer, bool canIntersect,
        LayerLassoOptions preferred, out Func<LayerLassoOptions> value)
    {
        var dialog = Create(owner, t("LassoOptions"), 550, 335);
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SizeToContent = SizeToContent.Height;
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(Help(t("LassoDestination")));
        var newLayer = Choice("LassoNewLayer", t("LassoNewLayer"), true);
        var selected = Choice("LassoSelectedLayer", selectedLayer is null ? t("LassoSelectedUnavailable")
            : string.Format(t("LassoSelectedLayer"), selectedLayer), selectedLayer is not null);
        var intersect = Choice("LassoIntersectingLayer", t("LassoIntersectingLayer"), canIntersect);
        var initial = preferred.Destination switch
        {
            LayerLassoDestination.SelectedLayer when selected.IsEnabled => selected,
            LayerLassoDestination.IntersectingLayer when intersect.IsEnabled => intersect,
            _ => newLayer
        };
        initial.IsChecked = true;
        var magnetic = new CheckBox { Content = t("LassoMagnetic"), IsChecked = preferred.Magnetic, Margin = new(0, 10, 0, 6) };
        AutomationProperties.SetAutomationId(magnetic, "ImageLayers.LassoMagneticOption");
        panel.Children.Add(magnetic);
        panel.Children.Add(Help(t("LassoMagneticHelp")));
        panel.Children.Add(Actions(Accept(dialog, t, "LassoStart"), Cancel(dialog, t, "LassoCancel")));
        dialog.Content = panel;
        dialog.Loaded += (_, _) => initial.Focus();
        value = () => new(
            selected.IsChecked == true ? LayerLassoDestination.SelectedLayer
                : intersect.IsChecked == true ? LayerLassoDestination.IntersectingLayer : LayerLassoDestination.NewLayer,
            magnetic.IsChecked == true);
        return dialog;

        RadioButton Choice(string key, string caption, bool enabled)
        {
            var radio = new RadioButton
            {
                Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap },
                GroupName = "LassoDestination", IsEnabled = enabled, Margin = new(0, 0, 0, 8)
            };
            AutomationProperties.SetAutomationId(radio, "ImageLayers." + key);
            panel.Children.Add(radio);
            return radio;
        }
    }

    internal static Window LassoTarget(Window owner, Func<string, string> t, LayerLassoHit[] choices, out Func<Guid> value)
    {
        var dialog = Create(owner, t("LassoChooseTarget"), 550, 365);
        var panel = new DockPanel { Margin = new(20) };
        var question = Help(t("LassoChooseTargetHelp"));
        DockPanel.SetDock(question, Dock.Top);
        panel.Children.Add(question);
        var list = new ListBox();
        list.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        list.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(list, "ImageLayers.LassoTargets");
        AutomationProperties.SetName(list, t("LassoChooseTarget"));
        foreach (var hit in choices)
            list.Items.Add(new ListBoxItem { Content = string.Format(t("LassoTargetPixels"), hit.Name, hit.Pixels) });
        list.SelectedIndex = 0;
        var actions = Actions(Accept(dialog, t, "LassoMergeAction"), Cancel(dialog, t, "LassoCancel"));
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(list);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => list.Focus();
        value = () => choices[list.SelectedIndex].Id;
        return dialog;
    }
}
