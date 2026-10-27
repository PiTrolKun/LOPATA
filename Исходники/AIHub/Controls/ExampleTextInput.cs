using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>An example is presentation only: it never becomes input text, even on focus.</summary>
public sealed class ExampleTextInput : Grid
{
    public TextBox Input { get; } = new() { Padding = new Thickness(9), MinHeight = 36 };
    private readonly TextBlock _example = new()
    {
        Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, Opacity = .55, TextTrimming = TextTrimming.CharacterEllipsis
    };
    public bool ExampleVisible => _example.Visibility == Visibility.Visible;
    public ExampleTextInput(string label, string example, string id, bool multiline = false)
    {
        Input.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        Input.SetResourceReference(TextBox.BackgroundProperty, "PanelBrush");
        Input.SetResourceReference(TextBox.BorderBrushProperty, "LineBrush");
        _example.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _example.Text = example;
        if (multiline)
        {
            Input.AcceptsReturn = true;
            Input.TextWrapping = TextWrapping.Wrap;
            Input.Height = 280;
            Input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Input.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _example.VerticalAlignment = VerticalAlignment.Top;
            _example.Margin = new Thickness(11, 10, 11, 10);
            _example.TextWrapping = TextWrapping.Wrap;
            _example.TextTrimming = TextTrimming.None;
        }
        AutomationProperties.SetAutomationId(Input, id);
        AutomationProperties.SetName(Input, label);
        AutomationProperties.SetHelpText(Input, example);
        Children.Add(Input); Children.Add(_example);
        Input.TextChanged += (_, _) => _example.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
