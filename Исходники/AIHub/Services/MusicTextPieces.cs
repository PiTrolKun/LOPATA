using System.Buffers;
using System.Globalization;
using System.Text;

namespace AIHub.Services;

// YuE2 protocol adaptation; yue2.cpp authors, MIT (Licenses/texts/music-tokenizer-MIT.txt).
// Pre-tokenization: contractions, letters, single numbers, symbols and whitespace.
// Rune categories also cover supplementary Unicode characters (unlike UTF-16 regex categories).
internal static class MusicTextPieces
{
    private static readonly string[] Contractions = ["s", "t", "re", "ve", "m", "ll", "d"];
    public static IEnumerable<string> Split(string text, CancellationToken cancellation)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = offset; var rune = At(text, offset);
            if (rune.Value == '\'')
            {
                var contraction = Contractions.FirstOrDefault(s => text.AsSpan(offset + 1).StartsWith(s, StringComparison.OrdinalIgnoreCase));
                if (contraction is not null)
                { offset += 1 + contraction.Length; yield return text[start..offset]; continue; }
            }
            var letters = offset;
            if (!Letter(rune) && !Number(rune) && rune.Value is not (10 or 13)) letters += rune.Utf16SequenceLength;
            var end = letters;
            while (end < text.Length && Letter(At(text, end))) end += At(text, end).Utf16SequenceLength;
            if (end > letters) { offset = end; yield return text[start..offset]; continue; }
            if (Number(rune)) { offset += rune.Utf16SequenceLength; yield return text[start..offset]; continue; }
            var symbols = rune.Value == 32 ? offset + 1 : offset;
            end = symbols;
            while (end < text.Length)
            {
                var next = At(text, end);
                if (Space(next) || Letter(next) || Number(next)) break;
                end += next.Utf16SequenceLength;
            }
            if (end > symbols)
            {
                while (end < text.Length && text[end] is '\r' or '\n') end++;
                offset = end; yield return text[start..offset]; continue;
            }
            if (Space(rune))
            {
                var lastNewline = -1; var lastStart = offset; end = offset;
                while (end < text.Length && Space(At(text, end)))
                {
                    lastStart = end;
                    var next = At(text, end); end += next.Utf16SequenceLength;
                    if (next.Value is 10 or 13) lastNewline = end;
                }
                offset = lastNewline >= 0 ? lastNewline : end < text.Length && lastStart > start ? lastStart : end;
                yield return text[start..offset]; continue;
            }
            offset += rune.Utf16SequenceLength; yield return text[start..offset];
        }
    }
    private static Rune At(string text, int offset) => Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out _) == OperationStatus.Done
        ? rune : throw new ArgumentException("Invalid Unicode text.");
    private static bool Letter(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
    private static bool Number(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    private static bool Space(Rune rune) => Rune.IsWhiteSpace(rune) || rune.Value is >= 0x1c and <= 0x1f;
}
