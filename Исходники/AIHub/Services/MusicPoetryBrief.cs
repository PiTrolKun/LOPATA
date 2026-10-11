using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicPoetryBrief(string Idea, string Structure, string Music)
{
    private static readonly HashSet<string> EngineKeys = MusicExpertCatalog.Parameters.Concat(MusicAceCatalog.Parameters)
        .Concat(MusicDiffRhythmCatalog.Parameters).Concat(MusicHeartMuLaCatalog.Parameters).Select(parameter => parameter.Key)
        .Concat(MusicAceCatalog.TextParameters.Select(parameter => parameter.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string ToText() => string.Join("\n\n", new[] { ("[Idea]", Idea), ("[Structure]", Structure), ("[Music]", Music) }
        .Where(part => !string.IsNullOrWhiteSpace(part.Item2)).Select(part => part.Item1 + "\n" + part.Item2.Trim()));

    public static MusicPoetryBrief FromSnapshot(MusicProjectSnapshot snapshot, Func<string, string> localize)
    {
        var idea = new List<string>();
        foreach (var (key, value) in new[] { ("Music.Output.Title", snapshot.Title), ("Music.Output.Artist", snapshot.Artist),
            ("Music.Output.Comment", snapshot.Comment) })
            if (!string.IsNullOrWhiteSpace(value)) idea.Add(localize(key) + ": " + value);
        var music = new List<string>(); var wishes = MusicWishPrompt.Build(snapshot.Wishes.ToPreferences());
        if (!string.IsNullOrWhiteSpace(wishes)) music.Add(wishes);
        if (snapshot.Expert.Values.TryGetValue("bpm", out var bpm) && bpm > 0)
            music.Add("bpm = " + bpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var structure = new List<string>();
        foreach (var key in new[] { "vocal_language", "timesignature", "keyscale" })
            if (snapshot.Expert.TextValues.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                && value is not ("auto" or "unknown"))
                (key == "vocal_language" ? structure : music).Add(key + " = " + value);
        return new(string.Join("\n", idea), string.Join("\n", structure), string.Join("\n", music));
    }

    public static MusicPoetryBrief Parse(string text)
    {
        var parts = new[] { new List<string>(), new List<string>(), new List<string>() }; var topic = 2;
        foreach (var line in Filter(text).Split('\n'))
        {
            var value = line.Trim();
            var heading = value.TrimEnd(':').ToLowerInvariant();
            var section = heading switch {
                "[idea]" or "идея песни" or "song idea" => 0,
                "[structure]" or "текст и структура" or "lyrics and structure" => 1,
                "[music]" or "музыка и исполнение" or "music and performance" or "музыкальные параметры" or "musical parameters" => 2,
                _ => -1 };
            if (section >= 0) { topic = section; continue; }
            var routed = Regex.IsMatch(value, @"^(Название|Исполнитель|Комментарий|Title|Artist|Comment|Music\.Output\.(Title|Artist|Comment))\s*:", RegexOptions.IgnoreCase) ? 0
                : Regex.IsMatch(value, @"^(Язык|Рифма|Структура|Language|Rhyme|Structure|vocal_language)\s*[:=]", RegexOptions.IgnoreCase) ? 1 : topic;
            parts[routed].Add(line);
        }
        return new(string.Join("\n", parts[0]).Trim(), string.Join("\n", parts[1]).Trim(), string.Join("\n", parts[2]).Trim());
    }

    // Only known service fields are removed; arbitrary free-form wishes stay intact.
    public static string Filter(string text)
    {
        var result = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var value = line.Trim();
            if (Regex.IsMatch(value, @"^(Желаемая длительность|Desired duration|Duration|Варианты|Variants|Формат|Format|Music\.Generation\.(Duration|Variants)|Music\.Output\.Format)\s*:", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(value, @"^(Текущие настройки музыкального движка|Current music engine settings|Current musical engine settings|Композиция и исполнение|Composition and performance|Music\.Poetry\.Expert|Music\.Tuning\.Title)\s*:", RegexOptions.IgnoreCase)) continue;
            var assignment = Regex.Match(value, @"^([a-z][a-z0-9_.]*)\s*=", RegexOptions.IgnoreCase);
            if (assignment.Success && EngineKeys.Contains(assignment.Groups[1].Value)
                && !Regex.IsMatch(value, @"^(bpm|vocal_language|timesignature|keyscale)\s*=", RegexOptions.IgnoreCase)) continue;
            if (value.StartsWith('{') && value.Contains("\"Profile\"", StringComparison.Ordinal) && value.Contains("\"Pins\"", StringComparison.Ordinal)) continue;
            if (value.StartsWith('{'))
            {
                try
                {
                    var remainder = Encoding.UTF8.GetBytes(string.Join("\n", lines.Skip(index)));
                    var reader = new Utf8JsonReader(remainder);
                    using var json = JsonDocument.ParseValue(ref reader);
                    if (json.RootElement.TryGetProperty("Profile", out _) && json.RootElement.TryGetProperty("Pins", out _))
                    { index += Encoding.UTF8.GetString(remainder.AsSpan(0, checked((int)reader.BytesConsumed))).Count(character => character == '\n'); continue; }
                }
                catch (JsonException) { }
            }
            if (Regex.IsMatch(value, @"^(Без вокала|Без хора|Без бэк-вокала|Instrumental|No vocals|No choir|No backing vocals|Music\.Wishes\.(Instrumental|NoChoir|NoBacking))\s*:\s*(Нет|No|False|Music\.Poetry\.No)\s*$", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(value, @"^(Название|Исполнитель|Комментарий|Пожелания|Title|Artist|Comment|Wishes|Music\.Output\.(Title|Artist|Comment)|Music\.Poetry\.Wishes)\s*:\s*$", RegexOptions.IgnoreCase)) continue;
            result.Add(line);
        }
        return string.Join("\n", result).Trim();
    }
}
