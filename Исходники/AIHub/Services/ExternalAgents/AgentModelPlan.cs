using AIHub.Models;
using AIHub.Services.AnimatedAssets;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VisualScenario;

namespace AIHub.Services.ExternalAgents;

internal static partial class AgentModelPlan
{
    internal static IReadOnlyDictionary<string, string[]> Operations { get; } = Catalog();

    private static Dictionary<string, string[]> Catalog()
    {
        var catalog = new Dictionary<string,string[]>
        {
        [ScenarioNavigationCatalog.VideoGeneration] = [VideoH3Catalog.ModelId, VideoH3LiteCatalog.ModelId, VideoVaceCatalog.ModelId, VideoLtxCatalog.ModelId,
            .. VideoGeneratorPlan.AvailableCandidates.SelectMany(model => new[] { model.Id + "/deno", model.Id + "/deno-accelerated" })],
        [ScenarioNavigationCatalog.AnimatedAssets] = ["advisor", "artist"],
        [ScenarioNavigationCatalog.VisualScenario] = [.. VisualComponents.Advisors.Select(id => "advisor/" + id),
            .. VisualComponents.Artists.Select(id => "artist/" + id)],
        [ScenarioNavigationCatalog.SongClip] = [.. SongClipVariantCatalog.VisibleVariants.Where(v => v.Available).Select(v => v.Id),
            AgentProjects.AcceleratedDenoOperation]
        };
        AddOtherOperations(catalog);
        AddAssistantOperations(catalog);
        return catalog;
    }

    internal static void Validate(string scenario, string operation)
    {
        if (!Operations.TryGetValue(scenario, out var supported) || !supported.Contains(operation, StringComparer.Ordinal))
            throw new ArgumentException("Unknown model operation for this scenario. See modelOperations in lopata_info.");
    }

    internal static async Task<bool> PrepareAsync(StorageSettings storage, int connections, string scenario, string operation,
        bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        Validate(scenario, operation);
        var root = storage.Models.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path))?.Path;
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Generation.StorageRequired");
        using var audit = ComponentAudit.CaptureOperation();
        switch (scenario)
        {
            case ScenarioNavigationCatalog.AnimatedAssets:
                var animation = new AnimationModelService(storage, connections, key => key);
                return await animation.PrepareSelectedModelAsync(operation == "artist", download, progress, token);
            case ScenarioNavigationCatalog.VisualScenario:
                var visual = new VisualComponents(storage, connections);
                var id = operation[(operation.IndexOf('/') + 1)..];
                return operation.StartsWith("artist/", StringComparison.Ordinal)
                    ? await visual.PrepareArtistAsync(id, true, download, progress, token) is not null
                    : await visual.PrepareAdvisorAsync(id, download, progress, token) is not null;
            case ScenarioNavigationCatalog.SongClip:
                var accelerated = operation == AgentProjects.AcceleratedDenoOperation;
                var variant = accelerated ? SongClipDeno.Mode : operation;
                using (var plan = new VideoH3Preparation(root, cards: SongClipVariantCatalog.Cards(variant, root, accelerated)) { ParallelConnections = connections })
                    return await plan.PrepareAsync(download, progress, token);
            case ScenarioNavigationCatalog.VideoGeneration:
                switch (operation)
                {
                    case VideoVaceCatalog.ModelId:
                        using (var plan = new VideoVacePreparation(root) { ParallelConnections = connections })
                            return await plan.PrepareAsync(download, progress, token);
                    case VideoLtxCatalog.ModelId:
                        using (var plan = new VideoLtxPreparation(root) { ParallelConnections = connections })
                            return await plan.PrepareAsync(download, progress, token);
                    default:
                        var parts = operation.Split('/', 2);
                        var lite = parts[0] == VideoH3LiteCatalog.ModelId;
                        ManagedModelArtifactCard[]? cards = lite ? VideoH3LiteCatalog.Cards(root).ToArray() : null;
                        if (parts.Length == 2)
                        {
                            var profile = new VideoH3DenoProfile(parts[1] == AgentProjects.AcceleratedDenoOperation);
                            cards = [.. (cards ?? VideoH3Catalog.Cards(root).ToArray()), .. profile.AdditionalCards(root, lite)];
                        }
                        using (var plan = new VideoH3Preparation(root, cards: cards) { ParallelConnections = connections })
                            return await plan.PrepareAsync(download, progress, token);
                }
            case ScenarioNavigationCatalog.GameSounds or ScenarioNavigationCatalog.OneC or ScenarioNavigationCatalog.CharacterKnowledge or ScenarioNavigationCatalog.NeuroConstructor:
                return await PrepareAssistantAsync(storage, root, connections, scenario, operation, download, progress, token);
            default:
                return await PrepareOtherAsync(storage, root, connections, scenario, operation, download, progress, token);
        }
    }
}
