using AIHub.Models;
using AIHub.Services.ImageLayers;
using AIHub.Services.OfficeReader;
using AIHub.Services.Shorts;
using AIHub.Services.VectorGraphics;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;
using AIHub.Services.VisualScenario;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentModelPlan
{
    private static void AddOtherOperations(Dictionary<string, string[]> catalog)
    {
        var voices = VideoNarratorCatalog.Models.Where(v => v.Blocked is null).Select(v => "speech/" + v.Id).ToArray();
        catalog.Add(ScenarioNavigationCatalog.SpeechAssets, voices);
        catalog.Add(ScenarioNavigationCatalog.VideoEditor, ["analyze", "music", .. voices]);
        catalog.Add(ScenarioNavigationCatalog.OfficeEditor, ["text/selected", .. voices]);
        catalog.Add(ScenarioNavigationCatalog.Music, MusicModelSelectionCatalog.All.SelectMany(m => m.Variants)
            .Where(v => v.Connected).Select(v => MusicModelVariants.Normalize(v.Id)).Distinct().ToArray());
        catalog.Add(ScenarioNavigationCatalog.Shorts, AgentProjects.ShortsOperations.Keys.ToArray());
        catalog.Add(ScenarioNavigationCatalog.VideoUpscale, Enum.GetValues<UpscaleEngine>().Where(e => e != UpscaleEngine.Scale)
            .Select(e => e.ToString()).ToArray());
        catalog.Add(ScenarioNavigationCatalog.VectorEditor, [VectorModels.Ridge]);
        catalog.Add(ScenarioNavigationCatalog.ImageLayers, ["scene", "parts", LayerAdditionalMethods.Portrait,
            LayerAdditionalMethods.Describe, LayerAdditionalMethods.Text]);
        catalog.Add(ScenarioNavigationCatalog.Literary, ["writing-assistant", "source-search", .. LiteraryJellyInstallation.Modes.Select(m => "memory/" + m)]);
    }

    private static async Task<bool> PrepareOtherAsync(StorageSettings storage, string root, int connections, string scenario,
        string operation, bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        switch (scenario)
        {
            case ScenarioNavigationCatalog.OfficeEditor when operation == "text/selected":
                var library = new ManagedModelLibraryStore();
                var selected = new ReaderAiSettingsStore().Load().TextModel;
                var model = ReaderAiModelCatalog.TextModels(storage, library.LoadAll()).FirstOrDefault(m => m.Id == selected);
                if (model is null || !ReaderTranslationRuntime.CanAttempt(model.Card))
                    throw new InvalidOperationException("Reader.Translation.UnsupportedModel");
                using (var reader = new ReaderTextPreparation(library) { ParallelConnections = connections })
                    return await reader.PrepareAsync(model.Card, download, progress, token);
            case ScenarioNavigationCatalog.OfficeEditor or ScenarioNavigationCatalog.VideoEditor or ScenarioNavigationCatalog.SpeechAssets:
                var need = operation.StartsWith("speech/", StringComparison.Ordinal)
                    ? new VideoModelNeed("speech", [operation["speech/".Length..]]) : new(operation, []);
                using (var editor = new VideoModelPreparation { ParallelConnections = connections })
                    return await editor.PrepareAsync(root, need, download, progress, token);
            case ScenarioNavigationCatalog.Shorts:
                return await ShortsComponents.PrepareAsync(root, connections, AgentProjects.ShortsOperations[operation], download, progress, token);
            case ScenarioNavigationCatalog.VideoUpscale:
                var project = new UpscaleProject { Steps = [UpscaleCatalog.New(Enum.Parse<UpscaleEngine>(operation))] };
                using (var upscale = new UpscaleComponents(connections))
                    return await upscale.PrepareAsync(root, project, download, progress, token);
            case ScenarioNavigationCatalog.VectorEditor:
                var checking = new InlineProgress<double>(_ => progress.Report(new(operation, "", 0, 0, 0, "verifying")));
                if (!download) return (await VectorPreparation.CheckAsync(operation, checking, token)).Ready;
                var fetching = new InlineProgress<double>(percent => progress.Report(new(operation, LiteraryModelLocation.FileName,
                    (long)(Math.Clamp(percent, 0, 100) * LiteraryModelLocation.SizeBytes / 100), LiteraryModelLocation.SizeBytes, 0, "downloading")));
                await VectorPreparation.PrepareAsync(operation, true, fetching, token);
                return true;
            case ScenarioNavigationCatalog.ImageLayers:
                var method = LayerAnalysisPreparation.ChooseMethod(false);
                LayerAdditionalOptions? extra = operation switch {
                    LayerAdditionalMethods.Portrait => new(LayerAdditionalMethod.Portrait),
                    LayerAdditionalMethods.Describe => new(LayerAdditionalMethod.Describe),
                    LayerAdditionalMethods.Text => new(LayerAdditionalMethod.Text), _ => null };
                return await new LayerAnalysisPreparation(new(), storage, connections).PrepareAsync(false, download, progress,
                    token, method, parts: operation == "parts" && method == LayerMaskMethod.Sam2, extra: extra, text: operation == "parts");
            case ScenarioNavigationCatalog.Music:
                using (var music = new MusicPreparationService { Variation = operation, MaximumParallelConnections = connections })
                {
                    var cards = download ? await music.PrepareAsync(root, true, progress, token) : await music.CheckAsync(root, progress, token);
                    return music.LyricsComponentsReady && cards.Count > 0 && cards.All(c => c.Status == ManagedModelStatuses.Installed);
                }
            case ScenarioNavigationCatalog.Literary when operation == "writing-assistant":
                return await new VisualComponents(storage, connections).PrepareAdvisorAsync(VisualComponents.Literary,
                    download, progress, token) is not null;
            case ScenarioNavigationCatalog.Literary:
                var reports = new InlineProgress<LiteraryPreparationProgress>(p =>
                    progress.Report(new(operation, p.Detail, 0, 0, 0, p.Stage)));
                var runtimeReady = await GigaEmbeddingInstallation.RuntimeReadyAsync(token);
                if (download && !runtimeReady)
                {
                    await new ComponentManager().DownloadAndInstallAsync(HardwareRuntimeCatalog.PythonCpuId,
                        new InlineProgress<ComponentDownloadProgress>(p => progress.Report(new(p.ComponentId, p.ComponentId,
                            p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
                    runtimeReady = await GigaEmbeddingInstallation.RuntimeReadyAsync(token);
                }
                if (operation == "source-search")
                {
                    var options = QdrantRuntime.Shared.Options;
                    if (download)
                    {
                        if (!await QdrantInstaller.IsInstalledAsync(options, token))
                            await QdrantInstaller.InstallAsync(options, new InlineProgress<double>(_ =>
                                progress.Report(new(operation, "Qdrant", 0, 0, 0, "downloading"))), token);
                        if (!await GigaEmbeddingInstallation.ModelReadyAsync(token))
                            await GigaEmbeddingInstallation.InstallModelAsync(reports, token);
                    }
                    return runtimeReady && await QdrantInstaller.IsInstalledAsync(options, token)
                        && await GigaEmbeddingInstallation.ModelReadyAsync(token);
                }
                var mode = operation["memory/".Length..];
                if (download) await LiteraryJellyInstallation.InstallAsync(mode, reports, token);
                return runtimeReady && await LiteraryJellyInstallation.FindModelAsync(mode, token) is not null
                    && await LiteraryJellyInstallation.FindDependenciesAsync(mode, token) is not null;
            default:
                throw new ArgumentException("Unknown scenario operation.");
        }
    }
}
