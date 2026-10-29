using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal static class VideoH3RuntimeCatalog
{
    internal const string Id = "video-h3-runtime-libraries";
    internal sealed record Manifest(string Revision, ManagedModelArtifactFile[] Files, PinnedTreeFile[] Library,
        Dictionary<string, PinnedTreeFile[]> Trees, Dictionary<string, string> Folders);
    internal static Manifest Data { get; } = Read();
    private static Manifest Read()
    {
        using var source = typeof(VideoH3RuntimeCatalog).Assembly.GetManifestResourceStream("AIHub.VideoH3RuntimeManifest")!;
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Manifest>(gzip) ?? throw new InvalidDataException("Missing pinned H3 runtime.");
    }
    internal static ManagedModelArtifactCard Card(string modelsRoot) => new()
    {
        ModelArtifactId = Id, DisplayName = "H3 · ComfyUI Python libraries", Family = "MiniMax H3", Role = ManagedModelRoles.Tool,
        Provider = "PyPI", RepositoryId = "Comfy-Org/ComfyUI", Revision = Data.Revision, Format = "Pinned Windows CPython 3.12 wheels",
        License = "Apache-2.0; MIT; BSD; upstream notices", SourcePage = "https://github.com/Comfy-Org/ComfyUI",
        ModelsRoot = modelsRoot, InstallDirectory = Path.Combine(modelsRoot, "Video", "H3", Id, Data.Revision),
        IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
        Files = Data.Files.ToList(), Consumers = [new() { Id = "video_generation", DisplayName = "MiniMax H3", Kind = "scenario" }]
    };
}
