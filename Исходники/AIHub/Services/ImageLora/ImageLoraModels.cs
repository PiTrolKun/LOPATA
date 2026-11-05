using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services.ImageLora;

public sealed record ImageLoraChoice(string Id, double Strength, bool Enabled = true);
public sealed record ImageLoraUse(string Id, string Revision, double Strength);
public sealed record ImageLoraSelection
{
    public bool Enabled { get; init; } = true;
    public Dictionary<string, ImageLoraChoice[]> ByModel { get; init; } = [];
    public ImageLoraSelection Snapshot() => this with { ByModel = ByModel.ToDictionary(p => p.Key, p => p.Value.ToArray()) };
    public void Validate()
    {
        if (ByModel is null || ByModel.Count > 30) throw new InvalidDataException("Generation.Lora.Invalid");
        foreach (var (model, choices) in ByModel)
            if (model.Length > 100 || choices is null || choices.Length > 64 || choices.Any(c => c is null
                || !Regex.IsMatch(c.Id, @"\A[a-z0-9-]{1,80}\z") || !double.IsFinite(c.Strength) || c.Strength is < 0 or > 2)
                || choices.DistinctBy(c => c.Id).Count() != choices.Length) throw new InvalidDataException("Generation.Lora.Invalid");
    }
}
public sealed record ImageLoraDefinition(string Id, string Name, string DescriptionRu, string DescriptionEn,
    string ModelId, string Repository, string Revision, string License, string Trigger, string[] Tags,
    ManagedModelArtifactFile[] Files, string Kind, bool Supported, string TermsText, string AuthorText)
{
    public string LicenseId => "image-lora-found-" + Id;
    public long Bytes => Files.Sum(f => f.SizeBytes);
    public ManagedModelArtifactCard Card(string root) => new() {
        ModelArtifactId = LicenseId, DisplayName = Name, Family = "Image LoRA", Role = ManagedModelRoles.Tool,
        Provider = "Hugging Face", RepositoryId = Repository, Revision = Revision, Format = "Safetensors LoRA", License = License,
        SourcePage = "https://huggingface.co/" + Repository, IsManaged = true, CanRemoveFiles = true,
        Origin = ManagedModelOrigins.PredefinedScenario, ModelsRoot = root,
        InstallDirectory = Path.Combine(root, "ImageLoRA", Id, Revision[..12]),
        Files = Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, SizeBytes = f.SizeBytes,
            Sha256 = f.Sha256, SourceUrl = f.SourceUrl, Purpose = "image_lora" }).ToList() };
}
public sealed record ImageLoraSummary(string Text, string Model, DateTimeOffset Created);
