using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageGenerationCatalog
{
    public const string BackgroundKind = "image_generation.simple";
    public static ImageGenerationManifest Manifest { get; } = Load();
    public static bool IsAvailable(string id) => Manifest.Models.Any(m => m.Id == id);
    public static ImageGenerationModel Get(string id) => Manifest.Models.SingleOrDefault(m => m.Id == id)
        ?? throw new InvalidOperationException("Generation.ModelUnavailable");
    public static string DisplayName(string id) => Manifest.Models.FirstOrDefault(m => m.Id == id)?.Name ?? id;
    private static ImageGenerationManifest Load()
    {
        using var stream = typeof(ImageGenerationCatalog).Assembly.GetManifestResourceStream("AIHub.ImageGenerationManifest")
            ?? throw new InvalidDataException("Missing image generation manifest.");
        var data = JsonSerializer.Deserialize<ImageGenerationManifest>(stream)!;
        if (data.Schema != 1 || data.Models.Length == 0 || data.Models.Select(m => m.Id).Distinct().Count() != data.Models.Length
            || data.Artifacts.Select(a => a.Id).Distinct().Count() != data.Artifacts.Length
            || data.Artifacts.Any(a => !data.Models.Any(m => m.Components.Contains(a.Id)))
            || data.Models.Any(m => m.Components.Any(id => !data.Artifacts.Any(a => a.Id == id)))
            || data.Artifacts.Any(a => a.Files.Any(f => f.Sha256.Length != 64 || f.SizeBytes <= 0
                || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var u) || u.Scheme != "https")))
            throw new InvalidDataException("Invalid image generation manifest.");
        return data;
    }
    public static IEnumerable<ManagedModelArtifactCard> CreateCards(string root) => Manifest.Artifacts.Select(a => new ManagedModelArtifactCard
    {
        ModelArtifactId = a.Id, DisplayName = a.Name, Family = "ImageGeneration", Role = ManagedModelRoles.Tool,
        RepositoryId = a.Repository, Revision = a.Revision, License = a.License,
        SourcePage = a.Id == "generation-runtime" ? "https://github.com/leejet/stable-diffusion.cpp" : "https://huggingface.co/" + a.Repository,
        Provider = a.Id == "generation-runtime" ? "GitHub" : "Hugging Face", IsManaged = true, CanRemoveFiles = true,
        Format = a.Id == "generation-runtime" ? "ZIP" : "GGUF / Safetensors", RuntimeBackend = "stable-diffusion.cpp",
        ModelsRoot = root, InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ImageGeneration", a.Id, a.Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        Consumers = [new() { Id = "simple-generation", DisplayName = "Простая генерация / Simple generation", Kind = "scenario" }],
        Files = a.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, SourceUrl = f.SourceUrl,
            SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Purpose = f.Purpose }).ToList()
    });
    public static void Validate(ImageGenerationRequest request)
    {
        var model = Get(request.ModelId);
        if (!Guid.TryParseExact(request.Id, "N", out _) || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32000
            || request.Seeds.Length is < 1 or > 4 || request.Seeds.Any(s => s < 0 || s > int.MaxValue)
            || request.Width < 256 || request.Height < 256 || request.Width > model.MaximumSide || request.Height > model.MaximumSide
            || request.Width % 64 != 0 || request.Height % 64 != 0)
            throw new ArgumentException("Generation.InvalidInput");
    }
}
