using System.Text;

namespace AIHub.Services.OfficeReader;

/// <summary>Logical text lines and Unicode characters, including spaces but excluding line endings.</summary>
internal readonly record struct ReaderTextStatistics(long Lines, long Characters)
{
    // A deliberately inexpensive, model-independent estimate. No tokenizer/model is loaded.
    internal long ApproximateTokens => (long)Math.Ceiling(Characters / 3.2);

    internal static ReaderTextStatistics Count(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return default;
        long lines = 1, characters = 0;
        var previousCr = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r') lines++;
            else if (rune.Value == '\n') { if (!previousCr) lines++; }
            else characters++;
            previousCr = rune.Value == '\r';
        }
        return new(lines, characters);
    }

    public static ReaderTextStatistics operator +(ReaderTextStatistics a, ReaderTextStatistics b) =>
        new(a.Lines + b.Lines, a.Characters + b.Characters);
}
