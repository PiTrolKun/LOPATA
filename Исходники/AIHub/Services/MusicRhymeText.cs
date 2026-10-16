using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Analysis-only formatting removal. The original UTF-16 coordinates never move.</summary>
internal static class MusicRhymeText
{
    public static string Prepare(string text)
    {
        var chars = text.ToCharArray();
        var lines = Regex.Matches(text, @"(?:\A|(?<=\n)|(?<=\r)(?!\n))[^\r\n]*").Cast<Match>().ToArray();
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l.Value));
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n]; var stripped = Regex.Replace(line.Value, @"[*_`]", " ");
            var next = n == first ? lines.Skip(n + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Value)) : null;
            var title = n == first && (Regex.IsMatch(line.Value, @"^\s*#{1,6}\s")
                || Regex.IsMatch(stripped, @"(?i)^\s*(название|title)\s*:")
                || Regex.IsMatch(line.Value, @"^\s*(\*\*|__)") && next is not null
                && Regex.IsMatch(Regex.Replace(next.Value, @"[*_`]", " "), @"^\s*\[[^\]]+\]\s*$"));
            if (title)
            {
                Array.Fill(chars, ' ', line.Index, line.Length); continue;
            }
            for (var j = 0; j < line.Length; j++)
                if (line.Value[j] is '*' or '_' or '`') chars[line.Index + j] = ' ';
            if (Regex.IsMatch(stripped, @"(?i)^\s*\[(концовка|финал)\]\s*$"))
            {
                var replacement = "[outro]".PadRight(line.Length);
                for (var j = 0; j < line.Length; j++) chars[line.Index + j] = replacement[j];
            }
        }
        return new(chars);
    }

    // Do not normalize Unicode here: a composed accent would change source positions.
    public static string Clean(string text) => Regex.Replace(text.ToLowerInvariant(), @"[*_`+\u0301\u0300\W]+", " ").Trim();
    public static int WordCount(string text) => Regex.Matches(text, @"[\w\u0301]+").Count;
    public static bool Terminal(string text) => Regex.IsMatch(text, "[.!?…][\\s*_'\"»”)\\]]*$");
}
