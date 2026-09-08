using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

// Search changes the viewport, never the selected set.
public sealed class LiteraryChoiceList : UserControl
{
    private readonly List<(string Id, string Label, CheckBox Box, ListBoxItem Item)> _items = [];
    public IReadOnlyList<string> SelectedIds => _items.Where(i => i.Box.IsChecked == true).Select(i => i.Id).ToArray();
    public LiteraryChoiceList(IEnumerable<(string Id, string Label)> choices, string? searchHint = null)
    {
        var panel = new DockPanel();
        var list = new System.Windows.Controls.ListBox { Height = searchHint is null ? 160 : 210, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        list.SetResourceReference(BackgroundProperty, "PanelBrush");
        list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        list.SetResourceReference(BorderBrushProperty, "LineBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        foreach (var (id, label) in choices)
        {
            var check = new CheckBox { Content = label, Padding = new Thickness(5), VerticalContentAlignment = VerticalAlignment.Center };
            check.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            var item = new ListBoxItem { Content = check };
            _items.Add((id, label, check, item));
            list.Items.Add(item);
        }
        if (searchHint is not null)
        {
            var searchPanel = new StackPanel();
            searchPanel.Children.Add(LiteraryUi.Text(searchHint));
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(7), MaxLength = 80 };
            searchPanel.Children.Add(search);
            DockPanel.SetDock(searchPanel, Dock.Top);
            panel.Children.Add(searchPanel);
            search.TextChanged += (_, _) =>
            {
                var query = search.Text.Trim();
                if (query.Length == 0) return;
                var match = _items.FirstOrDefault(i => i.Label.StartsWith(query, StringComparison.CurrentCultureIgnoreCase));
                if (match.Item is null) match = _items.FirstOrDefault(i => i.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase));
                if (match.Item is not null) { list.SelectedItem = match.Item; list.ScrollIntoView(match.Item); }
            };
        }
        panel.Children.Add(list);
        Content = panel;
    }
}

public static class LiteraryChoices
{
    public static readonly string[] Forms = ["story", "novella", "novel", "script", "free"];
    public static readonly string[] Genres = ["fantasy", "scifi", "adventure", "mystery", "thriller", "horror", "romance", "drama", "comedy", "historical", "fairytale", "sliceoflife"];
    public static IEnumerable<string> CountryCodes => CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => new RegionInfo(c.Name).TwoLetterISORegionName).Where(c => c.Length == 2).Distinct();
}
