using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record ModelStorageGroup(string Id, string Title, ManagedModelArtifactCard[] Artifacts)
{
    public long Bytes => Artifacts.Sum(card => card.TotalBytes);
    public ModelStorageVideoEncoderCopy? VideoEncoderCopy { get; init; }
}
internal sealed record ModelStorageVideoEncoderCopy(string Path, long Bytes);
internal sealed record ModelStorageScenario(string Id, string DirectionId, string TitleKey, ModelStorageGroup[] Groups);

/// <summary>Only installed bundles with relocation-aware consumers are offered for transfer.</summary>
internal static class ModelStorageCatalog
{
    private static readonly string[] CoreScenarios =
    [
        ScenarioNavigationCatalog.Images, ScenarioNavigationCatalog.ImageGeneration, ScenarioNavigationCatalog.VisualScenario,
        ScenarioNavigationCatalog.VideoGeneration, ScenarioNavigationCatalog.Music, ScenarioNavigationCatalog.GameSounds,
        ScenarioNavigationCatalog.BookConversion, ScenarioNavigationCatalog.RagBooks, ScenarioNavigationCatalog.OfficeReader,
        ScenarioNavigationCatalog.OfficeEditor, ScenarioNavigationCatalog.MailHub, ScenarioNavigationCatalog.Island,
        ScenarioNavigationCatalog.OneC
    ];

    internal static ModelStorageScenario[] Create(StorageSettings settings, ManagedModelLibraryStore store)
    {
        var root = settings.Models.Locations.Select(location => location.Path).FirstOrDefault(Path.IsPathFullyQualified);
        if (root is null) return BuildScenarios([]);
        var registered = store.LoadAll().ToDictionary(card => card.ModelArtifactId, StringComparer.OrdinalIgnoreCase);
        var imageArtifacts = ImageGenerationCatalog.CreateCards(root, store.Addresses).ToDictionary(card => card.ModelArtifactId);

        ModelStorageGroup? Group(string id, string title, IEnumerable<ManagedModelArtifactCard> components)
        {
            var cards = components.Select(card => Installed(card, registered)).ToArray();
            return cards.Length > 0 && cards.All(card => card is not null) ? new(id, title, cards.OfType<ManagedModelArtifactCard>().ToArray()) : null;
        }

        var imageGroups = ImageGenerationCatalog.Manifest.Models.Select(model => Group(model.Id, model.Name,
            model.Components.Where(id => !ImageGenerationCatalog.IsRuntimeArtifact(id)).Select(id => imageArtifacts[id])))
            .OfType<ModelStorageGroup>().ToArray();
        var miniMaxIndex = Array.FindIndex(imageGroups, group => group.Id == MiniMaxImageRuntime.ModelId);
        if (miniMaxIndex >= 0)
        {
            var group = imageGroups[miniMaxIndex];
            var video = VideoPreparation.VideoH3Catalog.Cards(root).Single(card => card.ModelArtifactId == "video-h3-models");
            imageGroups[miniMaxIndex] = group with { VideoEncoderCopy = VideoEncoderCopy(group, Registered(video, registered)) };
        }
        var visualIds = VisualScenario.VisualComponents.Artists.ToHashSet();
        var videoImageIds = ImageGenerationCatalog.VideoWorkspaceModels.Select(model => model.Id).ToHashSet();
        var core = Installed(ManagedModelCatalog.CreateCore(root), registered);
        ModelStorageGroup[] coreGroups = core is null ? [] : [new(ManagedModelCatalog.CoreArtifactId, core.DisplayName, [core])];
        var musicGroups = MusicPoetryProfileCatalog.Cards(root)
            .Select(card => Group(card.ModelArtifactId, card.DisplayName, [card])).ToList();
        musicGroups.Insert(0, Group(MusicAceCatalog.Variation, MusicModelVariants.Name(MusicAceCatalog.Variation),
            MusicAceCatalog.Cards(root).Where(card => card.ModelArtifactId != MusicAceCatalog.RuntimeId)));
        ModelStorageGroup?[] videoBundles =
        [
            Group(VideoPreparation.VideoVaceCatalog.ModelId, "Wan2.1 VACE 1.3B", VideoPreparation.VideoVaceCatalog.Cards(root)),
            Group(VideoPreparation.VideoH3LiteCatalog.ModelId, VideoPreparation.VideoH3LiteCatalog.Name,
                VideoPreparation.VideoH3LiteCatalog.Cards(root).Where(card => card.ModelArtifactId is
                    "video-h3lite-models" or "video-h3lite-encoder" or "video-h3lite-vaes" or "video-h3lite-projection")),
        ];

        var groups = new Dictionary<string, ModelStorageGroup[]>
        {
            [ScenarioNavigationCatalog.ImageGeneration] = imageGroups,
            [ScenarioNavigationCatalog.VisualScenario] = imageGroups.Where(group => visualIds.Contains(group.Id)).ToArray(),
            [ScenarioNavigationCatalog.VideoGeneration] =
                [.. imageGroups.Where(group => videoImageIds.Contains(group.Id)), .. videoBundles.OfType<ModelStorageGroup>()],
            [ScenarioNavigationCatalog.Music] = [.. musicGroups.OfType<ModelStorageGroup>(), .. imageGroups.Where(group => group.Id == "z-image")],
            [ManagedModelCatalog.CoreArtifactId] = coreGroups
        };
        foreach (var id in CoreScenarios) groups[id] = [.. (groups.GetValueOrDefault(id) ?? []), .. coreGroups];
        return BuildScenarios(groups);
    }

    private static ModelStorageScenario[] BuildScenarios(Dictionary<string, ModelStorageGroup[]> groups)
    {
        var directions = ScenarioNavigationCatalog.Nodes.Where(node => node.Kind == ScenarioNavigationKind.Direction)
            .Select((node, index) => (node.Id, index)).ToDictionary(item => item.Id, item => item.index);
        return [.. ScenarioNavigationCatalog.Nodes.Where(node => node.Kind == ScenarioNavigationKind.Scenario && node.IsAvailable)
            .Select(node => new ModelStorageScenario(node.Id, Direction(node), node.TitleKey, groups.GetValueOrDefault(node.Id) ?? []))
            .OrderBy(scenario => directions[scenario.DirectionId]),
            new(ManagedModelCatalog.CoreArtifactId, "", "ModelStorage.Core", groups.GetValueOrDefault(ManagedModelCatalog.CoreArtifactId) ?? [])];
    }

    private static string Direction(ScenarioNavigationNode node)
    {
        while (node.ParentId is { } parent) node = ScenarioNavigationCatalog.Get(parent);
        return node.Id;
    }

    private static ModelStorageVideoEncoderCopy? VideoEncoderCopy(ModelStorageGroup image, ManagedModelArtifactCard video)
    {
        foreach (var artifact in image.Artifacts)
        foreach (var encoder in artifact.Files.Where(file => file.Purpose == "llm" && file.Sha256.Length == 64))
        {
            var match = video.Files.FirstOrDefault(file => file.SizeBytes == encoder.SizeBytes
                && string.Equals(file.Sha256, encoder.Sha256, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            var path = Path.Combine(video.InstallDirectory, match.RelativePath);
            var info = new FileInfo(path);
            if (info.Exists && info.Length == encoder.SizeBytes
                && !ModelStorageAddresses.Paths.Equals(path, Path.Combine(artifact.InstallDirectory, encoder.RelativePath)))
                return new(path, encoder.SizeBytes);
        }
        return null;
    }

    private static ManagedModelArtifactCard Registered(ManagedModelArtifactCard defined, Dictionary<string, ManagedModelArtifactCard> registered) =>
        registered.TryGetValue(defined.ModelArtifactId, out var actual) && actual.Revision == defined.Revision ? actual : defined;

    private static ManagedModelArtifactCard? Installed(ManagedModelArtifactCard defined,
        Dictionary<string, ManagedModelArtifactCard> registered)
    {
        var card = Registered(defined, registered);
        if (!card.IsManaged || string.IsNullOrWhiteSpace(card.InstallDirectory) || card.Files.Count == 0) return null;
        foreach (var file in card.Files.Where(file => file.IsRequired))
        {
            var info = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
            if (!info.Exists || info.Length != file.SizeBytes) return null;
        }
        return Sidecars(card).All(path => File.Exists(Path.Combine(card.InstallDirectory, path))) ? card : null;
    }

    internal static IEnumerable<string> Sidecars(ManagedModelArtifactCard card)
    {
        if (card.ModelArtifactId == ManagedModelCatalog.CoreArtifactId) yield return CoreModelManager.ManifestFileName;
    }
}
