using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private Button? _versionBack, _versionForward;
    private TextBlock? _versionLabel;
    private IReadOnlyList<VideoConceptChoice> _conceptChoices = [];

    private Button ConceptIcon(string key, string icon, Action action)
    {
        var button = Action(key, action);
        if (button.Content is TextBlock text) text.Text = icon;
        button.ToolTip = T(key); AutomationProperties.SetName(button, T(key));
        return button;
    }

    private void BuildConceptTools(StackPanel page)
    {
        var tools = new DockPanel { LastChildFill = true };
        var sparkle = ConceptIcon(IsDevelopmentStep ? "AiDevelopToggle" : "AiToggle", "✨", () => _ = ToggleConceptPanelAsync());
        sparkle.IsEnabled = !ConceptBusy && !PendingConcept;
        sparkle.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(sparkle, Dock.Right); tools.Children.Add(sparkle);
        var history = new WrapPanel { Orientation = Orientation.Horizontal };
        _versionBack = ConceptIcon("VersionBack", "←", () => MoveConcept(-1));
        _versionForward = ConceptIcon("VersionForward", "→", () => MoveConcept(1));
        history.Children.Add(_versionBack); history.Children.Add(_versionForward);
        _versionLabel = Text("Version"); _versionLabel.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_versionLabel, "VideoPrep.Version");
        history.Children.Add(_versionLabel);
        if (_conceptBusy || PendingConcept)
        {
            var actions = new WrapPanel { Margin = new Thickness(12, 0, 0, 0) };
            actions.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync()));
            actions.Children.Add(Action(VideoTranslationProtocol.IsTranslationPurpose(_runningPurpose) || PendingConceptState?.Kind is VideoTranslationProtocol.Kind or VideoDevelopment.TranslationKind
                ? "TranslationCancel" : "AiCancel", CancelConcept));
            history.Children.Add(actions);
        }
        tools.Children.Add(history); page.Children.Add(tools);
        RefreshConceptHistory();
        var selected = ActiveText.IdeaHistory.FirstOrDefault(v => v.Id == ActiveText.IdeaRevisionId);
        if (selected?.Model is { } model)
        {
            var details = new StackPanel();
            var identity = Text("AiModel"); identity.Text = model.Name + " · " + T("AiFreedom." + selected.Freedom);
            details.Children.Add(identity);
            var changes = Text("AiChanges"); changes.Text = T("AiChanges") + "\n" + selected.Changes; details.Children.Add(changes);
            if (!string.IsNullOrWhiteSpace(selected.Clarifications))
            { var questions = Text("AiClarifications"); questions.Text = T("AiClarifications") + "\n" + selected.Clarifications; details.Children.Add(questions); }
            var expander = new Expander { Header = T("AiDetails"), Content = details, Margin = new Thickness(0, 6, 0, 6) };
            expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); page.Children.Add(expander);
        }
    }

    private void RefreshConceptHistory()
    {
        var project = ActiveText;
        var index = project.IdeaHistory.FindIndex(v => v.Id == project.IdeaRevisionId);
        var draft = index >= 0 && project.IdeaHistory[index].Text != project.IdeaText;
        if (_versionBack is not null) _versionBack.IsEnabled = index > 0 || draft;
        if (_versionForward is not null) _versionForward.IsEnabled = index >= 0 && index + 1 < project.IdeaHistory.Count;
        if (_versionLabel is not null) _versionLabel.Text = project.IdeaHistory.Count == 0 ? T("VersionEmpty")
            : string.Format(T("Version"), index + 1, project.IdeaHistory.Count);
    }

    public bool MoveConcept(int direction)
    {
        var text = ActiveText.IdeaText; var selected = ActiveText.IdeaRevisionId;
        var moved = VideoConceptHistory.Move(ActiveText, direction);
        Changed();
        if (!Checkpoint())
        {
            ActiveText.IdeaText = text; ActiveText.IdeaRevisionId = selected; Session.Changed();
            RefreshConceptHistory(); return false;
        }
        Render();
        if (moved && ConceptResultVisible) AIHub.Services.ApplicationBackgroundOperations.Current?.Acknowledge(ActiveText.IdeaRevisionId);
        return moved;
    }
}
