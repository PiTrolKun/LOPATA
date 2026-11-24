using AIHub.Services.AnimatedAssets;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private const string PreserveReactionFrame =
        " Keep the same character, head shape, skin, clothing, position, original background, lighting, and exact camera framing. Render one still image of the character. Do not draw an emoji icon.";

    private static readonly (string Emoji, string Key, string ArtistAction)[] Reactions =
    [
        ("👋", "Wave", "Replace the current reaction with a friendly wave. Add one raised hand with an open palm beside the face. Open both eyes and give the character a small friendly closed-mouth smile. Keep the tongue inside the mouth."),
        ("😄", "Joy", "Replace the current facial expression with joy. Lift the cheeks, relax the eyebrows, and give the character a broad warm smile with both eyes gently open. Keep the tongue inside the mouth."),
        ("😂", "Laugh", "Replace the current facial expression with hearty laughter. Crinkle both eyes symmetrically, lift both cheeks, and open the smiling mouth. Keep the tongue inside the mouth."),
        ("😮", "Surprise", "Replace the current facial expression with surprise. Raise both eyebrows, open both eyes wide, and make the mouth a small rounded O. Retract the tongue fully inside the mouth."),
        ("😢", "Sad", "Replace the current facial expression with sadness. Lower the gaze and the corners of the closed mouth, lift the inner ends of the brows, and relax the cheeks. Keep the tongue inside the mouth."),
        ("😠", "Angry", "Replace the current facial expression with anger. Pull both brows down toward the nose, look straight ahead with both eyes open, and press the lips firmly together. Keep the tongue inside the mouth."),
        ("😳", "Shy", "Replace the current expression with bashful embarrassment. Look down and to the side with both eyes open. Give the cheeks a slight blush and draw a modest closed-mouth smile. Keep the tongue inside the mouth."),
        ("❤️", "Love", "Replace the current expression with warm affection. Soften the gaze in both open eyes, slightly lift the cheeks, and give the character a tender closed-mouth smile. Keep the tongue inside the mouth."),
        ("👍", "Approval", "Replace the current reaction with clear approval. Put one hand beside the face with its thumb pointing up and the other fingers curled. Give the character a small closed-mouth smile and open both eyes."),
        ("👎", "Disagree", "Replace the current reaction with clear disapproval. Put one hand beside the face with its thumb pointing down and the other fingers curled. Open both eyes and keep the lips closed with a mildly displeased expression."),
        ("🤔", "Doubt", "Replace the entire current facial expression with a clearly puzzled, thinking look. Retract the tongue completely inside the mouth, make the lips meet in a small closed asymmetric frown, and open both eyes as equally sized eyes looking up to the side. The eyebrows are drawn together in thoughtful doubt with one eyebrow slightly raised. Change the eyes, eyebrows, tongue, lips, and mouth."),
        ("🙏", "Thanks", "Replace the current reaction with gratitude. Bring both palms together visibly just below the chin. Open both eyes softly and give the character a small closed-mouth smile."),
        ("😉", "Wink", "Close exactly one eye by meeting its eyelids. Keep the other eye open with its gaze fixed. Draw a small closed-mouth smile and keep the tongue inside the mouth. Preserve the head position."),
        ("😜", "Tongue", "Draw a playful teasing expression. Keep one eye closed and the other open, smile widely, and stick out the tongue. Preserve the head position."),
        ("🙂", "Smile", "Replace the current expression with a relaxed smile. Open both eyes naturally, lower and relax both eyebrows, and curve the closed lips gently upward. Keep the tongue inside the mouth.")
    ];

    private static (string Emoji, string Key, string ArtistAction) FindReaction(string expression)
    {
        var symbol = expression.Trim() switch
        {
            ";)" => "😉",
            ";)-" => "😜",
            "=)" or ":)" => "🙂",
            var value => value
        };
        return Reactions.FirstOrDefault(item => item.Emoji == symbol);
    }

    internal static string ReactionInstruction(string expression, string details, Func<string, string> text)
    {
        var key = FindReaction(expression).Key;
        var action = key is null
            ? AnimationEmojiCatalog.Find(expression) is { } entry
                ? text("EmojiCatalogLanguage") == "en" ? entry.EnglishName : entry.RussianName
                : expression.Trim()
            : text(key + "Pose");
        return string.Format(text("ReactionPosePrompt"), AddReactionDetails(action, details));
    }

    internal static string ArtistReactionInstruction(string expression, string details)
    {
        var action = FindReaction(expression).ArtistAction ?? AnimationEmojiCatalog.Find(expression)?.ArtistAction;
        if (action is null)
            action = string.IsNullOrWhiteSpace(details) ? "Change the character's expression to " + expression.Trim() + "." : "";
        return AddReactionDetails(action, details) + PreserveReactionFrame;
    }

    private static string AddReactionDetails(string action, string details) =>
        string.IsNullOrWhiteSpace(details) ? action :
        string.IsNullOrWhiteSpace(action) ? details.Trim() :
        action.TrimEnd('.') + ". " + details.Trim();

    internal static string ReactionPoseInstruction(AnimationProject project, AnimationCard card,
        AnimationPose pose, Func<string, string> text, bool forArtist = false)
    {
        if (card.Kind == "scene" || string.IsNullOrWhiteSpace(pose.Text))
            return string.IsNullOrWhiteSpace(pose.Prompt) ? pose.Text : pose.Prompt;

        var details = card.Kind == "reaction" ? card.ReactionDetails : "";
        var localized = ReactionInstruction(pose.Text, details, text);
        var stored = pose.Prompt;
        if (!string.IsNullOrWhiteSpace(stored) && stored != localized &&
            stored != string.Format(text("ReactionPrompt"), pose.Text, project.ReferenceEmoji) &&
            stored != PreviousReactionInstruction(project, pose.Text, details, text))
            return stored;

        return forArtist ? ArtistReactionInstruction(pose.Text, details) : localized;
    }

    private static string PreviousReactionInstruction(AnimationProject project, string expression,
        string details, Func<string, string> text)
    {
        var key = FindReaction(expression).Key;
        var action = key switch
        {
            "Wink" or "Tongue" => text(key + "Pose"),
            null => expression.Trim(),
            _ => text(key)
        };
        if (!string.IsNullOrWhiteSpace(details)) action += ". " + details.Trim();
        var source = project.ReferenceIsEmoji && !string.IsNullOrWhiteSpace(project.ReferenceEmoji)
            ? project.ReferenceEmoji : text("ReferenceExpression");
        return string.Format(text("ReactionEditPrompt"), action, source);
    }
}
