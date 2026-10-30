using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public static partial class VideoWorld
{
    // Runtime choices, provenance schema defaults and edit history do not change a visual description.
    public static bool MatchesSnapshot(string snapshot, IEnumerable<VideoWorldCard> cards)
    {
        var saved = JsonSerializer.Deserialize<List<VideoWorldCard>>(snapshot);
        return saved is not null && saved.Select(Content).SequenceEqual(cards.Select(Content));
    }
    private static (string, string, string, string, string, string) Content(VideoWorldCard card)
        => (card.Id, card.Kind, card.Name, card.Fixed, card.Text.IdeaText, card.Text.Form);
    private static string WorkingText(IEnumerable<VideoWorldCard> cards) => string.Join("\n\n", cards.Select(c =>
        $"{c.Kind}: «{c.Name}»\n{c.Text.IdeaText}" + (c.Fixed.Length == 0 ? "" : "\nKeep: " + c.Fixed)));

    /// <summary>Restore the already approved revision only when an old snapshot changed metadata alone.</summary>
    public static bool RestoreEquivalentWorkingSource(VideoPreparationProject project)
    {
        if (project.World is not { } world || !SourceCurrent(project) || VideoTranslationHistory.IsCurrent(world.Working)) return false;
        var working = world.Working;
        var translation = VideoTranslationHistory.Selected(working);
        if (translation is null || translation.Text != working.EnglishText || string.IsNullOrWhiteSpace(working.EnglishText)
            || translation.IdeaText != working.IdeaText || translation.Form != working.Form
            || working.IdeaText != WorkingText(world.Cards)) return false;
        var source = working.IdeaHistory.FirstOrDefault(v => v.Id == translation.IdeaRevisionId);
        var snapshot = world.History.FirstOrDefault(v => v.Id == source?.ContextRevisionId);
        if (source is null || snapshot is null || source.Text != working.IdeaText || source.Form != working.Form
            || source.ContextText != world.Context || snapshot.Context != world.Context || !MatchesSnapshot(snapshot.Cards, world.Cards)) return false;
        // Preserve every revision and the original translation; select its equivalent source again.
        world.RevisionId = snapshot.Id;
        working.ContextRevisionId = source.ContextRevisionId;
        working.ContextText = source.ContextText;
        working.IdeaRevisionId = source.Id;
        return true;
    }
}
