using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal static class LetItOutVision
{
    internal static ManagedModelArtifactCard Register(string modelPath, ManagedModelLibraryStore? library = null)
    {
        var store = library ?? new();
        var model = store.Load(ManagedModelCatalog.LetItOutArtifactId) ?? throw new InvalidDataException("LetItOut.NotPrepared");
        if (!Path.GetFullPath(modelPath).Equals(Path.GetFullPath(Path.Combine(model.InstallDirectory, LetItOutModel.FileName)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LetItOut.ModelChanged");
        var card = ManagedModelCatalog.CreateLetItOutVision(model.ModelsRoot); store.Upsert(card); return card;
    }
    internal static async Task<bool> PrepareAsync(string modelPath, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var store = new ManagedModelLibraryStore(); var card = Register(modelPath, store);
        await ComponentLicenseGate.EnsureAsync(card.ModelArtifactId, token);
        using var acquisition = new ManagedModelAcquisitionService(store);
        card = await acquisition.VerifyAsync(card.ModelArtifactId, progress, token);
        if (card.Status != ManagedModelStatuses.Installed && download) card = await acquisition.DownloadAsync(card.ModelArtifactId, progress, token);
        return card.Status == ManagedModelStatuses.Installed;
    }
    internal static async Task<string> VerifiedPathAsync(string modelPath, CancellationToken token)
    {
        var store = new ManagedModelLibraryStore(); var card = Register(modelPath, store);
        await ComponentLicenseGate.EnsureAsync(card.ModelArtifactId, token);
        await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, token);
        return Path.Combine(card.InstallDirectory, "mmproj-F16.gguf");
    }
}
