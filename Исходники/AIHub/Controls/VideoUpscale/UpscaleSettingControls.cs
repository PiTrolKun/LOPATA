using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls.VideoUpscale;

internal sealed record UpscaleSettingOption(string Value, string Label, string Help = "", double FrameRatio = 0);

/// <summary>Visible exclusive switches and bounded numeric controls; no processing is started here.</summary>
internal static class UpscaleSettingControls
{
    internal static Panel Choices(string id, IEnumerable<UpscaleSettingOption> options, string current, Action<string> selected, bool tiles = false)
    {
        Panel panel = tiles ? new WrapPanel() : new StackPanel();
        foreach (var option in options) {
            var toggle = MusicWishUi.Check("", option.Value == current, _ => { });
            toggle.VerticalAlignment = VerticalAlignment.Center; toggle.Margin = new(0, 2, 8, 2);
            AutomationProperties.SetName(toggle, option.Label);
            AutomationProperties.SetAutomationId(toggle, id + "." + option.Value);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
            if (option.Help.Length > 0) { var help = new TextBlock { Text = option.Help, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 0) }; help.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); text.Children.Add(help); }
            var row = new DockPanel(); DockPanel.SetDock(toggle, Dock.Left); row.Children.Add(toggle);
            if (option.FrameRatio > 0) {
                var shape = new Border { Width = option.FrameRatio >= 1 ? 38 : 38 * option.FrameRatio, Height = option.FrameRatio >= 1 ? 38 / option.FrameRatio : 38, BorderThickness = new(2), CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                shape.SetResourceReference(Border.BorderBrushProperty, "TextPrimaryBrush"); shape.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                var preview = new Grid { Width = 46, Height = 42, Margin = new(0, 0, 8, 0), Children = { shape } }; DockPanel.SetDock(preview, Dock.Left); row.Children.Add(preview);
            }
            row.Children.Add(text);
            var frame = new Border { Child = row, Padding = new(8), Margin = new(0, 0, 6, 6), BorderThickness = new(1), CornerRadius = new(5) };
            if (tiles) frame.Width = 230;
            frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, option.Value == current ? "AccentBrush" : "LineBrush");
            frame.Tag = option.Value; panel.Children.Add(frame);
            toggle.Click += (_, _) => {
                Select(panel, option.Value);
                selected(option.Value);
            };
            frame.MouseLeftButtonUp += (_, e) => {
                if (e.OriginalSource is DependencyObject target && IsInsideToggle(target, toggle)) return;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); e.Handled = true;
            };
        }
        return panel;
    }
    internal static void Select(Panel panel, string value)
    {
        foreach (var frame in panel.Children.OfType<Border>()) {
            var active = frame.Tag as string == value;
            if (frame.Child is DockPanel row) row.Children.OfType<CheckBox>().First().IsChecked = active;
            frame.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "LineBrush");
        }
    }
    private static bool IsInsideToggle(DependencyObject element, CheckBox toggle)
    {
        for (var node = element; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node)) if (node == toggle) return true;
        return false;
    }
    internal static FrameworkElement Range(string id, string name, double value, double minimum, double maximum, double increment, Action<double> changed, bool percent = false)
    {
        var panel = new StackPanel { Margin = new(0, 7, 0, 5) }; panel.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new(90) }); panel.Children.Add(row);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = increment, SmallChange = increment, LargeChange = increment * 10, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) };
        var box = new TextBox { Text = Format(value), VerticalContentAlignment = VerticalAlignment.Center, Margin = new(2) }; Grid.SetColumn(box, 1); row.Children.Add(slider); row.Children.Add(box);
        AutomationProperties.SetName(slider, name); AutomationProperties.SetAutomationId(slider, id + ".Slider");
        AutomationProperties.SetName(box, name); AutomationProperties.SetAutomationId(box, id + ".Value");
        var changing = false;
        slider.ValueChanged += (_, _) => {
            if (changing) return;
            var number = Math.Clamp(Math.Round((slider.Value - minimum) / increment) * increment + minimum, minimum, maximum);
            changing = true; slider.Value = number; box.Text = Format(number); box.ClearValue(Control.BorderBrushProperty); changing = false; changed(number);
        };
        box.LostKeyboardFocus += (_, _) => {
            if (changing) return;
            var input = box.Text.Trim().TrimEnd('%').Trim();
            if ((!double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) && !double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) || !double.IsFinite(number)) { box.BorderBrush = Brushes.OrangeRed; return; }
            if (percent) number /= 100;
            var rounded = Math.Round((number - minimum) / increment) * increment + minimum;
            if (number < minimum || number > maximum || Math.Abs(rounded - number) > .000001) { box.BorderBrush = Brushes.OrangeRed; return; }
            changing = true; slider.Value = number; box.Text = Format(number); box.ClearValue(Control.BorderBrushProperty); changing = false; changed(number);
        };
        return panel;
        string Format(double number) => percent ? (number * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%" : number.ToString("0.###", CultureInfo.CurrentCulture);
    }
}
