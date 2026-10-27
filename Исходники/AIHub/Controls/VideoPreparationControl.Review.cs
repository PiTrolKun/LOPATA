using System.Windows;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool ReviewBeforeNext()
    {
        var p = Session.Project;
        var development = IsDevelopmentStep && !VideoDevelopment.SourceCurrent(p);
        var world = IsWorldStep && !VideoWorld.SourceCurrent(p);
        var translation = p.CurrentStep is "translation" or "development_translation" or "world_translation"
            && !string.IsNullOrWhiteSpace(ActiveText.EnglishText) && (!VideoTranslationHistory.IsCurrent(ActiveText) || development || world);
        if (!translation && !development && !world) return true;
        if (!IsVisible) return false;
        if (development && !DevelopmentSourceAvailable()) return false;
        if (world && !VideoWorld.SourceAvailable(p)) { _statusKey = "WorldNeedsSource"; RefreshStatus(); return false; }
        var text = ActiveText; var original = text.IdeaText; var english = text.EnglishText; var context = VideoWorld.Context(p);
        var sourceOriginal = (p.IdeaText, p.Development?.IdeaText);
        var message = T(translation ? "ReviewTranslation" : development ? "ReviewDevelopment" : "ReviewWorld");
        if (translation && (world || development)) message = T(world ? "ReviewWorld" : "ReviewDevelopment") + "\n\n" + message;
        var dialog = new VideoPreparationDecisionWindow(Window.GetWindow(this), _localize, "ReviewTitle",
            message,
            ("ReviewContinue", "continue"), ("ReviewEdit", "cancel"));
        try
        {
            if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) != true || dialog.Choice != "continue") return false;
            if (!ReferenceEquals(p, Session.Project) || text.IdeaText != original || text.EnglishText != english || VideoWorld.Context(p) != context
                || sourceOriginal != (p.IdeaText, p.Development?.IdeaText))
            { _statusKey = "LanguageSourceChanged"; RefreshStatus(); return false; }
            if (development) VideoDevelopment.RefreshSource(p);
            if (world) VideoWorld.Review(p);
            if (translation) VideoTranslationHistory.ConfirmCurrentSource(text);
            Session.Changed(); return Checkpoint();
        }
        catch (OperationCanceledException) { return false; }
    }
}
