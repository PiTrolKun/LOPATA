using System.IO;
using System.Text.Json;
using AIHub.Services.MusicLora;

namespace AIHub.Services.ImageLora;

internal static class ImageLoraStore
{
    internal static string Root => Path.Combine(AppDataPaths.BaseDirectory, "ImageLoRA");
    internal static string Digest(string value) => MusicLoraSearchStore.Digest(value);
    internal static void Write<T>(string path, T value) => MusicLoraSearchStore.Write(path, value);
    internal static T? Read<T>(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 32000000 ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            OwnedProcessRegistry.Log("image_lora_cache_read_failed", "Image.LoRA", detail: e.GetType().Name); return default; }
    }
    internal static ImageLoraDefinition[] Registered()
    {
        var dir = Path.Combine(Root, "Catalog"); if (!Directory.Exists(dir)) return [];
        var result = new List<ImageLoraDefinition>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json")) {
            var d = Read<ImageLoraDefinition>(path); if (d is null) continue;
            try { ImageLoraCatalog.Validate(d); if (Path.GetFileNameWithoutExtension(path) == d.Id) result.Add(d); }
            catch (InvalidDataException) { OwnedProcessRegistry.Log("image_lora_cache_invalid", "Image.LoRA"); }
        }
        return result.ToArray();
    }
    internal static void Register(ImageLoraDefinition d)
    {
        ImageLoraCatalog.Validate(d); if (!d.Supported) throw new InvalidDataException("Generation.Lora.Incompatible");
        ComponentLicenseExtensions.Register(new() { Id = d.LicenseId, Name = d.Name, Version = d.Revision,
            Author = d.Repository.Split('/')[0], License = d.License, Source = "https://huggingface.co/" + d.Repository + "/tree/" + d.Revision,
            Checked = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), Terms = Digest(d.TermsText), Texts = ["extensions/" + d.LicenseId],
            Ru = "Найденная LoRA изображений. Изучите условия автора. Качество результата не проверено.",
            En = "Discovered image LoRA. Review the author's terms. Output quality has not been tested." }, d.TermsText);
        var path = Path.Combine(Root, "Catalog", d.Id + ".json");
        if (File.Exists(path)) {
            var old = Read<ImageLoraDefinition>(path);
            if (old is null || JsonSerializer.Serialize(old) != JsonSerializer.Serialize(d)) throw new InvalidDataException("Generation.Lora.Invalid");
        } else Write(path, d);
    }
    internal static string SummaryPath(ImageLoraDefinition d, bool ru) => Path.Combine(Root, "Descriptions", Digest(d.Id + d.Revision + (ru ? ":ru:v2" : ":en:v2")) + ".json");
}
