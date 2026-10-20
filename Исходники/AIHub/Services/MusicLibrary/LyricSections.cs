using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLibrary;

public sealed record LyricSection(string EntryId, int Revision, string Title, string Language, string Genre,
    string Tags, string Role, int Start, int Length, string Text)
{
    public string Style { get; init; } = "";
}

public static class LyricSections
{
    public static IReadOnlyList<LyricSection> Split(LyricEntry entry)
    {
        entry.Validate(); var text = entry.Text;
        var result = new List<LyricSection>(); var start = 0; var role = "stanza";
        foreach (Match line in Regex.Matches(text, @"(?m)^[^\r\n]*(?:\r\n|\n|\r|\z)"))
        {
            if (line.Length == 0) continue;
            var marker = Regex.Match(line.Value.Trim(), @"^\[([^\]\r\n]+)\]$");
            var sectionMarker = marker.Success && Regex.IsMatch(marker.Groups[1].Value,
                @"^(?:verse|chorus|bridge|intro|outro|pre[- ]?chorus|post[- ]?chorus|hook|refrain|break|solo|instrumental|куплет|припев|бридж|вступление|проигрыш|аутро|рефрен|соло|предприпев|постприпев)\b", RegexOptions.IgnoreCase);
            if (sectionMarker || string.IsNullOrWhiteSpace(line.Value))
            {
                Add(start, line.Index);
                if (sectionMarker) { role = marker.Groups[1].Value; start = line.Index; }
                else start = line.Index + line.Length;
            }
        }
        Add(start, text.Length); return result;
        void Add(int from, int to)
        {
            var fragment = text[from..to].TrimEnd();
            if (!Regex.Replace(fragment, @"\[[^\]]*\]", "").Any(char.IsLetter)) return;
            result.Add(new(entry.Id, entry.Revision, entry.Title, entry.Language, entry.Genre, entry.Tags,
                role, from, fragment.Length, fragment) { Style = entry.Style });
        }
    }
}
