using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

internal static class RagBookUiLocalization
{
    // Update captions in place; rebuilding a page would detach its active acquisition view.
    internal static void Refresh(DependencyObject root, Dictionary<string, string> labels, Func<string, string> l)
    {
        var changes = labels.Select(p => (p.Key, Old: p.Value, New: l("RagBooks." + p.Key))).ToArray();
        string Translate(string value) => changes.FirstOrDefault(p => p.Old == value) is { New: not null } match ? match.New : value;
        foreach (var element in Descendants(root))
        {
            if (element is FrameworkElement framework && framework.ToolTip is string tooltip) framework.ToolTip = Translate(tooltip);
            if (element is TextBlock text) text.Text = Translate(text.Text);
            if (element is TabItem tab && tab.Header is string header) tab.Header = Translate(header);
            if (element is Button button)
            {
                if (button.Content is string content) button.Content = Translate(content);
                var name = AutomationProperties.GetName(button);
                if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(button, Translate(name));
            }
        }
        foreach (var change in changes) labels[change.Key] = change.New;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
