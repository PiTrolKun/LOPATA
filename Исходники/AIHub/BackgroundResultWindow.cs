using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace AIHub;

/// <summary>Completion receipts can be read without restoring an old mutable session.</summary>
public sealed class BackgroundResultWindow : Window
{
    public BackgroundResultWindow(Window owner, string title, string text, string hint)
    {
        Owner = owner; Resources = owner.Resources; Title = title;
        Width = 840; Height = 640; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var note = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        content.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        panel.Children.Add(content); Content = panel;
    }
}
