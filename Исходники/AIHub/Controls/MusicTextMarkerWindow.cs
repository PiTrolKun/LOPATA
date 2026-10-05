using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>Selects text only; nothing is written to the editor until the dialog is accepted.</summary>
public sealed class MusicTextMarkerWindow : Window
{
    public IReadOnlyList<string>? AcceptedMarkers { get; private set; }
    public string SelectedBrackets { get; private set; }
    public MusicTextMarkerWindow(Func<string, string> l, string category, IReadOnlyList<MusicPerformer> performers, string brackets = "[]")
    {
        SelectedBrackets = brackets;
        MusicWishUi.PrepareWindow(this, l("Music.Text." + category), "Music.Text.Picker", 520);
        Height = 440; MinHeight = 350;
        var grid = new Grid { Margin = new(16) };
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var panel = new StackPanel();
        grid.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(MusicWishUi.Text(l("Music.Text.Experimental")));
        var options = new ComboBox { Margin = new(0, 6, 0, 8), DisplayMemberPath = nameof(MusicTextMarkerChoice.NameKey) };
        AutomationProperties.SetAutomationId(options, "Music.Text.Choice");
        AutomationProperties.SetName(options, l("Music.Text." + category));
        options.ItemsSource = category == "Performer" ? performers.Select(x => new MusicTextMarkerChoice(x.Name, x.Name)).ToArray() :
            MusicTextMarkerCatalog.Choices(category).Select(x => x with { NameKey = l(x.NameKey) })
                .Append(new MusicTextMarkerChoice("", l("Music.Text.Custom"))).ToArray();
        options.SelectedIndex = options.Items.Count > 0 ? 0 : -1;
        panel.Children.Add(options);
        var format = new ComboBox { ItemsSource = new[] { "[]", "()", "{}" }, SelectedItem = brackets, Width = 100,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(format, "Music.Text.Brackets");
        AutomationProperties.SetName(format, l("Music.Text.Brackets"));
        if (category == "Performer")
        {
            panel.Children.Add(MusicWishUi.Text(l("Music.Text.Brackets"))); panel.Children.Add(format);
            if (performers.Count == 0) panel.Children.Add(MusicWishUi.Text(l("Music.Text.NoPerformers")));
        }
        var extra = new TextBox { Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(extra, "Music.Text.Extra");
        AutomationProperties.SetName(extra, l("Music.Text.Extra"));
        if (category != "Performer")
        {
            panel.Children.Add(MusicWishUi.Text(l("Music.Text.Extra"))); panel.Children.Add(extra);
        }
        var preview = MusicWishUi.Text(""); AutomationProperties.SetAutomationId(preview, "Music.Text.Preview"); panel.Children.Add(preview);
        var error = MusicWishUi.Text(l("Music.Text.Invalid")); error.Foreground = System.Windows.Media.Brushes.Orange;
        panel.Children.Add(error);
        var footer = MusicWishUi.Footer(l, () =>
        {
            var markers = Build(); if (markers is null) return;
            AcceptedMarkers = markers; SelectedBrackets = (string)format.SelectedItem; DialogResult = true;
        }, () => DialogResult = false);
        Grid.SetRow(footer, 1); grid.Children.Add(footer); Content = grid;
        var apply = footer.Children.OfType<Button>().First();
        options.SelectionChanged += (_, _) => Update(); format.SelectionChanged += (_, _) => Update();
        extra.TextChanged += (_, _) => Update(); Update();

        IReadOnlyList<string>? Build()
        {
            if (options.SelectedItem is not MusicTextMarkerChoice choice || format.SelectedItem is not string delimiters) return null;
            var marker = MusicTextEdits.Marker(choice.Text, category == "Performer" ? delimiters : "[]");
            if (choice.Text.Length > 0 && marker is null) return null;
            if (category == "Performer" || string.IsNullOrWhiteSpace(extra.Text)) return marker is null ? null : [marker];
            var additional = MusicTextEdits.Marker(extra.Text);
            return additional is null ? null : marker is null ? [additional] : [marker, additional];
        }
        void Update()
        {
            var markers = Build(); apply.IsEnabled = markers is not null;
            preview.Text = markers is null ? "" : string.Join(Environment.NewLine, markers);
            error.Visibility = markers is null && options.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
