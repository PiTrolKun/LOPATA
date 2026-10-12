using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class MusicPoetryProfileInstallation
{
    public static async Task<DebugModelInfo> DownloadAsync(MusicPoetryModelCandidate candidate, StorageSettings settings,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        var profile = MusicPoetryProfileCatalog.Find(candidate.Id) ?? throw new InvalidOperationException("Music.Poetry.ModelUnsupported");
        var root = settings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrEmpty(p))
            ?? throw new IOException("Music.StorageRequired");
        var store = new ManagedModelLibraryStore();
        var card = store.Upsert(MusicPoetryProfileCatalog.Card(profile, root));
        using var acquisition = new ManagedModelAcquisitionService(store)
        { MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
        var result = await acquisition.DownloadAsync(card.ModelArtifactId, progress, token);
        if (result.Status != ManagedModelStatuses.Installed) throw new InvalidDataException(result.LastError);
        var model = MusicPoetryProfileCatalog.Installed(profile, root) ?? throw new InvalidDataException("Music.Poetry.ModelMissing");
        if (!MusicPoetryModelCatalog.IsCompatible(model)) throw new InvalidDataException("Music.Poetry.ModelUnsupported");
        return model;
    }

}
