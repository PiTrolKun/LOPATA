using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub.Controls.VisualScenario;

internal static class VisualPreparation
{
    private sealed record Choice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    internal static string ModelLabel(string id, string name, Func<string, string> localize) => id switch
    {
        VisualSenseNova.Id => name + " · " + localize("Visual.SenseNovaExperimental"),
        VisualComponents.Gemma => name + " · " + localize("Visual.Recommended"),
        _ => name
    };

    internal static VisualPair? Enter(Window owner, Func<string, string> localize, VisualComponents components, VisualPair preferred,
        Func<string, bool, Task> retire)
    {
        var readyAdvisors = VisualComponents.Advisors.Where(components.AdvisorPresent).ToArray();
        var readyArtists = VisualComponents.Artists.Where(components.ArtistPresent).ToArray();
        string Title(string title, bool ready) => title + " · " + localize(ready ? "Visual.Present" : "Visual.ComponentsMissing");
        Choice[] AdvisorChoices() => VisualComponents.Advisors.Select(id => new Choice(id, Title(ModelLabel(id, components.AdvisorName(id), localize),
            readyAdvisors.Contains(id)))).ToArray();
        Choice[] ArtistChoices() => VisualComponents.Artists.Select(id => new Choice(id, Title(ModelLabel(id, ImageGenerationCatalog.DisplayName(id), localize),
            readyArtists.Contains(id)))).ToArray();
        var advisors = Options(AdvisorChoices(), Choose(preferred.Advisor, readyAdvisors));
        var artists = Options(ArtistChoices(), Choose(preferred.Artist, readyArtists));
        var panel = new StackPanel { Margin = new(0, 12, 0, 0) };
        panel.Children.Add(MusicWishUi.Text(localize("Visual.ChoosePair")));
        panel.Children.Add(MusicWishUi.Text(localize("Visual.Advisor")));
        panel.Children.Add(advisors);
        panel.Children.Add(MusicWishUi.Text(localize("Visual.Artist")));
        panel.Children.Add(artists);

        VisualPair? ready = null;
        var initial = true;
        Popup? hint = null;
        var refreshing = false;
        void Hint(bool artist)
        {
            if (refreshing || advisors.SelectedItem is not Choice advisor || artists.SelectedItem is not Choice artistChoice) return;
            if (hint is not null) hint.IsOpen = false;
            hint = VisualSenseNovaHint.Show(artist ? artists : advisors, localize, artist ? artistChoice.Id : advisor.Id,
                artist ? advisor.Id : artistChoice.Id, artist);
        }
        advisors.SelectionChanged += (_, _) => Hint(artist: false);
        artists.SelectionChanged += (_, _) => Hint(artist: true);
        var window = new VideoPreparationWindow(owner, localize, localize("Visual.Title"), async (download, progress, token) =>
        {
            using var audit = ComponentAudit.CaptureOperation();
            var wanted = new VisualPair(((Choice)advisors.SelectedItem).Id, ((Choice)artists.SelectedItem).Id);
            var combinations = initial
                ? (from a in readyAdvisors.OrderByDescending(a => a == wanted.Advisor)
                   from b in readyArtists.OrderByDescending(b => b == wanted.Artist)
                   select new VisualPair(a, b)).ToArray() : [wanted with { Confirmed = true }];
            initial = false;
            foreach (var pair in combinations)
            {
                var advisor = await components.PrepareAdvisorAsync(pair.Advisor, download, progress, token);
                if (advisor is null) continue;
                if (pair.Advisor != VisualSenseNova.Id || pair.Artist != VisualSenseNova.Id)
                {
                    var artist = await components.PrepareArtistAsync(pair.Artist, true, download, progress, token);
                    if (artist is null) continue;
                }
                ready = pair;
                return true;
            }
            return false;
        }, ApplicationBackgroundOperations.ExitToken, choices: panel);
        window.Closed += (_, _) => { if (hint is not null) hint.IsOpen = false; };
        AddRemoveButton(advisors, artist: false);
        AddRemoveButton(artists, artist: true);
        return BackgroundUserDecision.ShowDialog(window, owner, ApplicationBackgroundOperations.ExitToken) == true ? ready : null;

        void AddRemoveButton(System.Windows.Controls.ComboBox combo, bool artist)
        {
            var index = panel.Children.IndexOf(combo);
            panel.Children.Remove(combo);
            var row = new DockPanel();
            var remove = MusicWishUi.Button(localize("Music.Models.Remove.Action"),
                artist ? "Visual.Preparation.RemoveArtist" : "Visual.Preparation.RemoveAdvisor", () =>
                {
                    if (combo.SelectedItem is not Choice selected) return;
                    _ = window.RunChoiceActionAsync(async () =>
                    {
                        await VisualModelRemoval.RemoveAsync(window, localize, components, selected.Id, artist,
                            () => retire(selected.Id, artist));
                        readyAdvisors = VisualComponents.Advisors.Where(components.AdvisorPresent).ToArray();
                        readyArtists = VisualComponents.Artists.Where(components.ArtistPresent).ToArray();
                        refreshing = true;
                        try { RefreshChoices(advisors, AdvisorChoices()); RefreshChoices(artists, ArtistChoices()); }
                        finally { refreshing = false; }
                    });
                });
            void Update() => remove.IsEnabled = combo.SelectedItem is Choice selected && components.HasRemovableFiles(selected.Id, artist);
            combo.SelectionChanged += (_, _) => Update();
            Update();
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(combo);
            panel.Children.Insert(index, row);
        }
    }

    private static void RefreshChoices(System.Windows.Controls.ComboBox combo, Choice[] choices)
    {
        var id = (combo.SelectedItem as Choice)?.Id;
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(value => value.Id == id) ?? choices[0];
    }

    private static string Choose(string preferred, string[] ready) =>
        ready.Contains(preferred) ? preferred : ready.FirstOrDefault() ?? preferred;

    private static System.Windows.Controls.ComboBox Options(IEnumerable<Choice> options, string id)
    {
        var values = options.ToArray();
        return new() { ItemsSource = values, SelectedItem = values.FirstOrDefault(value => value.Id == id) ?? values[0],
            Margin = new(0, 3, 0, 8), MinHeight = 28 };
    }
}
