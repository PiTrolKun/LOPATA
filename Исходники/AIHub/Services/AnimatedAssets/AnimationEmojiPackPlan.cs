using System.IO;

namespace AIHub.Services.AnimatedAssets;

internal static class AnimationEmojiPackPlan
{
    private const string SameExpressionMovement =
        " Keep the same emotion, eye state and gesture as in the reference. Make a small but visibly distinct variation " +
        "in the intensity of the expression, using cheek movement or a related change in the eyebrows while preserving the character and framing.";

    public static List<AnimationCard> Build(AnimationProject project, IEnumerable<string> selection, double duration,
        Func<AnimationEmojiEntry, string> name, Func<string, string> artistPrompt, bool reuseExisting = true)
    {
        if (!double.IsFinite(duration) || duration is < AnimationRenderer.MinimumDuration or > AnimationRenderer.MaximumDuration)
            throw new InvalidDataException("AnimatedAssets.Error.Duration");

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emoji in selection)
        {
            var entry = AnimationEmojiCatalog.Find(emoji)
                ?? throw new InvalidDataException("Animation.AutoPackUnknownEmoji");
            selected.Add(entry.Emoji);
        }
        if (selected.Count == 0)
            throw new InvalidDataException("Animation.AutoPackNeedSelection");

        var previous = reuseExisting ? ReusableCards(project.Cards, selected) : null;
        var referenceEmoji = project.ReferenceIsEmoji ? AnimationEmojiCatalog.Find(project.ReferenceEmoji)?.Emoji : null;
        var cards = new List<AnimationCard>(selected.Count);
        foreach (var entry in AnimationEmojiCatalog.Entries)
        {
            if (!selected.Contains(entry.Emoji))
                continue;

            var card = previous?.GetValueOrDefault(entry.Emoji)?.Snapshot() ?? new AnimationCard();
            card.Name = name(entry);
            card.TargetEmoji = entry.Emoji;
            card.Duration = duration;

            var pose = card.Poses.Count == 1 ? card.Poses[0] : new AnimationPose();
            pose.Time = duration / 2;
            if (pose.Image is not { Length: > 0 })
            {
                var prompt = artistPrompt(entry.Emoji);
                if (string.IsNullOrWhiteSpace(prompt))
                    throw new InvalidDataException("Animation.NeedDescription");
                pose.Text = entry.Emoji;
                pose.Prompt = prompt.Trim() + (referenceEmoji == entry.Emoji ? SameExpressionMovement : "");
            }
            card.Poses = [pose];
            cards.Add(card);
        }
        return cards;
    }

    public static bool IsReady(AnimationCard card) =>
        IsFrameReaction(card) && card.Poses is [{ Image.Length: > 0 }];

    private static bool IsFrameReaction(AnimationCard card) => card.Kind == "reaction" && card.Motion == "frames";

    private static Dictionary<string, AnimationCard> ReusableCards(IEnumerable<AnimationCard> cards, HashSet<string> selected)
    {
        var result = new Dictionary<string, AnimationCard>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!IsFrameReaction(card) || card.Poses.Count > 1 || AnimationEmojiCatalog.Find(card.TargetEmoji) is not { } entry
                || !selected.Contains(entry.Emoji))
                continue;
            if (!result.TryGetValue(entry.Emoji, out var held) || !IsReady(held) && IsReady(card))
                result[entry.Emoji] = card;
        }
        return result;
    }
}
