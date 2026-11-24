namespace AIHub.Services.AnimatedAssets;

internal static class AnimationOutputProvenance
{
    public static OutputProvenance Create(AnimationProject project, AnimationCard? card, string operation,
        DateTimeOffset? exportedAtUtc = null)
    {
        var still = operation == "png" && card is not null;
        var models = (still ? ModelsForStill(project, card!) : ModelsForAnimation(project, card))
            .Select(model => new OutputModelUse(model.Id, model.Id, model.Revision ?? "", model.Role))
            .Distinct()
            .ToArray();
        return new OutputProvenance
        {
            Scenario = "animated-assets/" + operation,
            CreatedUtc = project.CreatedAtUtc,
            ExportedUtc = exportedAtUtc ?? DateTimeOffset.UtcNow,
            ModelHistory = models.Length > 0 ? "current-operation-recorded; prior-history-unknown" : "prior-history-unknown",
            ModelScope = still ? "selected-image" : "output",
            Models = models
        };
    }

    private static IEnumerable<AnimationModelUse> ModelsForAnimation(AnimationProject project, AnimationCard? card)
    {
        IEnumerable<AnimationCard> cards = card is null ? project.Cards : [card];
        return project.Models.Concat(cards.SelectMany(ModelsForCard));
    }

    private static IEnumerable<AnimationModelUse> ModelsForStill(AnimationProject project, AnimationCard card)
    {
        var selected = card.Poses.FirstOrDefault(pose => pose.Id == project.PngPoseId && pose.Image is not null);
        if (selected is not null) return selected.Models;
        if (card.Still is not null) return card.Models.Where(model => model.Role == "artist");
        return project.Models;
    }

    private static IEnumerable<AnimationModelUse> ModelsForCard(AnimationCard card) =>
        card.Models.Concat(card.Poses.SelectMany(pose => pose.Models));
}
