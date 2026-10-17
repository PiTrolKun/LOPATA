using AIHub.Models;

namespace AIHub.Services;

internal static class MusicCoverProvenance
{
    // The supplied standard image was generated with this model, confirmed by its author.
    internal const string DefaultModel = "Z-Image Turbo";
    internal const string ModelTag = "LOPATA_COVER_MODEL";
    internal const string SourceTag = "LOPATA_COVER_SOURCE";
    internal static string Model(MusicCoverSelection? selection) =>
        string.IsNullOrWhiteSpace(selection?.ModelName) ? DefaultModel : selection.ModelName;
    internal static Dictionary<string, string> Tags(MusicCoverSelection? selection) => new(StringComparer.OrdinalIgnoreCase)
    {
        [ModelTag] = Model(selection), [SourceTag] = selection is null ? "standard" : "generated"
    };
}
