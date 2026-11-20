using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageGenerationCatalog
{
    public const string BackgroundKind = "image_generation.simple";
    private static readonly Dictionary<string, string> RetiredModelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen-image-21-turbo-full"] = "Qwen-Image-2.1 Turbo · BF16 (Qwen)",
        ["qwen-image-21-turbo-gguf"] = "Qwen-Image-2.1 Turbo · GGUF Q4_K_M (Unsloth)",
        ["qwen-image-21-base-int8"] = "Qwen-Image-2.1 Base · INT8 (Comfy-Org)"
    };

    public static ImageGenerationManifest Manifest { get; } = Load();
    internal static IEnumerable<ImageGenerationModel> VideoWorkspaceModels =>
        Manifest.Models.Where(m => m.Id != MiniMaxImageRuntime.ModelId);
    public static IEnumerable<ImageGenerationModel> VideoReferenceModels => VideoWorkspaceModels.Where(m => m.Family is null);
    internal static bool IsRuntimeArtifact(string id) => id is "generation-runtime" or MiniMaxImageRuntime.ArtifactId;
    public static bool IsAvailable(string id) => Manifest.Models.Any(m => m.Id == id);
    public static ImageGenerationModel Get(string id) => id == VisualScenario.VisualSenseNova.Id
        ? VisualScenario.VisualSenseNova.Model : Manifest.Models.SingleOrDefault(m => m.Id == id)
        ?? throw new InvalidOperationException("Generation.ModelUnavailable");
    public static string DisplayName(string id) => id == VisualScenario.VisualSenseNova.Id
        ? VisualScenario.VisualSenseNova.Name : Manifest.Models.FirstOrDefault(m => m.Id == id)?.Name
        ?? RetiredModelNames.GetValueOrDefault(id, id);
    internal static bool IsRetiredArtifact(string? id) => id is not null && id.StartsWith("generation-", StringComparison.OrdinalIgnoreCase)
        && RetiredModelNames.ContainsKey(id["generation-".Length..]);
    internal static bool SupportsEngineSet(ImageGenerationModel model, string engineSet) =>
        model.RequiredEngineSet is null || model.RequiredEngineSet == engineSet;

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
    public static IEnumerable<ManagedModelArtifactCard> CreateCards(string root) => CreateCards(root, ModelStorageAddresses.Current);

    internal static IEnumerable<ManagedModelArtifactCard> CreateCards(string root, ModelStorageAddresses addresses) => Manifest.Artifacts.Select(a => new ManagedModelArtifactCard
    {
        ModelArtifactId = a.Id, DisplayName = a.Name, Family = "ImageGeneration", Role = ManagedModelRoles.Tool,
        RepositoryId = a.Repository, Revision = a.Revision, License = a.License,
        SourcePage = a.Id switch
        {
            MiniMaxImageRuntime.ArtifactId => $"https://github.com/{a.Repository}/releases/tag/{a.Revision}",
            "generation-runtime" => "https://github.com/leejet/stable-diffusion.cpp",
            _ => "https://huggingface.co/" + a.Repository
        },
        Provider = IsRuntimeArtifact(a.Id) ? "GitHub" : "Hugging Face", IsManaged = true, CanRemoveFiles = true,
        Format = IsRuntimeArtifact(a.Id) ? "ZIP" : "GGUF / Safetensors", RuntimeBackend = "stable-diffusion.cpp",
        ModelsRoot = root, InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ImageGeneration", a.Id, a.Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        Consumers = [new() { Id = "simple-generation", DisplayName = "Простая генерация / Simple generation", Kind = "scenario" },
            new() { Id = "video-reference-images", DisplayName = "Референсы видео / Video references", Kind = "scenario" },
            .. (Get("z-image").Components.Contains(a.Id)
                ? new ManagedModelConsumer[] { new() { Id = MusicCoverWork.Kind, DisplayName = "Обложка песни / Song cover", Kind = "scenario" } }
                : [])],
        Files = a.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, SourceUrl = f.SourceUrl,
            SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Purpose = f.Purpose }).ToList()
    }).Select(addresses.Resolve);
    public static void Validate(ImageGenerationRequest request)
    {
        var model = Get(request.ModelId);
        if (!Guid.TryParseExact(request.Id, "N", out _) || !Enum.IsDefined(request.OutputFormat)
            || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32000
            || request.Seeds.Length is < 1 or > 4 || request.Seeds.Any(s => s < 0 || s > int.MaxValue)
            || request.Width < 256 || request.Height < 256 || request.Width > model.MaximumSide || request.Height > model.MaximumSide
            || request.Width % 64 != 0 || request.Height % 64 != 0)
            throw new ArgumentException("Generation.InvalidInput");
        if (request.ReferenceImages.Length > 0)
        {
            if (!VisualScenario.VisualComponents.Artists.Contains(request.ModelId)
                || request.ReferenceImages.Length > VisualScenario.VisualComponents.ImageLimit(request.ModelId))
                throw new ArgumentException("Generation.InvalidInput");
            if (request.ReferenceImages.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path)))
                throw new FileNotFoundException("Visual.MissingImage");
        }
    }
}
