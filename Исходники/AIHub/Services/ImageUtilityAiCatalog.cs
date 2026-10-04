using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ImageUtilityAiSetting(string Key, string NameKey, string DescriptionKey,
    string DefaultValue, string[]? Choices = null, bool Experimental = false);

public sealed class ImageUtilityAiArtifact
{
    public string MethodId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Repository { get; set; } = "";
    public string License { get; set; } = "";
    public List<ManagedModelArtifactFile> Files { get; set; } = [];
}

public static class ImageUtilityAiCatalog
{
    public static bool IsAi(string id) => id is "real-esrgan" or "real-cugan" or "swinir";
    public static string StorageRoot
    {
        get
        {
            var root = new StorageSettingsStore().LoadOrCreate().Models.Locations.FirstOrDefault()?.Path;
            return string.IsNullOrWhiteSpace(root) ? AppDataPaths.ComponentModelsDirectory : root;
        }
    }
    public static IReadOnlyList<ImageUtilityAiArtifact> Artifacts { get; } = Load();
    private static IReadOnlyList<ImageUtilityAiArtifact> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", "image-utility-ai-manifest.json");
        var result = JsonSerializer.Deserialize<List<ImageUtilityAiArtifact>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Image utility manifest is missing.");
        if (result.Any(a => !IsAi(a.MethodId) || a.Files.Count == 0 || a.Files.Any(f => f.Sha256.Length != 64
            || f.SizeBytes <= 0 || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")))
            throw new InvalidDataException("Image utility manifest is invalid.");
        return result;
    }
    public static IReadOnlyList<ManagedModelArtifactCard> CreateCards(string methodId, string? root = null)
    {
        if (!IsAi(methodId)) throw new ArgumentException("Unknown image AI method.", nameof(methodId));
        root ??= StorageRoot;
        return Artifacts.Where(a => a.MethodId == methodId).Select(a => new ManagedModelArtifactCard
        {
            ModelArtifactId = a.Id, DisplayName = a.Name, Family = "ImageUtility", Role = ManagedModelRoles.Tool,
            RepositoryId = a.Repository, SourcePage = "https://github.com/" + a.Repository,
            Revision = a.Revision, License = a.License, Provider = "Official release",
            IsManaged = true, CanRemoveFiles = true, ModelsRoot = root, Format = "ZIP / PyTorch",
            InstallDirectory = Path.Combine(root, "ImageUtility", a.Id, a.Revision),
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = methodId == "swinir" ? "PyTorch" : "ncnn Vulkan",
            Consumers = [new() { Id = "image-utility", DisplayName = "Image utility", Kind = "utility" }],
            Files = a.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, Purpose = f.Purpose,
                Sha256 = f.Sha256, SizeBytes = f.SizeBytes, SourceUrl = f.SourceUrl }).ToList()
        }).ToArray();
    }
    public static IReadOnlyList<ImageUtilityAiSetting> Settings(string id)
    {
        var result = new List<ImageUtilityAiSetting>();
        if (id == "real-esrgan")
        {
            result.Add(Setting("model", "EsrganModel", "realesrgan-x4plus", ["realesrgan-x4plus", "realesrgan-x4plus-anime", "realesr-animevideov3"]));
            result.Add(Setting("scale", "EsrganScale", "4", ["2", "3", "4"]));
        }
        else if (id == "real-cugan")
        {
            result.Add(Setting("model", "CuganModel", "models-se", ["models-se", "models-pro", "models-nose"]));
            result.Add(Setting("scale", "CuganScale", "2", ["2", "3", "4"]));
            result.Add(Setting("noise", "Noise", "-1", ["-1", "0", "1", "2", "3"], true));
            result.Add(Setting("syncgap", "SyncGap", "3", ["0", "1", "2", "3"], true));
        }
        else if (id == "swinir")
        {
            result.Add(Setting("scale", "SwinirScale", "4", ["2", "3", "4", "8"]));
            result.Add(Setting("tile", "SwinirTile", "256", null, true));
            result.Add(Setting("overlap", "Overlap", "32", null, true));
            result.Add(Setting("device", "SwinirDevice", "auto", null, true));
            return result;
        }
        else return result;
        result.Add(Setting("tile", "NcnnTile", "0", null, true));
        result.Add(Setting("device", "NcnnDevice", "auto", null, true));
        result.Add(Setting("threads", "Threads", "1:2:2", null, true));
        result.Add(Setting("tta", "Tta", "false", ["false", "true"], true));
        return result;
    }
    private static ImageUtilityAiSetting Setting(string key, string text, string value, string[]? choices, bool experimental = false)
        => new(key, "ImageUtility.Ai." + text, "ImageUtility.Ai." + text + "Help", value, choices, experimental);
}
