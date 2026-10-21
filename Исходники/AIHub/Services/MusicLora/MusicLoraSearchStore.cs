using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLora;

internal sealed record MusicLoraSearchQuery(string Name, string Genre, string Style, string Mood, string Voice, string Instruments)
{
    public string Parameters => string.Join(" ", new[] { Genre, Style, Mood, Voice, Instruments }.Where(x => x.Length > 0));
    public string Text => Name + " " + Parameters;
}
internal sealed record MusicLoraSearchItem(MusicLoraDefinition Definition, string AuthorText, string TermsText,
    string Author, long? Likes, long? Downloads, DateTimeOffset Checked, string[] Sources, bool Verified = false);
internal sealed record MusicLoraSearchResult(MusicLoraSearchItem[] Items, string[] Notices, DateTimeOffset Checked, bool Cached = false, bool Stopped = false);
internal sealed record MusicLoraSummary(string Text, string Model, DateTimeOffset Created);
internal static class MusicLoraSearchStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static MusicLoraDefinition[]? _registered;
    private static string Root => Path.Combine(AppDataPaths.BaseDirectory, "MusicLoRA", "Discovery");
    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static T? Read<T>(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 32000000 ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            OwnedProcessRegistry.Log("music_lora_cache_read_failed", "Music.LoRA", detail: e.GetType().Name); return default;
        }
    }
    internal static MusicLoraDefinition[] Registered()
    {
        lock (Gate) {
            if (_registered is not null) return _registered;
            var directory = Path.Combine(Root, "Registered"); if (!Directory.Exists(directory)) return _registered = [];
            var items = new List<MusicLoraDefinition>();
            foreach (var file in Directory.EnumerateFiles(directory, "found-*.json").Take(4000)) {
                var item = Read<MusicLoraSearchItem>(file); if (item is null) continue;
                try {
                    MusicLoraCatalog.ValidateDefinition(item.Definition);
                    if (file == Path.Combine(directory, item.Definition.Id + ".json")) items.Add(item.Definition);
                } catch (InvalidDataException) { OwnedProcessRegistry.Log("music_lora_cache_invalid", "Music.LoRA"); }
            }
            return _registered = items.ToArray();
        }
    }
    internal static void Register(MusicLoraSearchItem item)
    {
        if (item.Verified) return;
        var d = item.Definition; MusicLoraCatalog.ValidateDefinition(d);
        if (!Regex.IsMatch(d.Id, @"\Afound-[a-f0-9]{32}\z")) throw new InvalidDataException("Music.Lora.Invalid");
        lock (Gate) {
            ComponentLicenseExtensions.Register(new() { Id = d.LicenseId, Name = d.En, Version = d.Revision, Author = item.Author,
                License = d.License, Source = "https://huggingface.co/" + d.Repository + "/tree/" + d.Revision,
                Checked = item.Checked.ToString("yyyy-MM-dd"), Terms = Digest(item.TermsText), Texts = ["extensions/" + d.LicenseId],
                Ru = "Найденная музыкальная LoRA. Техническая совместимость проверена; качество звучания не испытывалось. Изучите условия автора.",
                En = "Discovered music LoRA. Technical compatibility checked; audio quality has not been tested. Review the author's terms." }, item.TermsText);
            var path = Path.Combine(Root, "Registered", d.Id + ".json");
            var old = Read<MusicLoraSearchItem>(path);
            if (old is not null && JsonSerializer.Serialize(old.Definition) != JsonSerializer.Serialize(d)) throw new InvalidDataException("Music.Lora.Invalid");
            if (old is null) {
                if (Directory.Exists(Path.GetDirectoryName(path)) && Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "found-*.json").Take(4000).Count() >= 4000)
                    throw new InvalidDataException("Music.Lora.Search.RegistryFull");
                Write(path, item);
            }
            _registered = null;
        }
    }
    private static string CachePath(string variation, MusicLoraSearchQuery query) => Path.Combine(Root, "Cache", Digest(variation + JsonSerializer.Serialize(query)) + ".json");
    internal static void Cache(string variation, MusicLoraSearchQuery query, MusicLoraSearchResult result)
    {
        lock (Gate) {
            var path = CachePath(variation, query); Write(path, result);
            var directory = Path.GetFullPath(Path.GetDirectoryName(path)!);
            // Only redundant query snapshots expire. Pinned definitions, terms and summaries are retained.
            foreach (var old in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(10)) {
                if (Path.GetDirectoryName(Path.GetFullPath(old)) != directory || !Regex.IsMatch(Path.GetFileName(old), @"\A[a-f0-9]{64}\.json\z")) continue;
                Lopata.Updates.SafeUpdatePath.RejectLinks(old);
                try { File.Delete(old); } catch (IOException) { }
            }
        }
    }
    internal static MusicLoraSearchResult? Cache(string variation, MusicLoraSearchQuery query)
    {
        lock (Gate) {
            var r = Read<MusicLoraSearchResult>(CachePath(variation, query));
            if (r?.Items is null || r.Notices is null) return null;
            try { foreach (var i in r.Items) {
                MusicLoraCatalog.ValidateDefinition(i.Definition);
                if (i.Definition.Variation != variation || i.AuthorText is null || i.TermsText is null || i.Sources is null) return null;
            } }
            catch (Exception e) when (e is InvalidDataException or NullReferenceException or ArgumentException) { return null; }
            return r with { Cached = true };
        }
    }
    private static string SummaryPath(string id, bool ru) => Path.Combine(Root, "Descriptions", Digest(id + (ru ? ":ru:v1" : ":en:v1")) + ".json");
    internal static MusicLoraSummary? Summary(string id, bool ru) => Read<MusicLoraSummary>(SummaryPath(id, ru));
    internal static void Summary(string id, bool ru, MusicLoraSummary summary) { lock (Gate) Write(SummaryPath(id, ru), summary); }
}
