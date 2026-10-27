using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool _checkingNarrativeLanguage;
    private string _languageNote = "";
    private (string Id, string Revision, string Text, string Form) _languageNoteSource;
    private string CurrentLanguageNote => _languageNoteSource == (ActiveText.Id, ActiveText.IdeaRevisionId,
        ActiveText.IdeaText, ActiveText.Form) ? _languageNote : "";

    private async Task PrepareWorkingVersionAsync(CancellationToken token)
    {
        var p = ActiveText;
        var snapshot = (p.Id, p.IdeaRevisionId, p.IdeaText, p.Form);
        _languageNoteSource = snapshot;
        var translate = false;
        _checkingNarrativeLanguage = true; _languageNote = ""; _statusKey = "LanguageChecking"; Render();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        try
        {
            var result = await Task.Run(() => VideoNarrativeLanguage.Detect(snapshot.IdeaText), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (Session.Project.CurrentStep is not ("translation" or "development_translation" or "world_translation") || snapshot != (ActiveText.Id,
                ActiveText.IdeaRevisionId, ActiveText.IdeaText, ActiveText.Form) || !DevelopmentReviewed() || !WorldReviewed())
            { _statusKey = "LanguageSourceChanged"; return; }
            var ready = VideoTranslationHistory.IsCurrent(p) && !string.IsNullOrWhiteSpace(p.EnglishText);
            if (!result.NeedsTranslation)
            {
                _languageNote = result.Kind == VideoNarrativeLanguageKind.English ? "LanguageEnglish" : "LanguageProtectedOnly";
                if (!ready)
                {
                    var select = p.EnglishText.Length == 0;
                    if (!UseOriginalWorkingVersion(select)) return;
                    _statusKey = select ? "LanguageCopied" : "AiReadyInHistory";
                }
                else _statusKey = "";
            }
            else
            {
                if (result.Kind == VideoNarrativeLanguageKind.Unknown) _languageNote = "LanguageUncertain";
                translate = !ready;
                _statusKey = "";
            }
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _checkingNarrativeLanguage = false; Render(); }
        if (translate) await TranslateConceptAsync(token, automatic: true);
    }
}
