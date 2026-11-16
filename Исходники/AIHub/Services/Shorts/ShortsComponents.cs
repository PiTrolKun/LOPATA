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

    private static ManagedModelArtifactCard[] GenerationCards(string root, ManagedModelLibraryStore library)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("VideoEditor.StorageRequired");
        var montage = ShortsJob.Cards(true, root);
        var expected = ManagedModelCatalog.CreateQwen36Executor(root);
        var installed = library.Load(expected.ModelArtifactId);
        var weight = expected.Files.Single();
        if (installed is { IsManaged: true } && Path.IsPathFullyQualified(installed.InstallDirectory) &&
            Path.GetFullPath(installed.InstallDirectory).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            installed.Files.Any(file => file.IsRequired && file.RelativePath == weight.RelativePath &&
                file.SizeBytes == weight.SizeBytes && file.Sha256.Equals(weight.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            expected.InstallDirectory = installed.InstallDirectory;
        }
        return [.. montage, expected];
    }

    internal static ManagedModelArtifactCard[] Cards(string root, ManagedModelLibraryStore? library = null) =>
        [.. GenerationCards(root, library ?? new()), .. UpscaleComponents.Cards(root, DlssProject())];

    internal static async Task<bool> NeedsPreparationAsync(string root, int connections, CancellationToken token)
    {
        if (SongClipAutomaticPreparation.NeedsDownload(Cards(root))) return true;
        using var upscale = new UpscaleComponents(connections);
        return !await upscale.PrepareAsync(root, DlssProject(), false, null, token);
    }

    internal static async Task<bool> PrepareAsync(string root, int connections, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var library = new ManagedModelLibraryStore();
        var cards = GenerationCards(root, library);
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

        using var upscale = new UpscaleComponents(connections);
        var dlssReady = await upscale.PrepareAsync(root, DlssProject(), download, progress, token);
        return ready && dlssReady;
    }
}
