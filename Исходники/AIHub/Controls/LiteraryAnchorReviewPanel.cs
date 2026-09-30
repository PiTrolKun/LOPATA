using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal sealed class LiteraryAnchorReviewPanel : StackPanel
{
    public LiteraryAnchorReviewPanel(Func<string, string> localize, TextBox positive, TextBox negative, Action compress)
    {
        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var total = LiteraryUi.Text("");
        summary.Children.Add(total);
        summary.Children.Add(LiteraryUi.Button(localize("Literary.Anchor.Compress"), compress));
        var positiveCount = LiteraryUi.Text("");
        var negativeCount = LiteraryUi.Text("");
        Children.Add(summary);
        Children.Add(LiteraryUi.Text(localize("Literary.Anchor.Positive")));
        Children.Add(positive); Children.Add(positiveCount);
        Children.Add(LiteraryUi.Text(localize("Literary.Anchor.Negative")));
        Children.Add(negative); Children.Add(negativeCount);
        void Refresh()
        {
            var count = LiteraryAnchorCompression.Count(positive.Text, negative.Text);
            var visibility = count > LiteraryPlotAnchorStore.MaxCharacters ? Visibility.Visible : Visibility.Collapsed;
            summary.Visibility = positiveCount.Visibility = negativeCount.Visibility = visibility;
            total.Text = string.Format(localize("Literary.Anchor.ReviewOverflow"), count, LiteraryPlotAnchorStore.MaxCharacters,
                Math.Max(0, count - LiteraryPlotAnchorStore.MaxCharacters));
            positiveCount.Text = string.Format(localize("Literary.Anchor.FieldCount"), positive.Text.Length);
            negativeCount.Text = string.Format(localize("Literary.Anchor.FieldCount"), negative.Text.Length);
        }
        positive.TextChanged += (_, _) => Refresh();
        negative.TextChanged += (_, _) => Refresh();
        Refresh();
    }
}
