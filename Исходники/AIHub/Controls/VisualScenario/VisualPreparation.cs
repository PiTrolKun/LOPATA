using System.Windows;
using System.Windows.Controls;
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

    internal static VisualPair? Enter(Window owner, Func<string, string> localize, VisualComponents components, VisualPair preferred)
    {
        var readyAdvisors = VisualComponents.Advisors.Where(components.AdvisorPresent).ToArray();
        var readyArtists = VisualComponents.Artists.Where(components.ArtistPresent).ToArray();
        string Title(string title, bool ready) => title + " · " + localize(ready ? "Visual.Present" : "Visual.ComponentsMissing");
        var advisors = Options(VisualComponents.Advisors.Select(id => new Choice(id, Title(components.AdvisorName(id)
            + (id == VisualComponents.Gemma ? " · " + localize("Visual.Recommended") : ""),
            readyAdvisors.Contains(id)))), Choose(preferred.Advisor, readyAdvisors));
        var artists = Options(VisualComponents.Artists.Select(id => new Choice(id, Title(ImageGenerationCatalog.DisplayName(id),
            readyArtists.Contains(id)))), Choose(preferred.Artist, readyArtists));
        var panel = new StackPanel { Margin = new(0, 12, 0, 0) };
        panel.Children.Add(MusicWishUi.Text(localize("Visual.ChoosePair")));
        panel.Children.Add(MusicWishUi.Text(localize("Visual.Advisor")));
        panel.Children.Add(advisors);
        panel.Children.Add(MusicWishUi.Text(localize("Visual.Artist")));
        panel.Children.Add(artists);

        VisualPair? ready = null;
        var initial = true;
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
                var artist = await components.PrepareArtistAsync(pair.Artist, true, download, progress, token);
                if (artist is null) continue;
                ready = pair;
                return true;
            }
            return false;
        }, ApplicationBackgroundOperations.ExitToken, choices: panel);
        return BackgroundUserDecision.ShowDialog(window, owner, ApplicationBackgroundOperations.ExitToken) == true ? ready : null;
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
