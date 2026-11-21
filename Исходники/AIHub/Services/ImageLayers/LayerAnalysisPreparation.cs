using System.IO;
using AIHub.Models;

namespace AIHub.Services.ImageLayers;

internal sealed class LayerAnalysisPreparation(ManagedModelLibraryStore library, StorageSettings storage, int parallel)
{
    private static string[] SceneModels(bool gemma, LayerMaskMethod method, bool parts, LayerAdditionalOptions? extra) => extra?.Method switch
    {
        LayerAdditionalMethod.Portrait => [ManagedModelCatalog.ImageLayerModNetId],
        LayerAdditionalMethod.Describe => [ManagedModelCatalog.ImageLayerFlorenceId, ManagedModelCatalog.ImageLayerSam2Id],
        LayerAdditionalMethod.Text => [ManagedModelCatalog.ImageLayerTextId],
        LayerAdditionalMethod.Matte or LayerAdditionalMethod.Watershed or LayerAdditionalMethod.EdgeDetails => [],
        _ when gemma => [ManagedModelCatalog.LetItOutArtifactId, ManagedModelCatalog.LetItOutVisionArtifactId],
        _ when parts => [ManagedModelCatalog.OmniGammaArtifactId, ManagedModelCatalog.ImageLayerSam2Id, ManagedModelCatalog.ImageLayerMediaPipeId],
        _ when method == LayerMaskMethod.Sam2 => [ManagedModelCatalog.OmniGammaArtifactId, ManagedModelCatalog.ImageLayerSam2Id],
        _ => [ManagedModelCatalog.OmniGammaArtifactId]
    };
    private static string[] Models(bool gemma, LayerMaskMethod method, bool parts, LayerAdditionalOptions? extra, bool text) =>
        text ? [.. SceneModels(gemma, method, parts, extra), ManagedModelCatalog.ImageLayerTextId]
            : SceneModels(gemma, method, parts, extra);

    private static ComponentCatalogEntry Cpu => ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCpuId)!;
    private static bool UsesTorch(bool gemma, LayerMaskMethod method, LayerAdditionalOptions? extra) =>
        extra?.Method == LayerAdditionalMethod.Describe || extra.UsesScene() && !gemma && method == LayerMaskMethod.Sam2;
    private static bool UsesPortrait(bool parts, LayerAdditionalOptions? extra, bool text) =>
        parts || text || extra?.Method is LayerAdditionalMethod.Matte or LayerAdditionalMethod.Portrait
            or LayerAdditionalMethod.EdgeDetails or LayerAdditionalMethod.Text;

    internal static string[] Licenses(bool gemma, LayerMaskMethod method, bool parts = false, LayerAdditionalOptions? extra = null,
        bool text = false) =>
        [
            .. Models(gemma, method, parts, extra, text),
            .. (UsesTorch(gemma, method, extra) ? Cpu.LicenseIds : []),
            .. (UsesPortrait(parts, extra, text) ? [LayerRasterRuntime.LicenseId, LayerRasterRuntime.PortraitLicenseId]
                : UsesTorch(gemma, method, extra) ? [] : new[] { LayerRasterRuntime.LicenseId })
        ];

    internal static LayerMaskMethod ChooseMethod(bool gemma) =>
        !gemma && EngineSetSelection.ActiveId == NativeRuntimeReleasePolicy.UpdatedSet
            ? LayerMaskMethod.Sam2 : LayerMaskMethod.ModelContours;

    internal async Task<bool> PrepareAsync(bool gemma, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token,
        LayerMaskMethod method = LayerMaskMethod.ModelContours, bool parts = false, LayerAdditionalOptions? extra = null,
        bool text = false)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var ids = Models(gemma, method, parts, extra, text);
        var useTorch = UsesTorch(gemma, method, extra);
        if (gemma && EngineSetSelection.ActiveId != NativeRuntimeReleasePolicy.UpdatedSet)
            throw new InvalidOperationException("LetItOut.Preparation.EngineRequired");
        var expected = ManagedModelCatalog.CreatePredefined(storage).Where(card => ids.Contains(card.ModelArtifactId)).ToArray();
        if (expected.Length != ids.Length) throw new InvalidDataException("ImageLayers.Components");
        await ComponentLicenseGate.EnsureAsync(Licenses(gemma, method, parts, extra, text), token);
        if (expected.Length > 0)
        {
            using var transferGate = await ModelStorageWorkGate.Current.EnterWorkAsync(token);
            using var acquisition = new ManagedModelAcquisitionService(library) { MaximumParallelConnections = parallel };
            var ready = true;
            foreach (var pinned in expected)
            {
                var previous = library.Load(pinned.ModelArtifactId);
                if (previous is null || previous.Revision != pinned.Revision || !Directory.Exists(previous.InstallDirectory)
                    || previous.Files.Count != pinned.Files.Count || pinned.Files.Any(file => !previous.Files.Any(existing =>
                        existing.RelativePath == file.RelativePath && existing.SizeBytes == file.SizeBytes
                        && existing.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))))
                {
                    if (string.IsNullOrEmpty(pinned.InstallDirectory)) throw new InvalidDataException("Generation.StorageRequired");
                    library.Upsert(pinned);
                }
                var card = await acquisition.VerifyAsync(pinned.ModelArtifactId, progress, token);
                if (card.Status != ManagedModelStatuses.Installed && download)
                    card = await acquisition.DownloadAsync(pinned.ModelArtifactId, progress, token);
                ready &= card.Status == ManagedModelStatuses.Installed;
            }
            if (!ready) return false;
        }
        if (extra.UsesScene() && LlamaRuntimeSelector.InstalledBundles().Count == 0)
        {
            if (!download) return false;
            if (EngineSetSelection.ActiveId == NativeRuntimeReleasePolicy.UpdatedSet)
                await VisualScenario.VisualComponents.DownloadEngineSetAsync(progress, token);
            else
                await new ComponentManager().DownloadAndInstallAsync(HardwareRuntimeCatalog.LlamaCpuId, null, token);
        }
        if (useTorch)
        {
            var manager = new ComponentManager();
            var cpu = manager.GetStatus().Single(component => component.Entry.Id == Cpu.Id);
            if (!cpu.IsAvailable && download) cpu = await manager.DownloadAndInstallAsync(Cpu.Id, null, token);
            if (!cpu.IsAvailable) return false;
        }
        var portrait = UsesPortrait(parts, extra, text);
        return useTorch && !portrait || await LayerRasterRuntime.PrepareAsync(download, progress, token, portrait);
    }
}
