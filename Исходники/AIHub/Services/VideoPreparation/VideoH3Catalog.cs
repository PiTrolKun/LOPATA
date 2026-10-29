using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Pinned acquisition inputs. Downloaded source archives are not an installed Python runtime.</summary>
internal static class VideoH3Catalog
{
    internal const string ModelId = "minimax-h3";
    internal sealed record Part(string Id, string Name, string Repository, string Revision, string License, ManagedModelArtifactFile[] Files);
    internal static IReadOnlyList<Part> Parts { get; } = Load();
    private static Part[] Load()
    {
        using var stream = typeof(VideoH3Catalog).Assembly.GetManifestResourceStream("AIHub.VideoH3Manifest")
            ?? throw new InvalidDataException("Missing H3 acquisition manifest.");
        var parts = JsonSerializer.Deserialize<Part[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid H3 acquisition manifest.");
        if (parts.Length == 0 || parts.Select(p => p.Id).Distinct().Count() != parts.Length
            || parts.Any(p => p.Revision.Length != 40 || p.Files.Length == 0 || p.Files.Any(f => f.SizeBytes <= 0
                || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit) || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")))
            throw new InvalidDataException("Incomplete H3 acquisition manifest.");
        foreach (var part in parts)
            foreach (var file in part.Files) PythonWheelExtractor.RelativeDestination(file.RelativePath, wheelLayout: false);
        return parts;
    }
    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Path.IsPathFullyQualified(modelsRoot))
            throw new InvalidOperationException("VideoEditor.StorageRequired");
        var root = Path.GetFullPath(modelsRoot);
        return Parts.Select(p => new ManagedModelArtifactCard
        {
            ModelArtifactId = p.Id, Family = "MiniMax H3", DisplayName = p.Name, Role = ManagedModelRoles.Tool,
            Provider = "ComfyUI", RepositoryId = p.Repository, Revision = p.Revision, Format = "pinned-inputs",
            License = p.License, SourcePage = (p.Id is "video-h3-models" or "video-h3-upscale-model" or "video-h3-preview-model"
                ? "https://huggingface.co/" : "https://github.com/") + p.Repository + "/tree/" + p.Revision,
            IsManaged = true, IsPinned = true, IsSystem = false, CanRemoveFiles = false,
            Origin = ManagedModelOrigins.PredefinedScenario, ModelsRoot = root,
            InstallDirectory = Path.Combine(root, "Video", "H3", p.Id, p.Revision),
            Files = p.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, SourceUrl = f.SourceUrl,
                SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Purpose = "h3-acquisition", IsRequired = true }).ToList(),
            Consumers = [new() { Id = "video_generation", DisplayName = "MiniMax H3 + obvpm-timeline", Kind = "scenario" }]
        }).ToArray();
    }
}
