using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicModelSelectionControl : UserControl
{
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);
    private Func<string, string> _l = key => key;
    public event Func<string, string, Task>? OpenRequested;

    public MusicModelSelectionControl() => AutomationProperties.SetAutomationId(this, "Music.Models");

    public void Localize(Func<string, string> localize)
    {
        _l = localize;
        var panel = new StackPanel { Margin = new(24), MaxWidth = 1560, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Text(_l("Music.Models.Title"), true));
        panel.Children.Add(Text(_l("Music.Models.Hint")));
        foreach (var model in MusicModelSelectionCatalog.All) panel.Children.Add(Card(model));
        Content = new ScrollViewer { Content = panel, Margin = new(0, 0, 0, 6), ClipToBounds = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private Grid Card(MusicModelCandidate model)
    {
        var row = new Grid { Margin = new(0, 8, 0, 12) };
        row.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var body = new StackPanel();
        var selector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 20, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(selector, "Music.Models.Variants." + model.Id);
        AutomationProperties.SetName(selector, string.Format(_l("Music.Models.Variations"), model.Name));
        selector.ToolTip = _l("Music.Models.VariationHint");
        foreach (var variant in model.Variants)
            selector.Items.Add(new ComboBoxItem { Tag = variant, Content = model.Name + " · " + _l("Music.Models.Variant." + variant.LabelKey) });
        var chosen = _choices.GetValueOrDefault(model.Id, model.Variants[0].Id);
        selector.SelectedIndex = Math.Max(0, model.Variants.ToList().FindIndex(v => v.Id == chosen));
        body.Children.Add(selector);
        body.Children.Add(Text(_l(model.DescriptionKey)));
        var status = Text(""); status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); body.Children.Add(status);
        var open = new Button { Padding = new(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(open, "Music.Models.Open." + model.Id);
        body.Children.Add(open);
        void Update()
        {
            var variant = (MusicModelVariant)((ComboBoxItem)selector.SelectedItem).Tag;
            _choices[model.Id] = variant.Id;
            status.Text = _l(variant.Connected ? "Music.Models.Connected" : "Music.Models.Planned");
            open.Content = _l(variant.Connected ? "Music.Models.Open" : "Music.Models.NotConnected");
            open.IsEnabled = variant.Connected;
            AutomationProperties.SetName(open, model.Name + " · " + open.Content);
        }
        selector.SelectionChanged += (_, _) => Update(); Update();
        open.Click += async (_, _) =>
        {
            var variant = (MusicModelVariant)((ComboBoxItem)selector.SelectedItem).Tag;
            if (MusicModelSelectionCatalog.CanOpen(model.Id, variant.Id) && OpenRequested is { } action)
                await action(model.Id, variant.Id);
        };
        row.Children.Add(Frame(body, new(0, 0, 12, 0)));
        AddExample("LocalExample", 1); AddExample("CloudExample", 2);
        return row;

        void AddExample(string key, int column)
        {
            var example = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            example.Children.Add(Text(_l("Music.Models." + key), true));
            example.Children.Add(Text("♫"));
            example.Children.Add(Text(_l("Music.Models.ExamplePending")));
            var border = Frame(example, new(column == 1 ? 0 : 6, 0, 0, 0));
            AutomationProperties.SetAutomationId(border, "Music.Models." + key + "." + model.Id);
            Grid.SetColumn(border, column); row.Children.Add(border);
        }
    }

    private static TextBlock Text(string value, bool title = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = title ? 21 : 15,
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 4, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    private static Border Frame(UIElement content, Thickness margin)
    {
        var border = new Border { Child = content, Padding = new(18), Margin = margin,
            CornerRadius = new(10), BorderThickness = new(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
}
