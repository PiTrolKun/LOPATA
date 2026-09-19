using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private FrameworkElement BuildStatus(UIElement memoryStatus)
    {
        // The main window hosts these live controls outside the route card.
        // Keep long errors and source receipts accessible without taking over the editor.
        foreach (var text in new[] { _status, _tokens, _receipts })
        {
            text.SetResourceReference(TextBlock.FontSizeProperty, "UiSmallFontSize");
            text.Margin = new Thickness(0, 2, 0, 2);
            var style = new Style(typeof(TextBlock));
            var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
            empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(empty); text.Style = style;
        }
        var current = new DockPanel();
        _tokens.Margin = new Thickness(16, 2, 0, 2);
        DockPanel.SetDock(_tokens, Dock.Right); current.Children.Add(_tokens);
        current.Children.Add(_status);
        var panel = new StackPanel();
        panel.Children.Add(current); panel.Children.Add(memoryStatus); panel.Children.Add(_receipts);
        return new ScrollViewer
        {
            Content = panel, MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }
}
