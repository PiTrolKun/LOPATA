using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.VideoEditor;

internal sealed class VideoVolumePopup : Popup
{
    internal Slider Level { get; } = new() { Minimum = 0, Maximum = 200, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10, MinWidth = 270, Margin = new(0, 10, 0, 5) };
    internal TextBox PercentInput { get; } = new() { Width = 65, MinHeight = 29, VerticalContentAlignment = VerticalAlignment.Center };
    internal Button Reset { get; } = new() { Margin = new(12, 0, 0, 0), Padding = new(8, 4, 8, 4) };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new(0, 6, 0, 0) };
    private readonly Action<double> _change;
    private readonly string _range;
    private bool _sync;
    internal VideoVolumePopup(ResourceDictionary resources, Func<string, string> text, string title, double currentPercent, bool enabled, string hint, Action<double> change)
    {
        _change = change; _range = text("VolumeRange"); AllowsTransparency = true; StaysOpen = false; Placement = PlacementMode.RelativePoint;
        var border = new Border { Padding = new(14), CornerRadius = new(7), BorderThickness = new(1), MaxWidth = 400 };
        border.Resources.MergedDictionaries.Add(resources); border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var panel = new StackPanel(); border.Child = panel; Child = border;
        TextBlock Label(string value) { var label = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return label; }
        var caption = Label(title); caption.FontWeight = FontWeights.SemiBold; panel.Children.Add(caption);
        var controls = new StackPanel { IsEnabled = enabled }; panel.Children.Add(controls); controls.Children.Add(Level);
        var ticks = new Grid(); for (var i = 0; i < 3; i++) ticks.ColumnDefinitions.Add(new());
        for (var i = 0; i < 3; i++) { var label = Label((i * 100) + "%"); label.HorizontalAlignment = i == 0 ? System.Windows.HorizontalAlignment.Left : i == 2 ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center; Grid.SetColumn(label, i); ticks.Children.Add(label); } controls.Children.Add(ticks);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) }; controls.Children.Add(row); row.Children.Add(PercentInput); row.Children.Add(Label(" %")); Reset.Content = text("VolumeReset"); row.Children.Add(Reset);
        panel.Children.Add(_error); var help = Label(hint); help.Margin = new(0, 10, 0, 0); help.MaxWidth = 300; panel.Children.Add(help);
        _sync = true; Level.Value = Math.Clamp(currentPercent, 0, 200); PercentInput.Text = currentPercent.ToString("0.#", CultureInfo.CurrentCulture); _sync = false;
        Level.ValueChanged += (_, _) => { if (_sync) return; PercentInput.Text = Level.Value.ToString("0.#", CultureInfo.CurrentCulture); _error.Visibility = Visibility.Collapsed; _change(Level.Value); };
        PercentInput.LostKeyboardFocus += (_, _) => TryApplyText();
        PercentInput.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { TryApplyText(); e.Handled = true; } };
        panel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { IsOpen = false; e.Handled = true; } };
        Reset.Click += (_, _) => { Level.Value = 100; PercentInput.Text = "100"; _error.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => { if (enabled) TryApplyText(); };
        Opened += (_, _) => { if (enabled) Level.Focus(); };
        System.Windows.Automation.AutomationProperties.SetAutomationId(border, "VideoEditor.VolumePopup");
        System.Windows.Automation.AutomationProperties.SetAutomationId(Level, "VideoEditor.VolumeSlider");
        System.Windows.Automation.AutomationProperties.SetAutomationId(PercentInput, "VideoEditor.VolumePercent");
        System.Windows.Automation.AutomationProperties.SetAutomationId(Reset, "VideoEditor.VolumeReset");
        Level.ToolTip = PercentInput.ToolTip = text("Tip.VolumeLevel"); Reset.ToolTip = text("Tip.VolumeReset");
        System.Windows.Automation.AutomationProperties.SetName(Level, text("Tip.VolumeLevel")); System.Windows.Automation.AutomationProperties.SetName(PercentInput, text("Tip.VolumeLevel")); System.Windows.Automation.AutomationProperties.SetName(Reset, text("VolumeReset"));
    }
    internal bool TryApplyText()
    {
        if (!double.TryParse(PercentInput.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value is < 0 or > 200) { _error.Text = _range; _error.Visibility = Visibility.Visible; return false; }
        Level.Value = value; _error.Visibility = Visibility.Collapsed; return true;
    }
}
