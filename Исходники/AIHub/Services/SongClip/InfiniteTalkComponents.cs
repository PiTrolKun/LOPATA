using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record InfiniteTalkEnvironment(ManagedPythonSelection Python, string Libraries, string Comfy,
    string Wrapper, string? Extras, IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>Immutable WanVideoWrapper InfiniteTalk inputs; H3 contributes only its shared Comfy core and wheels.</summary>
internal static class InfiniteTalkComponents
{
    internal const string SourceId = "song-clip-infinitetalk-source", ModelId = "song-clip-infinitetalk-models",
        AdapterId = "song-clip-infinitetalk-single", SharedId = "song-clip-infinitetalk-shared",
        Wav2VecId = "song-clip-infinitetalk-wav2vec", ExtrasId = "song-clip-infinitetalk-libraries";
    internal const string Revision = "088128b224242e110d3906c6750e9a3a348a659b";
    private const string CoreId = "video-h3-source-comfy";
    private static readonly SemaphoreSlim Gate = new(1);
    private sealed record RuntimeManifest(string Revision, ManagedModelArtifactFile[] Files,
        PinnedTreeFile[] Library, PinnedTreeFile[] SourceTree, string SourceFolder);
    private static readonly VideoH3Catalog.Part[] Parts = Read<VideoH3Catalog.Part[]>("Components");
    private static readonly RuntimeManifest Runtime = Read<RuntimeManifest>("Runtime", compressed: true);

    private static T Read<T>(string suffix, bool compressed = false)
    {
        using var source = typeof(InfiniteTalkComponents).Assembly.GetManifestResourceStream("AIHub.SongClip.InfiniteTalk" + suffix)
            ?? throw new InvalidDataException("Missing InfiniteTalk manifest.");
        if (!compressed) return JsonSerializer.Deserialize<T>(source) ?? throw new InvalidDataException("Invalid InfiniteTalk manifest.");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gzip) ?? throw new InvalidDataException("Invalid InfiniteTalk runtime manifest.");
    }

    internal static ManagedModelArtifactCard[] Cards(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Path.IsPathFullyQualified(modelsRoot))
            throw new InvalidOperationException("VideoEditor.StorageRequired");
        var root = Path.GetFullPath(modelsRoot);
        var cards = Parts.Select(part => new ManagedModelArtifactCard {
            ModelArtifactId = part.Id, DisplayName = part.Name, Family = "Wan2.1 · InfiniteTalk", Role = ManagedModelRoles.Tool,
            Provider = part.Id == SourceId ? "ComfyUI" : "Hugging Face", RepositoryId = part.Repository,
            Revision = part.Revision, License = part.License, Format = "pinned-inputs",
            SourcePage = (part.Id == SourceId ? "https://github.com/" : "https://huggingface.co/") + part.Repository + "/tree/" + part.Revision,
            ModelsRoot = root,
            // This independent subset reuses exactly the pinned UMT5/VAE originals. No VACE weights are acquired or loaded.
            InstallDirectory = part.Id == SharedId
                ? Path.Combine(root, "Video", "VACE-1.3B", "video-vace13-models", part.Revision)
                : Path.Combine(root, "Video", "SongClip", part.Id, part.Revision),
            IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
            Files = part.Files.ToList(), Consumers = [new() { Id = "song_clip", DisplayName = "InfiniteTalk", Kind = "scenario" }]
        }).ToList();
        cards.Add(new ManagedModelArtifactCard {
            ModelArtifactId = ExtrasId, DisplayName = "InfiniteTalk · pinned Python libraries", Family = "Wan2.1 · InfiniteTalk",
            Role = ManagedModelRoles.Tool, Provider = "PyPI", RepositoryId = "kijai/ComfyUI-WanVideoWrapper",
            Revision = Runtime.Revision, License = "Apache-2.0; MIT; BSD; upstream notices", Format = "Pinned Windows CPython 3.12 wheels",
            SourcePage = "https://github.com/kijai/ComfyUI-WanVideoWrapper/tree/" + Revision,
            ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "SongClip", ExtrasId, Runtime.Revision),
            IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
            Files = Runtime.Files.ToList(), Consumers = [new() { Id = "song_clip", DisplayName = "InfiniteTalk", Kind = "scenario" }]
        });
        cards.Add(VideoH3Catalog.Cards(root).Single(card => card.ModelArtifactId == CoreId));
        cards.Add(VideoH3RuntimeCatalog.Card(root));
        foreach (var card in cards) Validate(card);
        return cards.ToArray();
    }

    private static void Validate(ManagedModelArtifactCard card)
    {
        if (card.Revision.Length != 40 || card.Files.Count == 0
            || card.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != card.Files.Count)
            throw new InvalidDataException("Incomplete InfiniteTalk component manifest.");
        foreach (var file in card.Files) {
            if (file.SizeBytes <= 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)
                || !Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidDataException("Invalid pinned InfiniteTalk artifact.");
            PythonWheelExtractor.RelativeDestination(file.RelativePath, wheelLayout: false);
        }
    }

    internal static string FilePath(IReadOnlyList<ManagedModelArtifactCard> cards, string componentId, string fileName)
    {
        var card = cards.Single(item => item.ModelArtifactId == componentId);
        var file = card.Files.Single(item => Path.GetFileName(item.RelativePath).Equals(fileName, StringComparison.Ordinal));
        return RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
    }

    internal static async Task<InfiniteTalkEnvironment> PrepareAsync(string root, int connections, CancellationToken token, Action<string> log)
    {
        using var operation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(log);
        var cards = Cards(root);
        await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), token);
        var store = new ManagedModelLibraryStore();
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var card in cards) {
            token.ThrowIfCancellationRequested(); store.Upsert(card); log("[Prepare] " + card.DisplayName);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("SongClip.DownloadRequired");
        }
        foreach (var card in cards.Where(card => card.ModelArtifactId is ModelId or AdapterId or SharedId or Wav2VecId or SourceId))
            await RuntimeFileAudit.VerifyAsync("SongClip.InfiniteTalk." + card.ModelArtifactId, card.InstallDirectory, card.Revision,
                card.Files.Select(file => new PinnedTreeFile(file.RelativePath, file.SizeBytes, file.Sha256)).ToArray(), token, log);
        // A failed GPU discovery is not evidence for silently selecting CPU.
        var python = await ManagedPythonRuntime.ResolveAsync("auto", token, log: log, requireReliableGpuProbe: true);
        var commonRoot = Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "H3", VideoH3RuntimeCatalog.Data.Revision);
        var ownRoot = Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "infinitetalk");
        string libraries, comfy, wrapper, extras;
        await Gate.WaitAsync(token);
        try {
            var commonLibraries = cards.Single(card => card.ModelArtifactId == VideoH3RuntimeCatalog.Id);
            var commonCore = cards.Single(card => card.ModelArtifactId == CoreId);
            libraries = await Install(commonLibraries, VideoH3RuntimeCatalog.Data.Library,
                Path.Combine(commonRoot, "libraries", commonLibraries.Revision), wheels: true);
            comfy = await Install(commonCore, VideoH3RuntimeCatalog.Data.Trees[CoreId],
                Path.Combine(commonRoot, CoreId, commonCore.Revision), wheels: false);
            var wrapperCard = cards.Single(card => card.ModelArtifactId == SourceId);
            wrapper = await Install(wrapperCard, Runtime.SourceTree, Path.Combine(ownRoot, "source", wrapperCard.Revision), wheels: false);
            var extrasCard = cards.Single(card => card.ModelArtifactId == ExtrasId);
            extras = await Install(extrasCard, Runtime.Library, Path.Combine(ownRoot, "libraries", extrasCard.Revision), wheels: true);
        }
        finally { Gate.Release(); }
        return new(python, Path.Combine(libraries, "Lib", "site-packages"),
            RuntimeFileAudit.Contained(comfy, VideoH3RuntimeCatalog.Data.Folders[CoreId]),
            RuntimeFileAudit.Contained(wrapper, Runtime.SourceFolder), Path.Combine(extras, "Lib", "site-packages"), cards);

        Task<string> Install(ManagedModelArtifactCard card, PinnedTreeFile[] inventory, string target, bool wheels) =>
            GlitchframeComponents.InstallTreeAsync(card, inventory, target, wheels, token, repair: true);
    }
}
