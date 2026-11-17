using System.IO;
using AIHub.Models;

namespace AIHub.Services.VisualScenario;

internal sealed record VisualModelRemovalPlan(string Name, string[] Components, string[] Preserved, long Bytes,
    Func<long> Remove);

internal sealed partial class VisualComponents
{
    internal bool HasRemovableFiles(string id, bool artist)
    {
        try
        {
            return artist && id != VisualSenseNova.Id
                ? ImageGenerationModelRemoval.HasModelFiles(Root, ImageGenerationCatalog.Get(id))
                : RemovableAdvisor(id) is { } card && ManagedModelRemovalService.InspectScenarioFiles(card).Length > 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private ManagedModelArtifactCard? RemovableAdvisor(string id)
    {
        var card = AdvisorCard(id);
        return card is { IsManaged: true, CanRemoveFiles: true, IsSystem: false, IsPinned: false,
            Origin: ManagedModelOrigins.PredefinedScenario } ? card : null;
    }

    internal VisualModelRemovalPlan? ReviewRemoval(string id, bool artist)
    {
        if (artist && id != VisualSenseNova.Id)
        {
            var service = new ImageGenerationModelRemoval(_library, usageGuard ?? new NullModelUsageGuard());
            var plan = service.Preview(Root, id);
            if (plan.Components.Length == 0) return null;
            return new(ImageGenerationCatalog.DisplayName(id), plan.Components.Select(item => item.Card.DisplayName).ToArray(),
                plan.Preserved, plan.Bytes, () => service.Remove(plan));
        }

        if (RemovableAdvisor(id) is not { } card) return null;
        var removal = new ManagedModelRemovalService(_library, usageGuard);
        removal.EnsureScenarioRemovalAllowed(card);
        var files = ManagedModelRemovalService.InspectScenarioFiles(card);
        if (files.Length == 0) return null;
        return new(AdvisorName(id), [card.DisplayName], [], files.Sum(file => file.Bytes),
            () => removal.RemoveReviewedScenarioFiles(card, files));
    }
}
