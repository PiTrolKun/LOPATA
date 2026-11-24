using System.IO;
using AIHub.Models;
using AIHub.Services.GameSounds;
using AIHub.Services.OfficeReader;
using AIHub.Services.OneC;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentModelPlan
{
    private static void AddAssistantOperations(Dictionary<string, string[]> operations)
    {
        operations[ScenarioNavigationCatalog.GameSounds] = [ManagedModelCatalog.CoreArtifactId,
            ManagedModelCatalog.Qwen36ExecutorArtifactId, ManagedModelCatalog.LetItOutArtifactId];
        operations[ScenarioNavigationCatalog.OneC] = ["selected_text_model", "bsl_checker"];
    }

    private static async Task<bool> PrepareAssistantAsync(StorageSettings storage, string root, int connections,
        string scenario, string operation, bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        switch (scenario)
        {
            case ScenarioNavigationCatalog.GameSounds:
                var soundModel = GameSoundModels.Available(storage, new()).Single(c => c.ModelArtifactId == operation);
                return await GameSoundModels.PrepareAsync(soundModel, GameSoundPlacement.Auto, connections, download, progress, token);
            case ScenarioNavigationCatalog.OneC when operation == "selected_text_model":
                var selected = new ReaderAiSettingsStore(Path.Combine(AppDataPaths.BaseDirectory, "OneC", "ai-settings.json")).Load().TextModel;
                var library = new ManagedModelLibraryStore();
                var model = ReaderAiModelCatalog.TextModels(storage, library.LoadAll()).SingleOrDefault(m => m.Id == selected)
                    ?? throw new InvalidDataException("OneC.NoModel");
                using (var preparation = new ReaderTextPreparation(library) { ParallelConnections = connections })
                    return await preparation.PrepareAsync(model.Card, download, progress, token);
            case ScenarioNavigationCatalog.OneC when operation == "bsl_checker":
                var checker = new OneCBslRuntime();
                if (!download && !checker.Present) return false;
                await checker.PrepareAsync(download, new Progress<ComponentDownloadProgress>(p =>
                    progress.Report(new(p.ComponentId, "BSL Language Server", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
                return true;
            default:
                throw new ArgumentException("Unknown assistant operation.");
        }
    }
}
