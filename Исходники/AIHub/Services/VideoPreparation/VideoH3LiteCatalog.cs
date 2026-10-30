using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Pinned experimental bundle with a separate generation profile.</summary>
internal static class VideoH3LiteCatalog
{
    internal const string ModelId = "minimax-h3-w4a8";
    internal const string Name = "MiniMax H3 · W4A8 + Qwen 4B";
    internal static IReadOnlyList<VideoH3Catalog.Part> Parts { get; } = Load();
    private static VideoH3Catalog.Part[] Load()
    {
        using var stream = typeof(VideoH3LiteCatalog).Assembly.GetManifestResourceStream("AIHub.VideoH3LiteManifest")
            ?? throw new InvalidDataException("Missing H3 W4A8 acquisition manifest.");
        var parts = JsonSerializer.Deserialize<VideoH3Catalog.Part[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid H3 W4A8 acquisition manifest.");
        if (parts.Length == 0 || parts.Select(p => p.Id).Distinct().Count() != parts.Length
            || parts.Any(p => !p.Id.StartsWith("video-h3lite-", StringComparison.Ordinal) || p.Revision.Length != 40
                || !p.Revision.All(Uri.IsHexDigit) || p.Files.Length == 0 || p.Files.Any(f => f.SizeBytes <= 0
                    || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)
                    || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || !f.SourceUrl.Contains(p.Revision, StringComparison.Ordinal))))
            throw new InvalidDataException("Incomplete H3 W4A8 acquisition pins.");
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
            ModelArtifactId = p.Id, Family = Name, DisplayName = p.Name.Replace(" (not installed)", "", StringComparison.Ordinal), Role = ManagedModelRoles.Tool,
            Provider = "ComfyUI", RepositoryId = p.Repository, Revision = p.Revision, Format = "pinned-inputs",
            License = p.License, SourcePage = (p.Id == "video-h3lite-source-clipproj" ? "https://github.com/" : "https://huggingface.co/")
                + p.Repository + "/tree/" + p.Revision,
            IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
            ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "H3-W4A8", p.Id, p.Revision),
            Files = p.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath, SourceUrl = f.SourceUrl,
                SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Purpose = "h3lite-acquisition", IsRequired = true }).ToList(),
            Consumers = [new() { Id = "video_generation", DisplayName = Name, Kind = "scenario" }]
        }).ToArray();
    }
}
