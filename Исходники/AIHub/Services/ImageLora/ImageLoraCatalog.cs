using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Services.MusicLora;

namespace AIHub.Services.ImageLora;

public static class ImageLoraCatalog
{
    private static readonly Lazy<ImageLoraDefinition[]> Data = new(() => {
        using var stream = typeof(ImageLoraCatalog).Assembly.GetManifestResourceStream("AIHub.ImageLoras")!;
        var data = JsonSerializer.Deserialize<ImageLoraDefinition[]>(stream) ?? throw new InvalidDataException("Generation.Lora.Invalid");
        foreach (var d in data) Validate(d);
        if (data.DistinctBy(d => d.Id).Count() != data.Length) throw new InvalidDataException("Generation.Lora.Invalid");
        return data;
    });
    public static IReadOnlyList<ImageLoraDefinition> Bundled => Data.Value;
    public static ImageLoraDefinition[] All => [.. Bundled, .. ImageLoraStore.Registered().Where(d => !Bundled.Any(b => b.Id == d.Id))];
    public static ImageLoraDefinition[] For(string model) => All.Where(d => d.ModelId == model && (d.Supported || d.Kind == "collection")).ToArray();
    public static ImageLoraDefinition Find(string id) => All.SingleOrDefault(d => d.Id == id) ?? throw new InvalidDataException("Generation.Lora.Invalid");
    internal static void Validate(ImageLoraDefinition d)
    {
        if (d is null || !Regex.IsMatch(d.Id ?? "", @"\A[a-f0-9]{32}\z") || !MusicLoraHubClient.Repository(d.Repository ?? "")
            || !Regex.IsMatch(d.Revision ?? "", @"\A[a-f0-9]{40,64}\z") || d.Files is null || d.Tags is null
            || d.Trigger is null || d.Trigger.Length > 1000 || d.Trigger.IndexOfAny(['<', '>', '\r', '\n']) >= 0
            || d.TermsText is null || d.TermsText.Length > 300000 || d.AuthorText is null || d.AuthorText.Length > 28000
            || d.DescriptionRu is null || d.DescriptionEn is null || d.Name is null || d.ModelId is null
            || d.Files.Length > 32 || d.Files.Any(f => f is null) || d.Files.DistinctBy(f => f.RelativePath).Count() != d.Files.Length
            || d.Supported && (d.Kind != "lora" || d.Files.Length != 1 || d.ModelId is not ("z-image" or "krea"))) Fail();
        foreach (var f in d.Files) {
            if (f is null || f.SizeBytes <= 8 || !Regex.IsMatch(f.Sha256 ?? "", @"\A[a-f0-9]{64}\z")
                || f.RelativePath != "adapter.safetensors" || f.SourceUrl is null
                || !f.SourceUrl.StartsWith("https://huggingface.co/" + d.Repository + "/resolve/" + d.Revision + "/", StringComparison.Ordinal)) Fail();
        }
    }
    public static ImageLoraUse[] Capture(ImageLoraSelection selection, string model)
    {
        selection.Validate(); if (!selection.Enabled) return [];
        var uses = (selection.ByModel.GetValueOrDefault(model) ?? []).Where(c => c.Enabled && c.Strength > 0)
            .Select(c => new ImageLoraUse(c.Id, Find(c.Id).Revision, c.Strength)).ToArray(); ValidateUses(uses, model); return uses;
    }
    public static void ValidateUses(ImageLoraUse[] uses, string model)
    {
        if (uses is null || uses.Length > 32 || uses.Any(u => u is null) || uses.DistinctBy(u => u.Id).Count() != uses.Length) Fail();
        foreach (var u in uses) { var d = Find(u.Id);
            if (!d.Supported || d.ModelId != model || d.Revision != u.Revision || !double.IsFinite(u.Strength) || u.Strength is <= 0 or > 2) Fail(); }
    }
    public static string EffectivePrompt(string prompt, ImageLoraUse[] uses)
    {
        if (uses.Length == 0) return prompt;
        var triggers = uses.Select(u => Find(u.Id).Trigger).Where(t => t.Length > 0 && !prompt.Contains(t, StringComparison.OrdinalIgnoreCase)).Distinct();
        return string.Join(", ", triggers.Append(prompt)) + "\n" + string.Join(" ", uses.Select(u =>
            "<lora:" + u.Id + "/" + u.Revision[..12] + "/adapter:" + u.Strength.ToString("0.####", CultureInfo.InvariantCulture) + ">"));
    }
    public static void ValidateRawPrompt(string prompt)
    {
        if (Regex.IsMatch(prompt, @"<(?:lora|lyco):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Generation.Lora.UsePicker");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new InvalidDataException("Generation.Lora.Incompatible");
}
