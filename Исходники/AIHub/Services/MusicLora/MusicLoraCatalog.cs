using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.MusicLora;

public sealed record MusicLoraDefinition(string Id, string Ru, string En, string DescriptionRu, string DescriptionEn,
    string Variation, string Repository, string Revision, string License, string Trigger, double Minimum, double Maximum,
    double Default, string[] Tags, Dictionary<string, double> Slots, ManagedModelArtifactFile[] Files, string Format = "peft")
{
    public string Name(bool ru) => ru ? Ru : En;
    public string Description(bool ru) => ru ? DescriptionRu : DescriptionEn;
    public string LicenseId => "music-lora-" + Id;
    public long Bytes => Files.Sum(f => f.SizeBytes);
    public ManagedModelArtifactCard Card(string root) => new() { ModelArtifactId = LicenseId, DisplayName = En,
        Family = MusicModelVariants.Family(Variation), Role = ManagedModelRoles.Tool, Provider = "Hugging Face",
        RepositoryId = Repository, Revision = Revision, Format = "Safetensors LoRA", License = License,
        SourcePage = "https://huggingface.co/" + Repository, IsManaged = true, CanRemoveFiles = true,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "MusicLoRA", Id, Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, Files = Files.Select(f => new ManagedModelArtifactFile {
            RelativePath = f.RelativePath, SizeBytes = f.SizeBytes, Sha256 = f.Sha256, SourceUrl = f.SourceUrl,
            Purpose = "music_lora" }).ToList() };
}
public static class MusicLoraCatalog
{
    private static readonly Lazy<MusicLoraDefinition[]> Data = new(Load);
    private static MusicLoraDefinition[] Load()
    {
        var definitions = JsonSerializer.Deserialize<MusicLoraDefinition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "music-loras.json")))
            ?? throw new InvalidDataException("Music.Lora.Invalid");
        if (definitions.Length > 200 || definitions.DistinctBy(d => d.Id).Count() != definitions.Length) throw new InvalidDataException("Music.Lora.Invalid");
        foreach (var d in definitions) ValidateDefinition(d);
        return definitions;
    }
    internal static void ValidateDefinition(MusicLoraDefinition d)
    {
            if (d is null || d.Id is null || d.Revision is null || d.Files is null || d.Slots is null || d.Tags is null
                || d.Repository is null || !MusicLoraHubClient.Repository(d.Repository) || d.Trigger is null || d.Trigger.Length > 2000
                || d.DescriptionRu is null || d.DescriptionEn is null || d.Ru is null || d.En is null
                || d.Variation != MusicAceCatalog.Variation && d.Variation != MusicStudioRuntime.Variation
                || d.Slots.Count is < 1 or > 2 || d.Slots.Any(p => p.Key is not ("ar" or "nar" or "dit") || !double.IsFinite(p.Value) || p.Value < 0 || p.Value > 4)
                || d.Variation == MusicAceCatalog.Variation && (d.Slots.Count != 1 || !d.Slots.ContainsKey("dit"))
                || d.Variation == MusicStudioRuntime.Variation && d.Slots.ContainsKey("dit")
                || d.Files.Any(f => f is null || f.Sha256 is null || f.SourceUrl is null)
                || !System.Text.RegularExpressions.Regex.IsMatch(d.Id, @"\A[a-z0-9][a-z0-9-]{0,79}\z")
                || !System.Text.RegularExpressions.Regex.IsMatch(d.Revision, @"\A[a-f0-9]{40,64}\z")
                || !double.IsFinite(d.Minimum) || !double.IsFinite(d.Maximum) || !double.IsFinite(d.Default)
                || d.Minimum < 0 || d.Maximum > 1 || d.Minimum > d.Default || d.Default > d.Maximum
                || d.Format is not ("peft" or "audio-sliders-peft-v1")
                || (d.Format == "audio-sliders-peft-v1" && d.Variation != MusicAceCatalog.Variation)
                || d.Files.Length is < 1 or > 10 || d.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != d.Files.Length
                || d.Files.Any(f => string.IsNullOrWhiteSpace(f.RelativePath) || Path.GetFileName(f.RelativePath) != f.RelativePath || f.RelativePath.Contains(':')
                    || f.SizeBytes <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(f.Sha256, @"\A[a-f0-9]{64}\z")
                    || !f.SourceUrl.StartsWith("https://huggingface.co/" + d.Repository + "/resolve/" + d.Revision + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("Music.Lora.Invalid");
    }
    internal static IReadOnlyList<MusicLoraDefinition> Bundled => Data.Value;
    public static IReadOnlyList<MusicLoraDefinition> All => Data.Value.Concat(MusicLoraSearchStore.Registered()
        .Where(d => !Data.Value.Any(b => b.Id == d.Id))).ToArray();
    public static MusicLoraDefinition Find(string id) => All.SingleOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Music.Lora.Invalid");
    public static MusicLoraDefinition[] For(string variation) => variation == MusicStudioRuntime.Variation && EngineSetSelection.ActiveId != "002"
        ? [] : All.Where(x => x.Variation == variation).ToArray();
    public static MusicLoraUse[] Capture(MusicLoraSelection selection, string variation)
    {
        selection.Validate();
        if (!selection.Enabled) return [];
        return selection.Choices(variation).Where(c => c.Strength > 0).Select(c => {
            var d = Find(c.Id);
            if (!For(variation).Any(x => x.Id == c.Id)) throw new InvalidDataException("Music.Lora.Incompatible");
            var u = new MusicLoraUse(d.Id, d.Revision, c.Strength); Validate([u], variation); return u;
        }).ToArray();
    }
    public static void Validate(IReadOnlyList<MusicLoraUse> uses, string variation)
    {
        if (uses is null || uses.Count > 32 || uses.Any(x => x is null) || uses.DistinctBy(x => x.Id).Count() != uses.Count) throw new InvalidDataException("Music.Lora.Invalid");
        foreach (var u in uses) {
            var d = Find(u.Id);
            if (d.Variation != variation || d.Revision != u.Revision || !double.IsFinite(u.Strength)
                || u.Strength < d.Minimum || u.Strength > d.Maximum) throw new InvalidDataException("Music.Lora.Incompatible");
        }
    }
    public static string[] Reasons(MusicLoraDefinition definition, string wishes) => definition.Tags
        .Where(t => System.Text.RegularExpressions.Regex.IsMatch(wishes, @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(t)
            + (t.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё') ? @"\p{L}*" : "") + @"(?![\p{L}\p{N}])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)).Distinct().ToArray();
    public static string EffectiveStyle(string style, IReadOnlyList<MusicLoraUse> uses) => uses.Count == 0 ? style
        : string.Join(", ", uses.Select(u => Find(u.Id).Trigger).Where(t => t.Length > 0).Distinct().Append(style).Where(t => t.Length > 0));
}
