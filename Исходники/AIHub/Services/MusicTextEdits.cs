using System.Globalization;

namespace AIHub.Services;

public sealed record MusicTextSelection(string Text, int Start, int Length);
public sealed record MusicTextEdit(int Start, int RemoveLength, string Insert, int SelectionStart, int SelectionLength);

/// <summary>Small, lossless edits. Markers are experimental text, not a model control protocol.</summary>
public static class MusicTextEdits
{
    public static MusicTextEdit? Accent(MusicTextSelection source)
    {
        if (!Valid(source) || source.Length == 0) return null;
        var selected = source.Text.Substring(source.Start, source.Length);
        if (StringInfo.ParseCombiningCharacters(selected).Length != 1 ||
            !"аеёиоуыэюяАЕЁИОУЫЭЮЯaeiouyAEIOUY".Contains(selected[0]) || selected.Contains('\u0301') ||
            selected.Skip(1).Any(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)) return null;
        // A user may select only the base letter of an already accented vowel.
        for (var i = source.Start + source.Length; i < source.Text.Length &&
            CharUnicodeInfo.GetUnicodeCategory(source.Text[i]) == UnicodeCategory.NonSpacingMark; i++)
            if (source.Text[i] == '\u0301') return null;
        var accented = selected + "\u0301";
        return new(source.Start, source.Length, accented, source.Start, accented.Length);
    }

    public static MusicTextEdit? Markers(MusicTextSelection source, IEnumerable<string> markers)
    {
        if (!Valid(source)) return null;
        var values = markers.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (values.Length == 0 || values.Any(x => x.Contains('\r') || x.Contains('\n'))) return null;
        var newline = source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            source.Text.Contains('\n') ? "\n" : source.Text.Contains('\r') ? "\r" : Environment.NewLine;
        var prefix = source.Start > 0 && source.Text[source.Start - 1] is not ('\r' or '\n') ? newline : "";
        var insert = prefix + string.Join(newline, values) + newline;
        return new(source.Start, 0, insert, source.Start + insert.Length, source.Length);
    }

    public static string? Marker(string text, string brackets = "[]")
    {
        if (brackets is not ("[]" or "()" or "{}") || string.IsNullOrWhiteSpace(text) ||
            text.Any(c => char.IsControl(c) || "[](){}".Contains(c))) return null;
        return brackets[0] + text.Trim() + brackets[1];
    }

    private static bool Valid(MusicTextSelection source) => source.Start >= 0 && source.Length >= 0 &&
        source.Start <= source.Text.Length && source.Length <= source.Text.Length - source.Start &&
        Boundary(source.Text, source.Start) && Boundary(source.Text, source.Start + source.Length);
    private static bool Boundary(string text, int index) => index == 0 || index == text.Length ||
        !(char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) &&
        !(text[index - 1] == '\r' && text[index] == '\n');
}
