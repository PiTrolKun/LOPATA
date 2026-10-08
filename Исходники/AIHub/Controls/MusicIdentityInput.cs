using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>A visual placeholder never becomes input or exported metadata.</summary>
public sealed class MusicIdentityInput : Grid
{
    public TextBox Input { get; } = new() { MinWidth = 0, Padding = new(5), MaxLength = 160 };
    private readonly TextBlock _placeholder = new() { IsHitTestVisible = false, Margin = new(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly bool _subtle;
    public MusicIdentityInput(string id, bool subtle = false)
    {
        _subtle = subtle; if (subtle) Input.MaxLength = 4096;
        Children.Add(Input); Children.Add(_placeholder); AutomationProperties.SetAutomationId(Input, id);
        Input.TextChanged += (_, _) => Refresh(); Input.GotKeyboardFocus += (_, _) => Refresh(); Input.LostKeyboardFocus += (_, _) => Refresh();
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); Refresh();
    }
    public void Localize(string placeholder)
    { _placeholder.Text = placeholder; Input.ToolTip = placeholder; AutomationProperties.SetName(Input, placeholder); }
    private void Refresh()
    {
        _placeholder.Visibility = Input.Text.Length == 0 && !Input.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        if (!_subtle) return;
        _placeholder.Opacity = .4;
        Input.SetResourceReference(TextBox.BackgroundProperty, Input.IsKeyboardFocusWithin ? "InputBackgroundBrush" : "WindowBackgroundBrush");
        Input.SetResourceReference(TextBox.ForegroundProperty, Input.IsKeyboardFocusWithin ? "TextPrimaryBrush" : "TextSecondaryBrush");
        Input.BorderThickness = new(Input.IsKeyboardFocusWithin ? 1 : 0); Input.Opacity = Input.IsKeyboardFocusWithin ? 1 : .65;
    }
}
