using AIHub.Models;

namespace AIHub.Services;

public static class MusicWishPrompt
{
    public static string Build(MusicPreferences state)
    {
        var parts = new List<string>();
        foreach (var category in new[] { "genres", "mood", "instruments", "rhythm", "development" })
            parts.AddRange(state.Selected(category).Select(id => MusicWishCatalog.Prompt(category, id)));
        if (state.Instrumental) parts.Add("instrumental, no vocals");
        else
        {
            parts.AddRange(state.Selected("delivery").Select(id => MusicWishCatalog.Prompt("delivery", id)));
            if (state.NoChoir) parts.Add("no choir");
            if (state.NoBacking) parts.Add("no backing vocals");
            foreach (var performer in state.Performers)
            {
                var voice = new List<string>();
                if (!string.IsNullOrWhiteSpace(performer.Name)) voice.Add(performer.Name.Trim() + ":");
                foreach (var (category, id) in new[] { ("voice", performer.Voice), ("range", performer.Range), ("language", performer.Language) })
                    if (!string.IsNullOrEmpty(id)) voice.Add(MusicWishCatalog.Prompt(category, id));
                voice.AddRange(performer.Timbres.Select(id => MusicWishCatalog.Prompt("timbre", id)));
                voice.AddRange(performer.Delivery.Select(id => MusicWishCatalog.Prompt("delivery", id)));
                if (!string.IsNullOrWhiteSpace(performer.Notes)) voice.Add(performer.Notes.Trim());
                parts.Add(string.Join(" ", voice));
            }
        }
        parts.AddRange(state.Selected("avoid").Select(id => "avoid " + MusicWishCatalog.Prompt("avoid", id)));
        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
