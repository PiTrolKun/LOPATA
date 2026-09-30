using System.Globalization;
using System.Text;

namespace AIHub.Controls;

public sealed record SettingsSearchEntry(string Id, string SectionId, string TitleKey, string HelpKey, string KeywordsKey);

/// <summary>A local index over the same settings that navigation displays.</summary>
public static class SettingsSearchIndex
{
    public static IReadOnlyList<SettingsSearchEntry> Find(IEnumerable<SettingsSearchEntry> entries,
        string query, Func<string, string> text)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return entries.Where(entry =>
        {
            var haystack = Normalize(text(entry.TitleKey) + " " + text(entry.HelpKey) + " " + text(entry.KeywordsKey));
            return words.All(word => haystack.Contains(word, StringComparison.Ordinal));
        }).ToArray();
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture))
            result.Append(char.IsLetterOrDigit(character) ? character == 'ё' ? 'е' : character : ' ');
        return result.ToString();
    }
}
