using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls;

/// <summary>Builds small localized settings cards; existing controls can be hosted unchanged.</summary>
public sealed class SettingsCardFactory(Func<string, string> text)
{
    private readonly List<Action> _localize = [];

    public TextBlock Label(string key, bool heading = false)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        label.SetResourceReference(TextBlock.ForegroundProperty, heading ? "TextPrimaryBrush" : "TextSecondaryBrush");
        if (heading) { label.FontWeight = FontWeights.SemiBold; label.SetResourceReference(TextBlock.FontSizeProperty, "UiSectionFontSize"); }
        _localize.Add(() => label.Text = text(key)); return label;
    }

    public Border Card(params FrameworkElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        var card = new Border { Child = panel, CornerRadius = new(12), BorderThickness = new(1), Margin = new(0, 0, 0, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.SetResourceReference(Border.PaddingProperty, "UiPanelPadding"); return card;
    }

    public Button Action(string id, string key, RoutedEventHandler handler)
    {
        var button = new Button { HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 8, 0, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SettingsActionButtonStyle");
        button.Click += handler;
        AutomationProperties.SetAutomationId(button, id);
        _localize.Add(() => { button.Content = text(key); AutomationProperties.SetName(button, text(key)); }); return button;
    }

    public CheckBox FutureSwitch(string key)
    {
        var checkbox = new CheckBox { IsEnabled = false, IsChecked = false, Margin = new(0, 0, 0, 10) };
        _localize.Add(() => checkbox.Content = new TextBlock { Text = text(key), TextWrapping = TextWrapping.Wrap }); return checkbox;
    }

    public void RefreshLocalization() { foreach (var action in _localize) action(); }
}
