using System.IO;
using System.Text.Json;

namespace AIHub.Controls;

/// <summary>Short, translated product guidance, shipped with the application.</summary>
internal static class LiteraryTipCatalog
{
    private static readonly Lazy<IReadOnlyList<LiteraryTip>> Russian = new(() => Read("ru"));
    private static readonly Lazy<IReadOnlyList<LiteraryTip>> English = new(() => Read("en"));

    public static IReadOnlyList<LiteraryTip> Load(string language) =>
        language == "ru" ? Russian.Value : English.Value;

    private static IReadOnlyList<LiteraryTip> Read(string language)
    {
        using var stream = typeof(LiteraryTipCatalog).Assembly.GetManifestResourceStream(
            $"AIHub.Content.LiteraryTips.{language}.json")
            ?? throw new InvalidDataException("The built-in tip catalog is missing.");
        var tips = JsonSerializer.Deserialize<LiteraryTip[]>(stream)
            ?? throw new InvalidDataException("The built-in tip catalog is empty.");
        if (tips.Length == 0 || tips.Any(t => string.IsNullOrWhiteSpace(t.Id)
            || string.IsNullOrWhiteSpace(t.Category) || string.IsNullOrWhiteSpace(t.Role)
            || string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Body))
            || tips.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tips.Length)
            throw new InvalidDataException("The built-in tip catalog is invalid.");
        return Array.AsReadOnly(tips);
    }
}
