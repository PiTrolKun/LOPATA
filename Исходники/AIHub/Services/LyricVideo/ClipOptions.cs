using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.LyricVideo;

public sealed record ClipOptions
{
    public int Effects { get; init; } = 1;
    public int TextSize { get; init; }
    public bool SongWords { get; init; } = true;
    public string ExtraRainText { get; init; } = "";
    public string Palette { get; init; } = "green";
    public int Height { get; init; } = 1080;
    public int Width => Height * 16 / 9;
    public float EffectScale => Effects switch { 0 => .70f, 2 => 1.22f, _ => 1 };
    public float TextScale => TextSize switch { 1 => 1.18f, 2 => 1.36f, _ => 1 };
    public void Validate()
    {
        if (Effects is < 0 or > 2 || TextSize is < 0 or > 2 || Height is not (720 or 1080)
            || Palette is not ("green" or "cyan" or "amber") || ExtraRainText is null
            || ExtraRainText.Length > 4000 || ExtraRainText.Contains('\0'))
            throw new InvalidDataException("LyricVideo.InvalidSettings");
    }
    internal string[] RainWords(string lyrics)
    {
        Validate();
        var sources = new[] { SongWords ? lyrics : "", ExtraRainText };
        var words = new List<string>();
        foreach (var text in sources)
        {
            var sourceWords = new List<string>();
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var tokens = Regex.Matches(line, @"[\p{L}\p{N}]{2,20}").Select(m => m.Value).ToArray();
                sourceWords.AddRange(tokens);
                for (var i = 0; i + 1 < tokens.Length; i += 2)
                    if (tokens[i].Length + tokens[i + 1].Length < 27) sourceWords.Add(tokens[i] + " " + tokens[i + 1]);
            }
            words.AddRange(sourceWords.Distinct(StringComparer.OrdinalIgnoreCase).Take(512));
        }
        // Reserve space for both sources rather than losing custom words after a long song.
        return words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class ClipPreferences(string path)
{
    public static ClipPreferences Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Video", "preferences.json"));
    public ClipOptions Load()
    {
        var options = File.Exists(path) ? JsonSerializer.Deserialize<ClipOptions>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("LyricVideo.InvalidSettings") : new();
        options.Validate(); return options;
    }
    public void Save(ClipOptions options) { options.Validate(); RagBookPreparation.AtomicJson(path, options); }
}
