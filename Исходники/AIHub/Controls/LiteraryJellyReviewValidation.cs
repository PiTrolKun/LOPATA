using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;

/// <summary>Highlights the invalid field without changing its text or source evidence.</summary>
internal sealed class LiteraryJellyReviewValidation(FrameworkElement owner, Func<string, string> localize)
{
    private readonly Dictionary<LiteraryJellyField, (Control Input, TextBlock Hint)> _fields = [];

    public void Add(StackPanel card, LiteraryJellyField field, Control input)
    {
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8) };
        card.Children.Add(hint);
        _fields.Add(field, (input, hint));
    }

    public Control? Show(IReadOnlyList<LiteraryJellyIssue> issues)
    {
        var background = owner.TryFindResource("PanelBrush") as SolidColorBrush;
        var dark = background is not null &&
            (0.2126 * background.Color.R + 0.7152 * background.Color.G + 0.0722 * background.Color.B) < 128;
        var brush = new SolidColorBrush(dark ? Color.FromRgb(248, 113, 113) : Color.FromRgb(185, 28, 28));
        brush.Freeze();
        foreach (var (field, (input, hint)) in _fields)
        {
            var issue = issues.FirstOrDefault(x => x.Field == field);
            if (issue is not null)
            {
                input.Foreground = brush;
                input.BorderBrush = brush;
                hint.Foreground = brush;
                hint.Text = localize(issue.MessageKey);
                hint.Visibility = Visibility.Visible;
            }
            else if (hint.Visibility == Visibility.Visible)
            {
                // Text inputs use local resource references; the selector uses its control theme.
                if (input is TextBox)
                {
                    input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                    input.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
                }
                else
                {
                    input.ClearValue(Control.ForegroundProperty);
                    input.ClearValue(Control.BorderBrushProperty);
                }
                hint.Text = "";
                hint.Visibility = Visibility.Collapsed;
            }
        }
        return issues.Count == 0 ? null : _fields[issues[0].Field].Input;
    }

    public void Reveal(Control input)
    {
        var hint = _fields.Values.FirstOrDefault(pair => pair.Input == input).Hint;
        if (hint is not null && hint.Visibility == Visibility.Visible) hint.BringIntoView();
    }
}
