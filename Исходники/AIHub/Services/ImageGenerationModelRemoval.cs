using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record ImageGenerationRemovalComponent(ManagedModelArtifactCard Card, ManagedModelRemovalFile[] Files);

internal sealed record ImageGenerationRemovalPlan(string ModelId, string Root,
    ImageGenerationRemovalComponent[] Components, string[] Preserved)
{
    public long Bytes => Components.SelectMany(component => component.Files).Sum(file => file.Bytes);
}

internal sealed class ImageGenerationModelRemoval(ManagedModelLibraryStore store, IModelUsageGuard guard)
{
    private const string RuntimeId = "generation-runtime";
    private readonly ManagedModelRemovalService _removal = new(store, guard);

    public static bool HasModelFiles(string root, ImageGenerationModel model)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            return ImageGenerationCatalog.CreateCards(root).Any(card =>
                model.Components.Contains(card.ModelArtifactId) && card.ModelArtifactId != RuntimeId
                && !ImageGenerationCatalog.Manifest.Models.Any(other =>
                    other.Id != model.Id && other.Components.Contains(card.ModelArtifactId))
                && Directory.Exists(card.InstallDirectory)
                && ManagedModelRemovalService.InspectScenarioFiles(card).Length > 0);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public ImageGenerationRemovalPlan Preview(string root, string modelId)
    {
        var model = ImageGenerationCatalog.Get(modelId);
        root = Path.GetFullPath(root);
        var cards = ImageGenerationCatalog.CreateCards(root).ToDictionary(card => card.ModelArtifactId);
        var inventories = cards.Values.ToDictionary(card => card.ModelArtifactId,
            card => new Lazy<ManagedModelRemovalFile[]>(() => ManagedModelRemovalService.InspectScenarioFiles(card)));
        var retained = ImageGenerationCatalog.Manifest.Models
            .Where(other => other.Id != modelId && other.Components.Intersect(model.Components).Any(id => id != RuntimeId))
            .Where(other => other.Components.Except(model.Components)
                .Any(id => id != RuntimeId && inventories[id].Value.Length > 0))
            .SelectMany(other => other.Components).ToHashSet(StringComparer.Ordinal);
        var registered = store.LoadAll();
        var components = new List<ImageGenerationRemovalComponent>();
        var preserved = new List<string>();
        foreach (var id in model.Components.Where(id => id != RuntimeId))
        {
            var files = inventories[id].Value;
            if (files.Length == 0) continue;
            var card = cards[id];
            if (retained.Contains(id) || registered.Any(other => other.ModelArtifactId != id
                && ManagedModelPathIdentity.SameDirectory(card.InstallDirectory, other.InstallDirectory)))
            {
                preserved.Add(card.DisplayName);
                continue;
            }
            _removal.EnsureScenarioRemovalAllowed(card);
            components.Add(new(card, files));
        }
        return new(modelId, root, components.ToArray(), preserved.ToArray());
    }

    public long Remove(ImageGenerationRemovalPlan confirmed)
    {
        var current = Preview(confirmed.Root, confirmed.ModelId);
        if (!current.Preserved.SequenceEqual(confirmed.Preserved)
            || !current.Components.SelectMany(component => component.Files)
                .SequenceEqual(confirmed.Components.SelectMany(component => component.Files)))
            throw new InvalidOperationException("The model files changed after confirmation.");

        long bytes = 0;
        foreach (var component in current.Components)
            bytes += _removal.RemoveReviewedScenarioFiles(component.Card, component.Files);
        return bytes;
    }
}
