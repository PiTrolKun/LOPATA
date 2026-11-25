using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record PiH3Runtime(string Comfy, string Extension, string Libraries);

internal static class PiH3Components
{
    internal const string ModelId = "pi_h3_dwd";
    internal const string VariantId = "pi-h3-dwd";
    internal const string AdaptersId = "song-clip-pi-h3-adapters";
    internal const string ComfyId = "song-clip-pi-h3-comfy";
    internal const string ExtensionId = "song-clip-pi-h3-extension";
    internal const string LibrariesId = "song-clip-pi-h3-libraries";
    internal const int DefaultSteps = 4;
    internal static IReadOnlyList<int> StepOptions { get; } = Array.AsReadOnly(new[] { 2, 4 });
    internal static bool SupportsSteps(int steps) => StepOptions.Contains(steps);
    internal static string AdapterFile(int steps) => SupportsSteps(steps)
        ? $"pi_h3_{steps}step_dwd_adapter_int8_convrot.safetensors"
        : throw new InvalidDataException("SongClip.InvalidSettings");

    private sealed record Part(string Id, string Name, string Repository, string Revision, string License,
        string SourcePage, ManagedModelArtifactFile[] Files, PinnedTreeFile[]? Tree = null, string? Folder = null);

    private static readonly Lazy<Part[]> Manifest = new(() =>
    {
        using var source = typeof(PiH3Components).Assembly.GetManifestResourceStream("AIHub.SongClip.PiH3Components")
            ?? throw new InvalidDataException("Missing pi-H3 manifest.");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Part[]>(gzip) ?? throw new InvalidDataException("Invalid pi-H3 manifest.");
    });
    private static readonly SemaphoreSlim InstallGate = new(1);

    internal static ManagedModelArtifactCard[] Cards(string root) => Manifest.Value.Select(part => new ManagedModelArtifactCard {
        ModelArtifactId = part.Id, DisplayName = part.Name, Family = "MiniMax H3", Role = ManagedModelRoles.Tool,
        RepositoryId = part.Repository, Revision = part.Revision, License = part.License, SourcePage = part.SourcePage,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "SongClip", part.Id, part.Revision),
        IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
        Files = part.Files.ToList(), Consumers = [new() { Id = "song_clip", DisplayName = "pi-H3 DWD", Kind = "scenario" }]
    }).ToArray();

    internal static string RuntimeDirectory(string id, string revision) =>
        Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "pi-h3", id, revision);

    internal static async Task<(VideoH3Environment H3, PiH3Runtime Pi)> PrepareAsync(
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token, Action<string> log)
    {
        var shared = cards.Single(c => c.ModelArtifactId == VideoH3RuntimeCatalog.Id);
        var libraries = await VideoH3Runtime.PrepareSharedLibrariesAsync(shared, token, log);
        var python = await ManagedPythonRuntime.ResolveAsync("auto", token, log: log, requireReliableGpuProbe: true);
        await InstallGate.WaitAsync(token);
        try
        {
            var comfy = await Install(ComfyId);
            var pi = new PiH3Runtime(comfy, await Install(ExtensionId), await Install(LibrariesId, wheels: true));
            return (new(python, libraries, comfy, "", "", "", "", cards), pi);
        }
        finally { InstallGate.Release(); }

        async Task<string> Install(string id, bool wheels = false)
        {
            var part = Manifest.Value.Single(p => p.Id == id);
            var card = cards.Single(c => c.ModelArtifactId == id);
            log("[Prepare] " + card.DisplayName);
            var root = await GlitchframeComponents.InstallTreeAsync(card,
                part.Tree ?? throw new InvalidDataException("Missing pi-H3 runtime inventory."),
                RuntimeDirectory(id, card.Revision), wheels, token, repair: true);
            return RuntimeFileAudit.Contained(root,
                part.Folder ?? throw new InvalidDataException("Missing pi-H3 runtime directory."));
        }
    }
}
