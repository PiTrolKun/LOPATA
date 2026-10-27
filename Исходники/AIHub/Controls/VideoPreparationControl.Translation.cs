using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private ExampleTextInput? _englishInput;
    private Button? _translationBack, _translationForward;
    private TextBlock? _translationVersion, _translationStale;
    private bool _translationStarting;
    private string _runningPurpose = "concept";

    private void BuildTranslation(StackPanel page)
    {
        VideoTranslationHistory.Capture(ActiveText);
        if (IsDevelopmentStep) BuildDevelopmentSourceState(page);
        if (IsWorldStep) BuildWorldSourceState(page);
        page.Children.Add(Text("TranslationHint"));
        if (CurrentLanguageNote.Length != 0) page.Children.Add(Text(CurrentLanguageNote));
        var source = Text("IdeaLabel"); source.Text = ActiveText.IdeaText;
        var original = new Expander { Header = T("TranslationSource"), Content = source, Margin = new Thickness(0, 4, 0, 10) };
        original.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); page.Children.Add(original);
        var actions = new WrapPanel();
        var model = Text("AiModel");
        model.Margin = new Thickness(0, 5, 12, 9);
        var id = ActiveText.TranslationModelId;
        model.Text = T("AiModel") + ": " + (id.Length == 0 ? T("TranslationDefaultModel") : VideoConceptModels.Name(id));
        actions.Children.Add(model);
        var translate = Action("Translate", () => _ = TranslationOptionsAsync());
        translate.IsEnabled = !ConceptBusy && !PendingConcept && !_translationStarting; actions.Children.Add(translate);
        page.Children.Add(actions);
        if (_checkingNarrativeLanguage || _translationStarting || _conceptBusy && VideoTranslationProtocol.IsTranslationPurpose(_runningPurpose)
            || PendingConceptState?.Kind is VideoTranslationProtocol.Kind or VideoDevelopment.TranslationKind or VideoWorld.TranslationKind)
        {
            var loading = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            var paused = PendingConceptState?.Phase is AIHub.Models.BackgroundOperationPhase.Paused
                or AIHub.Models.BackgroundOperationPhase.Waiting or AIHub.Models.BackgroundOperationPhase.Countdown;
            loading.Children.Add(Text(_checkingNarrativeLanguage ? "LanguageChecking" : paused ? "TranslationPaused" : "TranslationWorking"));
            var progress = new ProgressBar { IsIndeterminate = !paused, Height = 6, Minimum = 0, Maximum = 1 };
            progress.SetResourceReference(ForegroundProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(progress, "VideoPrep.TranslationLoading");
            AutomationProperties.SetName(progress, T("TranslationWorking")); loading.Children.Add(progress); page.Children.Add(loading);
        }
        page.Children.Add(Text("EnglishLabel"));
        _englishInput = new ExampleTextInput(T("EnglishLabel"), T("EnglishExample"), IsWorldStep ? "VideoPrep.WorldEnglish" : IsDevelopmentStep ? "VideoPrep.DevelopmentEnglish" : "VideoPrep.English", multiline: true);
        var material = ActiveText;
        _englishInput.Input.Text = material.EnglishText;
        LiterarySpellChecking.Enable(_englishInput.Input, "en-US");
        var editor = _englishInput;
        _englishInput.Input.TextChanged += (_, _) => { material.EnglishText = editor.Input.Text; Changed(); RefreshTranslationHistory(); };
        page.Children.Add(_englishInput);
        var history = new WrapPanel { Orientation = Orientation.Horizontal };
        _translationBack = ConceptIcon("TranslationBack", "←", () => MoveTranslation(-1));
        _translationForward = ConceptIcon("TranslationForward", "→", () => MoveTranslation(1));
        history.Children.Add(_translationBack); history.Children.Add(_translationForward);
        _translationVersion = Text("Version"); history.Children.Add(_translationVersion);
        if (_conceptBusy || PendingConcept)
        {
            history.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync()));
            history.Children.Add(Action("TranslationCancel", CancelConcept));
        }
        page.Children.Add(history);
        _translationStale = Text("TranslationStale");
        AutomationProperties.SetAutomationId(_translationStale, "VideoPrep.TranslationStale"); page.Children.Add(_translationStale);
        if (VideoTranslationHistory.Selected(ActiveText) is { } selected)
        {
            var details = Text("AiDetails"); details.Text = (selected.Model?.Name ?? T("TranslationManual"))
                + (selected.Source == "user" ? " · " + T("TranslationManual") : "")
                + (selected.Clarifications.Length == 0 ? "" : "\n" + selected.Clarifications);
            page.Children.Add(new Expander { Header = T("AiDetails"), Content = details });
        }
        RefreshTranslationHistory();
    }

    private void RefreshTranslationHistory()
    {
        var p = ActiveText; var index = p.TranslationHistory.FindIndex(v => v.Id == p.TranslationRevisionId);
        if (_translationBack is not null) _translationBack.IsEnabled = index > 0 || index >= 0 && p.TranslationHistory[index].Text != p.EnglishText;
        if (_translationForward is not null) _translationForward.IsEnabled = index >= 0 && index + 1 < p.TranslationHistory.Count;
        if (_translationVersion is not null) _translationVersion.Text = p.TranslationHistory.Count == 0 ? T("VersionEmpty")
            : string.Format(T("Version"), index + 1, p.TranslationHistory.Count);
        if (_translationStale is not null) _translationStale.Visibility = p.EnglishText.Length != 0 && !VideoTranslationHistory.IsCurrent(p)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool MoveTranslation(int direction)
    {
        var text = ActiveText.EnglishText; var selected = ActiveText.TranslationRevisionId;
        var moved = VideoTranslationHistory.Move(ActiveText, direction); Changed();
        if (!Checkpoint())
        { ActiveText.EnglishText = text; ActiveText.TranslationRevisionId = selected; Session.Changed(); return false; }
        Render();
        if (moved && ConceptResultVisible) ApplicationBackgroundOperations.Current?.Acknowledge(ActiveText.TranslationRevisionId);
        return moved;
    }

    private bool HasTranslationForNext()
    {
        VideoConceptHistory.Capture(ActiveText); VideoTranslationHistory.Capture(ActiveText);
        if (!string.IsNullOrWhiteSpace(ActiveText.EnglishText) && VideoTranslationHistory.IsCurrent(ActiveText)) return true;
        _statusKey = string.IsNullOrWhiteSpace(ActiveText.EnglishText) ? "MissingTranslation" : "TranslationStale";
        RefreshStatus(); _englishInput?.Input.Focus(); return false;
    }

    private bool UseOriginalWorkingVersion(bool select)
    {
        VideoTranslationHistory.UseOriginal(ActiveText, select); Session.Changed();
        return Checkpoint();
    }

    public async Task<bool> NextAsync(CancellationToken token = default)
    {
        if (_translationStarting || ConceptBusy || PendingConcept) return false;
        if (!ReferencesReady() || !ReviewReferenceSelection()) return false;
        if (!ReviewBeforeNext()) return false;
        var from = Session.Project.CurrentStep;
        if (!Next()) return false;
        if (from is "idea" or "development" or "world") await PrepareWorkingVersionAsync(token);
        if (from == "references") await StartShotsAsync(automatic: true, token: token);
        if (from == "shots" && Session.Project.Shots is { } shots && !shots.Scenes.All(s => VideoShots.TranslationCurrent(shots, s)))
            await StartShotsAsync(translation: true, token: token);
        return true;
    }

    public async Task TranslateConceptAsync(CancellationToken token = default, bool automatic = false)
    {
        if (ConceptBusy || PendingConcept || _translationStarting || !HasIdeaForNext()) return;
        var material = ActiveText;
        _translationStarting = true; _statusKey = "TranslationWorking"; Render();
        try
        {
            if (_assistant is null) { _statusKey = "AiUnavailable"; return; }
            if (_conceptChoices.Count == 0 || ActiveText.AssistantModelId == "") await LoadConceptChoicesAsync();
            if (!ReferenceEquals(ActiveText, material)) { _statusKey = "LanguageSourceChanged"; return; }
            var p = ActiveText;
            if (!p.TranslationModelExplicit || p.TranslationModelId == "")
            { p.TranslationModelId = p.AssistantModelId; Session.Changed(); }
            var choice = _conceptChoices.FirstOrDefault(c => c.Id == p.TranslationModelId);
            if (choice?.Model is null) { _statusKey = "AiNeedsPreparation"; return; }
            if (choice.Fits == false) { _statusKey = "AiMemoryUnavailable"; return; }
            if (!Save()) return;
            var request = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, Session.ArchivePath!, p.IdeaRevisionId,
                p.IdeaText, p.Form, choice.Id, choice.Model.Path, 1, VideoTranslationProtocol.Version, EngineSetSelection.ActiveId,
                IsWorldStep ? VideoWorld.TranslationPurpose : IsDevelopmentStep ? VideoDevelopment.TranslationPurpose : VideoTranslationProtocol.Purpose, p.TranslationRevisionId, p.EnglishText,
                ActivateResult: !automatic || p.EnglishText.Length == 0,
                ContextRevisionId: p.ContextRevisionId, ContextText: p.ContextText, ContextOriginal: IsWorldStep ? Session.Project.World!.Context : p.ContextOriginal, ResponseLanguage: InputLanguage);
            await RunConceptAsync(request, token);
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _translationStarting = false; Render(); }
    }

    private async Task TranslationOptionsAsync()
    {
        if (ConceptBusy || PendingConcept || _translationStarting) return;
        if (_conceptChoices.Count == 0 || ActiveText.AssistantModelId == "") await LoadConceptChoicesAsync();
        if (_conceptChoices.Count == 0) return;
        var p = ActiveText;
        var dialog = new VideoConceptOptionsWindow(Window.GetWindow(this), _localize, _conceptChoices,
            p.TranslationModelId == "" ? p.AssistantModelId : p.TranslationModelId, 1, translation: true);
        var accepted = false; _conceptDialogOpen = true;
        try
        {
            if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) == true && ActiveText.Id == p.Id)
            { p.TranslationModelId = dialog.SelectedModelId; p.TranslationModelExplicit = true; Session.Project.LastAssistantModelId = dialog.SelectedModelId; Changed(); accepted = true; }
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception error) { ConceptError(error); }
        finally { _conceptDialogOpen = false; Render(); }
        if (accepted) await TranslateConceptAsync();
    }
}
