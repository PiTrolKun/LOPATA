using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record MusicWishChoice(string Id, string Prompt, string NameKey);
public sealed record MusicGenre(string Id, string Name);

public static class MusicWishCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<MusicGenre> Genres { get; } = Read<MusicGenre[]>("MusicGenres.json");
    private static readonly Dictionary<string, MusicWishChoice[]> Choices = Read<Dictionary<string, MusicWishChoice[]>>("MusicOptions.json");
    public static IReadOnlyList<MusicWishChoice> Group(string category) => Choices.GetValueOrDefault(category) ?? [];
    public static string Prompt(string category, string id) => Group(category).FirstOrDefault(x => x.Id == id)?.Prompt ?? id;
    public static string Label(MusicWishChoice choice, Func<string, string> l) => l(choice.NameKey);
    public static string GenreLabel(string name, Func<string, string> l)
    {
        var key = "Music.Genre." + name; var value = l(key); return value == key || value == name ? name : value + " · " + name;
    }
    public static bool Matches(string name, string query) => Fold(name).Contains(Fold(query), StringComparison.Ordinal);
    private static string Fold(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        .ToLowerInvariant().Replace('ё', 'е').Replace('-', ' ');
    private static T Read<T>(string file)
    {
        using var stream = typeof(MusicWishCatalog).Assembly.GetManifestResourceStream("AIHub.Content." + file)
            ?? throw new InvalidDataException(file);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException(file);
    }
    // Editorial suggestions, not inferred instrumentation or restrictions on the model.
    public static IReadOnlyDictionary<string, string[]> Recommendations(IEnumerable<string> genres)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            var name = Fold(genre);
            foreach (var (words, instruments) in Rules)
            {
                if (!words.Any(word => (" " + name + " ").Contains(" " + word + " ", StringComparison.Ordinal))) continue;
                foreach (var instrument in instruments)
                {
                    if (!result.TryGetValue(instrument, out var sources)) result[instrument] = sources = [];
                    if (!sources.Contains(genre)) sources.Add(genre);
                }
            }
        }
        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
    }
    private static readonly (string[] Words, string[] Instruments)[] Rules =
    [
        (["blues", "блюз"], ["electric-guitar", "bass", "drums", "piano", "organ", "harmonica"]),
        (["rock", "metal", "punk", "grunge", "рок", "метал"], ["electric-guitar", "bass", "drums"]),
        (["jazz", "bebop", "swing", "джаз"], ["piano", "double-bass", "drums", "saxophone", "trumpet"]),
        (["classical", "baroque", "orchestral", "romantic", "классика"], ["piano", "strings", "violin", "cello", "flute", "horn"]),
        (["folk", "country", "bluegrass", "americana", "фолк", "кантри"], ["acoustic-guitar", "banjo", "violin", "mandolin", "double-bass"]),
        (["electronic", "techno", "house", "trance", "edm", "synthpop", "ambient", "электроника"], ["synthesizer", "drum-machine", "electronic-bass", "pads"]),
        (["hip hop", "rap", "trap", "рэп", "хип хоп"], ["drum-machine", "electronic-bass", "sampler"]),
        (["funk", "soul", "r&b", "фанк", "соул"], ["bass", "drums", "electric-guitar", "electric-piano", "brass"]),
        (["reggae", "ska", "dub", "регги"], ["bass", "drums", "electric-guitar", "organ", "brass"]),
        (["pop", "поп"], ["piano", "synthesizer", "bass", "drums"]),
        (["salsa", "samba", "bossa nova", "latin"], ["percussion", "acoustic-guitar", "piano", "brass"]),
        (["flamenco"], ["acoustic-guitar", "percussion"]),
        (["chanson", "tango", "шансон"], ["accordion", "acoustic-guitar", "piano", "violin"]),
        (["industrial"], ["synthesizer", "drum-machine", "electric-guitar", "sampler"])
    ];
}
