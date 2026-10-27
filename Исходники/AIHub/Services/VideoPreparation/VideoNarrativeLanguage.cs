using System.Text;

namespace AIHub.Services.VideoPreparation;

public enum VideoNarrativeLanguageKind { English, Other, Unknown, ProtectedOnly }
public sealed record VideoNarrativeLanguageResult(VideoNarrativeLanguageKind Kind, string Language)
{
    public bool NeedsTranslation => Kind is VideoNarrativeLanguageKind.Other or VideoNarrativeLanguageKind.Unknown;
}

/// <summary>Only balanced quoted/bracketed passages are excluded from detection. Input itself is never rewritten.</summary>
public static class VideoNarrativeLanguage
{
    public static VideoNarrativeLanguageResult Detect(string text) => Detect(text, WindowsLanguageDetection.Recognize);

    internal static VideoNarrativeLanguageResult Detect(string text, Func<string, string[]> recognize)
    {
        var narrative = OutsideProtected(text, out var balanced);
        if (!balanced) return new(VideoNarrativeLanguageKind.Unknown, "");
        var letters = narrative.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        if (letters.Length == 0) return new(VideoNarrativeLanguageKind.ProtectedOnly, "");
        var words = 0; var inWord = false;
        foreach (var rune in narrative.EnumerateRunes())
        {
            if (Rune.IsLetter(rune)) { if (!inWord) words++; inWord = true; }
            else if (rune.Value is not ('\'' or '’')) inWord = false;
        }
        if (words < 3 || letters.Length < 12) return new(VideoNarrativeLanguageKind.Unknown, "");
        var languages = recognize(narrative);
        var primary = languages.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "";
        if (primary.Length == 0) return new(VideoNarrativeLanguageKind.Unknown, "");
        // ELS ranks languages but does not supply numeric confidence. Mixed non-Latin prose must still be translated.
        var latin = letters.All(r => r.Value <= 0x024f || r.Value is >= 0x1e00 and <= 0x1eff);
        return new((primary == "en" || primary.StartsWith("en-", StringComparison.Ordinal)) && latin
            ? VideoNarrativeLanguageKind.English : VideoNarrativeLanguageKind.Other, primary);
    }

    public static string OutsideProtected(string text, out bool balanced)
    {
        var output = text.ToCharArray(); var frames = new Stack<(char Close, bool Quote)>();
        var start = 0; balanced = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\'' or '\\') { i++; continue; }
            // Apostrophes inside words are not quotation marks: don't, traveller's, l'homme.
            var apostrophe = c is '\'' or '’' && i > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]);
            if (apostrophe) continue;
            if (frames.TryPeek(out var top))
            {
                if (c == top.Close)
                {
                    frames.Pop();
                    if (frames.Count == 0) output.AsSpan(start, i - start + 1).Fill(' ');
                    continue;
                }
                if (top.Quote) continue;
            }
            var closing = c switch
            {
                '(' => ')',
                '[' => ']',
                '{' => '}',
                '<' => '>',
                '"' => '"',
                '\'' => '\'',
                '«' => '»',
                '‹' => '›',
                '“' => '”',
                '„' => '“',
                '‘' => '’',
                '「' => '」',
                '『' => '』',
                _ => '\0'
            };
            if (closing != '\0')
            {
                if (frames.Count == 0) start = i;
                frames.Push((closing, c is not ('(' or '[' or '{' or '<')));
            }
            else if (c is ')' or ']' or '}' or '>') balanced = false;
        }
        balanced &= frames.Count == 0;
        return new string(output);
    }
}
