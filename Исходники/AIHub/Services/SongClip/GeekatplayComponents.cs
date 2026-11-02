using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record GeekatplayEnvironment(VideoH3Environment H3, string Source, string AnalysisLibraries,
    string Sdxl, IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>Geekatplay's FL2VA branch, reusing the pinned SDXL, encoder, VAEs and runtime installations.</summary>
internal static class GeekatplayComponents
{
    internal const string SourceId = "song-clip-geek-source", ModelId = "song-clip-geek-models";
    internal const string Revision = "0482233d865c23e888e3046c592c40cf61fd5c57";
    internal static T Read<T>(string resource)
    {
        using var stream = typeof(GeekatplayComponents).Assembly.GetManifestResourceStream("AIHub.SongClip.Geek" + resource)
            ?? throw new InvalidDataException("Missing Geekatplay manifest.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Invalid Geekatplay manifest.");
    }
    internal static long TotalBytes => Cards(Path.GetTempPath()).Sum(c => c.TotalBytes);
    internal static ManagedModelArtifactCard[] Cards(string root)
    {
        var common = VideoH3Catalog.Cards(root);
        var original = common.Single(c => c.ModelArtifactId == "video-h3-models");
        // A distinct subset identity avoids changing the older complete H3 card.
        // The same pinned originals are reused in place, without copying 30 GB.
        var shared = new ManagedModelArtifactCard {
            ModelArtifactId = "song-clip-geek-shared-models", DisplayName = "H3 · Qwen3-VL 32B / video VAE",
            Family = "Geekatplay Music-to-Video", RepositoryId = original.RepositoryId, Revision = original.Revision,
            License = original.License, SourcePage = original.SourcePage, Role = ManagedModelRoles.Tool,
            ModelsRoot = root, InstallDirectory = original.InstallDirectory, IsManaged = true, IsPinned = true,
            Origin = ManagedModelOrigins.PredefinedScenario, CanRemoveFiles = false,
            Files = original.Files.Where(f => f.RelativePath.Contains("qwen3vl_32b_")
                || f.RelativePath.EndsWith("minimax_h3_video_vae_int8_convrot.safetensors")).ToList()
        };
        var extra = Read<VideoH3Catalog.Part[]>("Components").Select(p => new ManagedModelArtifactCard {
            ModelArtifactId = p.Id, DisplayName = p.Name, Family = "Geekatplay Music-to-Video", Role = ManagedModelRoles.Tool,
            RepositoryId = p.Repository, Revision = p.Revision, License = p.License,
            SourcePage = (p.Id == SourceId ? "https://github.com/" : "https://huggingface.co/") + p.Repository + "/tree/" + p.Revision,
            ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "SongClip", p.Id, p.Revision),
            IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
            Files = p.Files.ToList(), Consumers = [new() { Id = "song_clip", DisplayName = "Geekatplay Music-to-Video", Kind = "scenario" }]
        });
        return [..common.Where(c => VideoH3RuntimeCatalog.Data.Trees.ContainsKey(c.ModelArtifactId)),
            VideoH3RuntimeCatalog.Card(root), shared, ..extra,
            ..GlitchframeComponents.Cards(root).Where(c => c.ModelArtifactId is "song-clip-glitch-sdxl" or "song-clip-glitch-libraries")];
    }
    internal static async Task<GeekatplayEnvironment> PrepareAsync(string root, int connections, CancellationToken token, Action<string> log)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var cards = Cards(root);
        await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var store = new ManagedModelLibraryStore();
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var card in cards) {
            store.Upsert(card);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("SongClip.DownloadRequired");
        }
        foreach (var card in cards.Where(c => c.ModelArtifactId is SourceId or ModelId or "song-clip-geek-shared-models" or "song-clip-glitch-sdxl"))
            await RuntimeFileAudit.VerifyAsync(card.ModelArtifactId == "song-clip-glitch-sdxl" ? "SongClip.SDXL" : "SongClip.Geek." + card.ModelArtifactId, card.InstallDirectory, card.Revision,
                card.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray(), token, log);
        var source = await PrepareSourceAsync(cards.Single(c => c.ModelArtifactId == SourceId), token);
        var analysisCard = cards.Single(c => c.ModelArtifactId == H3MontageComponents.AnalysisLibrariesId);
        var analysis = await GlitchframeComponents.InstallTreeAsync(analysisCard, GlitchframeComponents.Read<PinnedTreeFile[]>("LibraryTree"),
            Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "libraries", analysisCard.Revision), true, token, true);
        var h3 = await VideoH3Runtime.PrepareAsync(root, connections, token, log, requireReliableGpuProbe: true,
            profile: cards.Where(c => c.ModelArtifactId.StartsWith("video-h3-", StringComparison.Ordinal)).ToArray());
        return new(h3, source, Path.Combine(analysis, "Lib", "site-packages"),
            cards.Single(c => c.ModelArtifactId == "song-clip-glitch-sdxl").InstallDirectory, cards);
    }
    internal static async Task<string> PrepareSourceAsync(ManagedModelArtifactCard sourceCard, CancellationToken token)
    {
        var source = Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "geekatplay", Revision);
        Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        if (!Directory.Exists(source)) {
            var stage = source + "." + Guid.NewGuid().ToString("N") + ".installing";
            Directory.CreateDirectory(stage);
            try {
                foreach (var file in sourceCard.Files) File.Copy(RuntimeFileAudit.Contained(sourceCard.InstallDirectory, file.RelativePath), RuntimeFileAudit.Contained(stage, file.RelativePath));
                await PinnedFileTreeVerifier.VerifyAsync(stage, Read<PinnedTreeFile[]>("Tree"), token);
                token.ThrowIfCancellationRequested(); Directory.Move(stage, source); ComponentAudit.Invalidate(source);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        await PinnedFileTreeVerifier.VerifyAsync(source, Read<PinnedTreeFile[]>("Tree"), token, pythonCacheQuarantineDirectory: source + ".bytecode");
        return source;
    }

}
