using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicExample(string Id, string File, string Title, bool Cloud, string Sha256,
    long Bytes, double DurationSeconds, string Request, string Genre)
{
    public string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "MusicExamples", File);
}

public sealed record MusicExampleMetadata(Dictionary<string, string> Tags, string Technical)
{
    public string Lyrics => Tags.GetValueOrDefault("lyrics") ?? Tags.FirstOrDefault(p =>
        p.Key.StartsWith("lyrics-", StringComparison.OrdinalIgnoreCase)).Value ?? "";

    public MusicProjectSnapshot Restore()
    {
        string Required(string key) => Tags.TryGetValue(key, out var value) && value.Length > 0
            ? value : throw new InvalidDataException("Missing audio metadata: " + key);
        if (Required("LOPATA_MODEL") != "YuE2 3B Q8_0" || Required("LOPATA_MODEL_REVISION") != MusicComponentCatalog.Revision)
            throw new InvalidDataException("Unsupported example model.");
        var raw = Required("LOPATA_PARAMETERS").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : "", StringComparer.Ordinal);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in MusicExpertCatalog.Parameters) {
            if (!raw.TryGetValue(parameter.Key, out var text) || !double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value)) throw new InvalidDataException("Missing/invalid parameter: " + parameter.Key);
            values.Add(parameter.Key, value);
        }
        var expert = new MusicExpertSettings { Values = values, Tuning = Tags.TryGetValue("LOPATA_TUNING", out var tuning)
            ? JsonSerializer.Deserialize<ModelTuningState>(tuning) : null };
        expert.Validate();
        var snapshot = new MusicProjectSnapshot {
            Lyrics = Lyrics.Length > 0 ? Lyrics : throw new InvalidDataException("Missing lyrics."),
            Title = Tags.GetValueOrDefault("title", ""), Artist = Tags.GetValueOrDefault("artist", ""),
            Comment = Tags.GetValueOrDefault("comment", ""),
            DurationSeconds = int.Parse(Required("LOPATA_DURATION_REQUEST_SECONDS"), CultureInfo.InvariantCulture),
            Expert = expert,
            Wishes = JsonSerializer.Deserialize<MusicWishSnapshot>(Required("LOPATA_WISHES")) ?? throw new InvalidDataException("Missing wishes."),
            Output = JsonSerializer.Deserialize<MusicOutputSettings>(Required("LOPATA_OUTPUT")) ?? throw new InvalidDataException("Missing output.") };
        snapshot.Validate(); return snapshot;
    }
}

public static class MusicExamples
{
    public static IReadOnlyList<MusicExample> All { get; } = Load();
    private static IReadOnlyList<MusicExample> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIHub.Content.MusicExamples.json")
            ?? throw new InvalidDataException("Missing music examples catalog.");
        var entries = JsonSerializer.Deserialize<MusicExample[]>(stream) ?? throw new InvalidDataException("Missing examples.");
        foreach (var item in entries)
            if (System.IO.Path.GetFileName(item.File) != item.File || !item.File.EndsWith(".mp3", StringComparison.Ordinal)
                || item.Sha256.Length != 64 || item.Bytes <= 0 || !double.IsFinite(item.DurationSeconds) || item.DurationSeconds <= 0)
                throw new InvalidDataException("Invalid music example.");
        return Array.AsReadOnly(entries);
    }
    public static async Task VerifyAsync(MusicExample example, CancellationToken token)
    {
        using var file = System.IO.File.OpenRead(example.Path);
        if (file.Length != example.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(example.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music example checksum mismatch.");
    }
    public static async Task<MusicExampleMetadata> ReadAsync(MusicExample example, CancellationToken token)
    {
        await VerifyAsync(example, token);
        await MusicAudioRuntime.Default.PrepareAsync(token);
        var text = await MusicAudioRuntime.Default.RunAsync(true,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", example.Path], token);
        return Parse(text);
    }
    public static MusicExampleMetadata Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var values))
            foreach (var tag in values.EnumerateObject()) tags.Add(tag.Name, tag.Value.GetString() ?? "");
        return new(tags, JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
}
