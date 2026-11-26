using System.IO;
using AIHub.Models;
using AIHub.Services.SongClip;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.Shorts;

internal static class ShortsComponents
{
    internal static UpscaleStep DlssStep() => new()
    {
        Engine = UpscaleEngine.DlssSr, Width = ShortsJob.Width, Height = ShortsJob.Height,
        Parameters = new() { ["upscale_dlss_mode"] = "Performance" }
    };

    private static UpscaleProject DlssProject() => new() { Steps = [DlssStep()] };

    private static ManagedModelArtifactCard[] GenerationCards(string root, ShortsGenerationProfile profile, ManagedModelLibraryStore library)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("VideoEditor.StorageRequired");
        var generation = profile.Cards(root);
        if (!profile.FromSong) return generation;
        var expected = ManagedModelCatalog.CreateQwen36Executor(root);
        ManagedModelCatalog.ReuseQwen36ExecutorDirectory(expected, library);
        return [.. generation, expected];
    }

    internal static ManagedModelArtifactCard[] Cards(string root, ShortsGenerationProfile profile, ManagedModelLibraryStore? library = null)
    {
        var generation = GenerationCards(root, profile, library ?? new());
        return profile.Deno ? generation : [.. generation, .. UpscaleComponents.Cards(root, DlssProject())];
    }

    internal static async Task<bool> NeedsPreparationAsync(string root, int connections, ShortsGenerationProfile profile, CancellationToken token)
    {
        if (SongClipAutomaticPreparation.NeedsDownload(Cards(root, profile))) return true;
        if (profile.Deno) return false;
        using var upscale = new UpscaleComponents(connections);
        return !await upscale.PrepareAsync(root, DlssProject(), false, null, token);
    }

    internal static async Task<bool> PrepareAsync(string root, int connections, ShortsGenerationProfile profile, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var library = new ManagedModelLibraryStore();
        var cards = GenerationCards(root, profile, library);
        using var acquisition = new ManagedModelAcquisitionService(library) { MaximumParallelConnections = connections };
        var ready = true;
        foreach (var card in cards)
        {
            token.ThrowIfCancellationRequested();
            var registered = library.Load(card.ModelArtifactId);
            if (registered is null || registered.Revision != card.Revision || registered.InstallDirectory != card.InstallDirectory ||
                !registered.Files.Where(file => file.IsRequired).Select(file => (file.RelativePath, file.Sha256, file.SizeBytes))
                    .SequenceEqual(card.Files.Where(file => file.IsRequired).Select(file => (file.RelativePath, file.Sha256, file.SizeBytes))))
                library.Upsert(card);
            var verified = await acquisition.VerifyAsync(card.ModelArtifactId, progress, token);
            var installed = verified.Status == ManagedModelStatuses.Installed && !SongClipAutomaticPreparation.NeedsDownload([card]);
            if (!installed && download)
                installed = (await acquisition.DownloadAsync(card.ModelArtifactId, progress, token)).Status == ManagedModelStatuses.Installed;
            ready &= installed;
        }

        if (profile.Deno) return ready;
        using var upscale = new UpscaleComponents(connections);
        var dlssReady = await upscale.PrepareAsync(root, DlssProject(), download, progress, token);
        return ready && dlssReady;
    }

    internal static Task<bool> PrepareAsync(string root, int connections, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token) =>
        PrepareAsync(root, connections, default, download, progress, token);
}
