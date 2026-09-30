using AIHub.Models;

namespace AIHub.Services;

/// <summary>Only the two editable fields belong to this independent request.</summary>
public static class LiteraryAnchorCompression
{
    public static long Count(string positive, string negative) => (long)positive.Length + negative.Length;

    public static ImageAnalysisHiddenMessage[] Request(string positive, string negative, Func<string, string> localize) =>
    [
        new()
        {
            Role = "user",
            Content = string.Format(localize("Literary.Anchor.CompressPrompt"), LiteraryPlotAnchorStore.MaxCharacters)
                + "\n\n" + localize("Literary.Anchor.Positive") + ":\n" + positive
                + "\n\n" + localize("Literary.Anchor.Negative") + ":\n" + negative
        }
    ];
}
