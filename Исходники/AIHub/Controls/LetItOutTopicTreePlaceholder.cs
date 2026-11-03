using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

internal sealed class LetItOutTopicTreePlaceholder : UserControl
{
    private readonly TextBlock _heading = new() { FontSize = 18, Margin = new(2, 4, 2, 12) };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(2, 12, 2, 0) };
    internal LetItOutTopicTreePlaceholder()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAutomationId(this, "LetItOut.TopicTree.Placeholder");
        var body = new StackPanel { Margin = new(12) }; body.Children.Add(_heading);
        var canvas = new Canvas { Width = 220, Height = 220, Opacity = .5 };
        Link(110, 36, 110, 84); Link(110, 112, 52, 155); Link(110, 112, 168, 155);
        Node(35, 0, 150); Node(35, 84, 150); Node(10, 155, 84); Node(126, 155, 84);
        body.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxHeight = 240 });
        body.Children.Add(_hint); Content = body;
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        void Link(double x1, double y1, double x2, double y2)
        {
            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1.5 };
            line.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush"); canvas.Children.Add(line);
        }
        void Node(double x, double y, double width)
        {
            var node = new Border { Width = width, Height = 36, CornerRadius = new(7), BorderThickness = new(1.5) };
            node.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
            Canvas.SetLeft(node, x); Canvas.SetTop(node, y); canvas.Children.Add(node);
        }
    }
    internal void Configure(Func<string, string> localize)
    { _heading.Text = localize("LetItOut.Tree.Title"); _hint.Text = localize("LetItOut.Tree.Placeholder"); }
}
