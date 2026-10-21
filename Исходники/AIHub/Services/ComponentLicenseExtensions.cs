using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Additive, immutable downloaded-component terms. Bundled entries always take precedence.</summary>
internal static class ComponentLicenseExtensions
{
    private sealed record Document(ComponentLicenseEntry Entry, string Text);
    private static readonly object Gate = new();
    private static string DirectoryPath => Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "Extensions");
    private static bool Safe(string id) => Regex.IsMatch(id, @"\Amusic-lora-found-[a-f0-9]{32}\z");
    internal static void Register(ComponentLicenseEntry entry, string text)
    {
        if (!Safe(entry.Id) || entry.Basic || entry.Texts.Length != 1 || entry.Texts[0] != "extensions/" + entry.Id
            || text.Length is < 1 or > 300000) throw new InvalidDataException("Music.Lora.Invalid");
        lock (Gate) {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, entry.Id + ".json");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var value = new Document(entry, text);
            if (File.Exists(path)) {
                var old = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (old?.Entry.Terms != entry.Terms || old.Text != text) throw new InvalidDataException("Music.Lora.Invalid");
                return;
            }
            MusicLora.MusicLoraSearchStore.Write(path, value);
        }
    }
    private static Document[] Read()
    {
        lock (Gate) {
            if (!Directory.Exists(DirectoryPath)) return [];
            var result = new List<Document>();
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "music-lora-found-*.json").Take(4000)) {
                try {
                    if (new FileInfo(path).Length > 500000) continue;
                    var d = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                    if (d?.Entry is not null && d.Entry.Id is not null && d.Text is not null && Safe(d.Entry.Id) && !d.Entry.Basic
                        && Path.GetFileNameWithoutExtension(path) == d.Entry.Id && d.Entry.Terms == MusicLora.MusicLoraSearchStore.Digest(d.Text)
                        && d.Entry.Texts?.SequenceEqual(["extensions/" + d.Entry.Id]) == true) result.Add(d);
                } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
                    OwnedProcessRegistry.Log("dynamic_license_read_failed", "Music.LoRA", detail: e.GetType().Name);
                }
            }
            return result.ToArray();
        }
    }
    internal static ComponentLicenseEntry[] Entries() => Read().Select(d => d.Entry).ToArray();
    internal static string Text(string reference) => Read().SingleOrDefault(d => reference == "extensions/" + d.Entry.Id)?.Text
        ?? throw new InvalidDataException("License extension text is missing.");
}
