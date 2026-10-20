using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLibrary;

public sealed record LyricArtistStatus(string[] Requested, string[] Missing);
public sealed record LyricArtistSelection(LyricArtistStatus Status, LyricEntry[] Entries)
{
    // Names are metadata identities, not concepts for the embedding model to guess.
    private const string Cue = @"(?:в\s+стиле|стил[ьяе]|манер[аеуы]|как(?:\s+у)?|похож[\p{L}]*\s+на|исполнител[\p{L}]*|артист[\p{L}]*|групп[\p{L}]*|performer|artist|band|like|similar\s+to|style\s+of)\s+(?:(?:the|group|band)\s+)?[«""“']?\s*$";
    private static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ").Trim();
    private static bool HasCue(string query, int position)
    {
        var prefix = query[Math.Max(0, position - 100)..position];
        return Regex.IsMatch(prefix, Cue, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(prefix, @"(?:не\s+(?:как(?:\s+у)?|в\s+стиле)|not\s+(?:like|in\s+the\s+style\s+of))\s+(?:(?:the\s+)?(?:исполнител[\p{L}]*|артист[\p{L}]*|групп[\p{L}]*|performer|artist|band)\s+)?[«""“']?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    public static LyricArtistSelection? Resolve(IReadOnlyList<LyricEntry> entries, string query)
    {
        var normalized = Normalize(query);
        var artists = entries.Where(e => !string.IsNullOrWhiteSpace(e.Artist))
            .GroupBy(e => Normalize(e.Artist)).ToDictionary(g => g.Key, g => g.First().Artist);
        var requested = new List<string>();
        foreach (var (key, name) in artists.OrderByDescending(p => p.Key.Length))
        {
            if (normalized == key) { requested.Add(name); continue; }
            foreach (Match match in Regex.Matches(normalized, @"(?<![\p{L}\p{N}])" + Regex.Escape(key) + @"(?![\p{L}\p{N}])"))
                if (HasCue(normalized, match.Index)) { requested.Add(name); break; }
        }
        foreach (Match match in Regex.Matches(query, @"«(?<name>[^»\r\n]{1,300})»|“(?<name>[^”\r\n]{1,300})”|""(?<name>[^""\r\n]{1,300})""|'(?<name>[^'\r\n]{1,300})'"))
            if (HasCue(query, match.Index)) requested.Add(match.Groups["name"].Value.Trim());
        // Unquoted unknown names require an explicit performer noun and an initial capital.
        foreach (Match match in Regex.Matches(query,
            @"(?i:исполнител[\p{L}]*|артист[\p{L}]*|групп[\p{L}]*|performer|artist|band)\s+(?<name>[\p{Lu}\p{N}][^\r\n.!?,;:«""”]{0,120})"))
        {
            if (!HasCue(query, match.Groups["name"].Index)) continue;
            var name = match.Groups["name"].Value.Trim();
            if (!requested.Any(n => Normalize(name).StartsWith(Normalize(n), StringComparison.Ordinal))) requested.Add(name);
        }
        var names = requested.DistinctBy(Normalize).Take(8).ToArray();
        if (names.Length == 0) return null;
        var wanted = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var selected = entries.Where(e => wanted.Contains(Normalize(e.Artist))).ToArray();
        return new(new(names, names.Where(n => !artists.ContainsKey(Normalize(n))).ToArray()), selected);
    }
    public LyricExample[] Examples(string query)
    {
        var language = Regex.IsMatch(query, @"русск|по-русски|\bRussian\b", RegexOptions.IgnoreCase) ? "ru"
            : Regex.IsMatch(query, @"английск|\bEnglish\b", RegexOptions.IgnoreCase) ? "en" : null;
        var works = Entries.OrderByDescending(e => language is not null && e.Language == language)
            .ThenBy(e => e.Title, StringComparer.Ordinal).Take(8)
            .Select(entry => (Entry: entry, Sections: LyricSections.Split(entry).Take(2).ToArray())).ToArray();
        var result = new List<LyricExample>();
        // Interleave works: one large song must not consume the whole reference budget.
        for (var section = 0; section < 2; section++)
        foreach (var work in works.Where(w => w.Sections.Length > section))
        {
            var fragment = work.Sections[section];
            var length = Math.Min(fragment.Text.Length, 1200);
            if (length < fragment.Text.Length)
            {
                var newline = fragment.Text.LastIndexOf('\n', length - 1, length);
                if (newline > length / 2) length = newline;
                else if (char.IsHighSurrogate(fragment.Text[length - 1])) length--;
            }
            result.Add(new(work.Entry.Id, work.Entry.Revision, work.Entry.Title, fragment.Role, fragment.Text[..length], 0) {
                Artist = work.Entry.Artist, Genre = work.Entry.Genre, Style = work.Entry.Style, Language = work.Entry.Language, MatchKind = "artist" });
            if (result.Count == 8) return result.ToArray();
        }
        return result.ToArray();
    }
}
