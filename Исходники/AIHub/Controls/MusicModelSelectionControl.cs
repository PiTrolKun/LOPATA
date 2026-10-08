using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicModelSelectionControl : UserControl, IDisposable
{
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);
    private Func<string, string> _l = key => key;
    private readonly List<MusicExamplePlayerControl> _players = [];
    public event Func<string, string, Task>? OpenRequested;
    public event Func<MusicProjectSnapshot, Task>? ExampleRequested;
    public Func<bool> CanApplyExample { get; set; } = () => true;

    public MusicModelSelectionControl() => AutomationProperties.SetAutomationId(this, "Music.Models");

    public void Localize(Func<string, string> localize)
    {
        DisposePlayers();
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
        var description = Text(_l(model.DescriptionKey)); body.Children.Add(description);
        var assessment = new StackPanel();
        if (model.Id == "yue2")
        {
            assessment.Children.Add(Text(_l("Music.Models.yue2.q8.Pros")));
            assessment.Children.Add(Text(_l("Music.Models.yue2.q8.Cons")));
            assessment.Children.Add(Text(_l("Music.Models.yue2.q8.Memory")));
        }
        body.Children.Add(assessment);
        var status = Text(""); status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); body.Children.Add(status);
        var open = new Button { Padding = new(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(open, "Music.Models.Open." + model.Id);
        body.Children.Add(open);
        var examples = new List<Border>();
        void Update()
        {
            var variant = (MusicModelVariant)((ComboBoxItem)selector.SelectedItem).Tag;
            _choices[model.Id] = variant.Id;
            assessment.Visibility = model.Id == "yue2" && variant.Id is "q8" or "bf16"
                ? Visibility.Visible : Visibility.Collapsed;
            if (model.Id == "yue2") {
                var prefix = "Music.Models.yue2." + variant.Id + ".";
                assessment.Children.Clear();
                foreach (var key in new[] { "Pros", "Cons", "Memory" }) assessment.Children.Add(Text(_l(prefix + key)));
                description.Text = _l(variant.Id == "bf16" ? prefix + "Description" : model.DescriptionKey);
            }
            status.Text = _l(variant.Connected ? "Music.Models.Connected" : "Music.Models.Planned");
            open.Content = _l(variant.Connected ? "Music.Models.Open" : "Music.Models.NotConnected");
            open.IsEnabled = variant.Connected;
            AutomationProperties.SetName(open, model.Name + " · " + open.Content);
            if (examples.Count > 0) UpdateExamples(variant);
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
            examples.Add(border);
            AutomationProperties.SetAutomationId(border, "Music.Models." + key + "." + model.Id);
            Grid.SetColumn(border, column); row.Children.Add(border);
            if (examples.Count == 2) UpdateExamples((MusicModelVariant)((ComboBoxItem)selector.SelectedItem).Tag);
        }
        void UpdateExamples(MusicModelVariant variant)
        {
            foreach (var border in examples) {
                if (border.Child is MusicExamplePlayerControl old) { old.Dispose(); _players.Remove(old); }
                if (model.Id == "yue2" && variant.Id is "q8" or "bf16") {
                    var cloud = Grid.GetColumn(border) == 2;
                    var entry = MusicExamples.All.Single(e => cloud ? e.Cloud : !e.Cloud && e.Variation == MusicModelVariants.Normalize(variant.Id));
                    var player = new MusicExamplePlayerControl(entry, _l, () => CanApplyExample());
                    player.Playing += () => { foreach (var other in _players.Where(p => p != player)) other.Pause(); };
                    player.ApplyRequested += snapshot => ExampleRequested?.Invoke(snapshot) ?? Task.CompletedTask;
                    _players.Add(player); border.Child = player;
                }
                else {
                    var pending = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    pending.Children.Add(Text(_l(Grid.GetColumn(border) == 1 ? "Music.Models.LocalExample" : "Music.Models.CloudExample"), true));
                    pending.Children.Add(Text("♫")); pending.Children.Add(Text(_l("Music.Models.ExamplePending"))); border.Child = pending;
                }
            }
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
    public void PauseExamples() { foreach (var player in _players) player.Pause(); }
    private void DisposePlayers() { foreach (var player in _players) player.Dispose(); _players.Clear(); }
    public void Dispose() => DisposePlayers();
}
