using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

internal static class CaptureUi
{
    internal sealed record Option(string Id, string Name) { public override string ToString() => Name; }
    public static TextBlock Text(string text, bool heading = false)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        result.SetResourceReference(TextBlock.ForegroundProperty, heading ? "TextPrimaryBrush" : "TextSecondaryBrush");
        if (heading) { result.FontSize = 24; result.FontWeight = FontWeights.SemiBold; }
        return result;
    }
    public static Button Button(string text, Action clicked)
    {
        var result = new Button { Content = text, Padding = new(14, 8, 14, 8), Margin = new(0, 0, 8, 8), MinHeight = 38 };
        result.Click += (_, _) => clicked(); return result;
    }
    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var result = new CheckBox { Content = text, IsChecked = value, Margin = new(0, 0, 16, 10) };
        result.Checked += (_, _) => changed(true); result.Unchecked += (_, _) => changed(false); return result;
    }
    public static ComboBox Choice(StackPanel panel, string title, string current, IEnumerable<Option> options, Action<string> changed)
    {
        panel.Children.Add(Text(title));
        var items = options.ToArray(); var result = new ComboBox { ItemsSource = items, Margin = new(0, 0, 0, 16), MinHeight = 36,
            SelectedItem = items.FirstOrDefault(o => o.Id == current) ?? items.First() };
        result.SelectionChanged += (_, _) => { if (result.SelectedItem is Option option) changed(option.Id); };
        panel.Children.Add(result); return result;
    }
}
