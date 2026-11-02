using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record H3MontageEnvironment(H3MusicEnvironment Motion, string AnySongSource, string AnalysisLibraries,
    IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>The Any Song editing mode shares H3 and adds its pinned MIT source pack.</summary>
internal static class H3MontageComponents
{
    internal const string SourceId = "song-clip-h3-any-song-source";
    internal const string SourceRevision = "37b72ef49d9128e6853f2b7a2d1fa56bc65cfeb1";
    internal const string AnalysisLibrariesId = "song-clip-glitch-libraries";
    internal static VideoH3Catalog.Part[] Parts { get; } = Read<VideoH3Catalog.Part[]>("AIHub.SongClip.MontageComponents");
    internal static PinnedTreeFile[] SourceTree { get; } = Read<PinnedTreeFile[]>("AIHub.SongClip.MontageTree");
    internal static long TotalBytes => H3MusicComponents.TotalBytes + Parts.Sum(part => part.Files.Sum(file => file.SizeBytes))
        + GlitchframeComponents.Parts.Single(part => part.Id == AnalysisLibrariesId).Files.Sum(file => file.SizeBytes);

    private static T Read<T>(string resource)
    {
        using var source = typeof(H3MontageComponents).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Missing pinned H3 montage manifest.");
        return JsonSerializer.Deserialize<T>(source) ?? throw new InvalidDataException("Invalid H3 montage manifest.");
    }

    internal static IReadOnlyList<ManagedModelArtifactCard> BaseCards(string root) => H3MusicComponents.Cards(root);

    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Model storage must be selected.");
        var additional = Parts.Select(part => new ManagedModelArtifactCard
        {
            ModelArtifactId = part.Id, Family = "H3 Any Song Montage", DisplayName = part.Name,
            Role = ManagedModelRoles.Tool, Provider = "ComfyUI", RepositoryId = part.Repository,
            Revision = part.Revision, License = part.License,
            SourcePage = "https://github.com/" + part.Repository + "/tree/" + part.Revision,
            IsManaged = true, IsPinned = true, CanRemoveFiles = false,
            Origin = ManagedModelOrigins.PredefinedScenario, ModelsRoot = root,
            InstallDirectory = Path.Combine(root, "Video", "SongClip", part.Id, part.Revision), Files = part.Files.ToList(),
            Consumers = [new() { Id = "song_clip", DisplayName = "MiniMax H3 · Montage", Kind = "scenario" }]
        });
        var analysis = GlitchframeComponents.Cards(root).Single(card => card.ModelArtifactId == AnalysisLibrariesId);
        return BaseCards(root).Append(analysis).Concat(additional).ToArray();
    }

    internal static async Task<H3MontageEnvironment> PrepareAsync(string root, int connections,
        CancellationToken token, Action<string> log)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var cards = Cards(root);
        await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), token);
        var sourceCard = cards.Single(card => card.ModelArtifactId == SourceId);
        var analysisCard = cards.Single(card => card.ModelArtifactId == AnalysisLibrariesId);
        var store = new ManagedModelLibraryStore();
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var card in new[] { sourceCard, analysisCard })
        {
            store.Upsert(card);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("SongClip.DownloadRequired");
        }
        await RuntimeFileAudit.VerifyAsync("SongClip.H3Montage.Source", sourceCard.InstallDirectory,
            sourceCard.Revision, SourceTree, token, log);
        var source = await PrepareSourceAsync(sourceCard, token, log);
        var analysis = await GlitchframeComponents.InstallTreeAsync(analysisCard,
            GlitchframeComponents.Read<PinnedTreeFile[]>("LibraryTree"),
            Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "libraries", analysisCard.Revision),
            wheels: true, token, repair: true);
        // No acquisition is started here. Shared H3 and Motion components use
        // their established licence, audit, installation and GPU checks.
        var motion = await H3MusicComponents.PrepareAsync(root, connections, token, log);
        return new(motion, source, Path.Combine(analysis, "Lib", "site-packages"), cards);
    }

    private static async Task<string> PrepareSourceAsync(ManagedModelArtifactCard card,
        CancellationToken token, Action<string> log)
    {
        var target = Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "h3-any-song", card.Revision);
        Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        if (!Directory.Exists(target))
        {
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".installing";
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in card.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var source = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
                    var destination = RuntimeFileAudit.Contained(staging, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
                await PinnedFileTreeVerifier.VerifyAsync(staging, SourceTree, token);
                token.ThrowIfCancellationRequested(); Directory.Move(staging, target); ComponentAudit.Invalidate(target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        await PinnedFileTreeVerifier.VerifyAsync(target, SourceTree, token,
            pythonCacheQuarantineDirectory: target + ".bytecode",
            cacheQuarantined: _ => log("[Audit] Quarantined unverified H3 Any Song Python bytecode."));
        return target;
    }
}
