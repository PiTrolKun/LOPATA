using System.Globalization;

namespace AIHub.Services;

public sealed record MusicTextUsage(int TotalTokens, int LyricsTokens, int LyricsBudget, int Characters, bool Exceeded);

public static class MusicTextBudget
{
    public const int ContextSize = 24_576;
    public const int SafetyReserve = (ContextSize + 19) / 20; // Round 5% upwards: 1,229 tokens.
    public const int FillLimit = ContextSize - SafetyReserve;
    public const string DefaultInstruction = "Generate a chord-annotated ABC transcription, then generate music with codec tokens from the given conditions.";
    public static string BuildText(string lyrics, string tags = "", string instruction = DefaultInstruction) =>
        instruction + "\n[Tags]\n" + tags + "\n[Lyrics]\n" + lyrics + "\n";

    // Start-of-document and ABC-start are explicit tokens outside the ordinary text encoder.
    // Future output reservations are separate from the measured input, never reported as used tokens.
    public static MusicTextUsage Measure(IMusicTokenizer tokenizer, string lyrics, string tags = "",
        string instruction = DefaultInstruction, int outputReserve = 0, CancellationToken cancellation = default, bool instrumental = false)
    {
        if (outputReserve < 0) throw new ArgumentOutOfRangeException(nameof(outputReserve));
        var program = checked(tokenizer.Count(BuildText("", tags, instruction), cancellation) + 2);
        var total = checked(tokenizer.Count(BuildText(instrumental ? "" : lyrics, tags, instruction), cancellation) + 2);
        var weight = tokenizer.Count(lyrics, cancellation);
        var budget = Math.Max(0, FillLimit - program - outputReserve);
        return new(total, weight, budget, CharacterCount(lyrics),
            !instrumental && weight > budget || total > FillLimit - outputReserve || program + (long)outputReserve > FillLimit);
    }
    public static int CharacterCount(string text) => StringInfo.ParseCombiningCharacters(text).Length;
    public static bool CanStart(MusicTextUsage? usage, bool counting, string lyrics, bool instrumental = false) =>
        !counting && usage is { Exceeded: false } && (instrumental || !string.IsNullOrWhiteSpace(lyrics));
}
