using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private System.Windows.Controls.Button? _worldUndo, _worldRedo;
    private TextBlock? _worldVersion;
    private bool IsWorldStep => VideoWorld.IsStep(Session.Project.CurrentStep);
    private VideoWorldCard SelectedWorldCard => VideoWorld.Ensure(Session.Project).Cards.First(c => c.Id == Session.Project.World!.SelectedCardId);
    private static string WorldKey(string key) => key switch
    {
        "TranslationHint" => "WorldTranslationHint",
        "TranslationSource" => "WorldTranslationSource",
        "EnglishLabel" => "WorldEnglishLabel",
        "EnglishExample" => "WorldEnglishExample",
        "MissingIdea" => "WorldMissingDescription",
        "AiWorking" => "WorldCardWorking",
        _ => key
    };
    private void BuildWorld(StackPanel page)
    {
        var p = Session.Project; var world = VideoWorld.Ensure(p);
        page.Children.Add(Text("WorldHint"));
        var contextText = Text("WorldSource"); contextText.Text = p.IdeaText + "\n\n" + p.Development?.IdeaText;
        page.Children.Add(new Expander { Header = T("WorldSource"), Content = contextText });
        BuildWorldSourceState(page);
        page.Children.Add(Action("WorldPropose", () => _ = ShowWorldOptionsAsync()));
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new StackPanel { Margin = new Thickness(0, 8, 12, 0) };
        foreach (var card in world.Cards)
        {
            var b = Action("WorldCard", () => { VideoWorld.Capture(p); world.SelectedCardId = card.Id; Changed(); Checkpoint(); Render(); });
            b.Content = (card.Id == world.SelectedCardId ? "● " : "") + (card.Name.Length == 0 ? T("WorldKind." + card.Kind) : card.Name);
            AutomationProperties.SetAutomationId(b, "VideoPrep.WorldCard." + card.Id); list.Children.Add(b);
        }
        var kinds = new System.Windows.Controls.ComboBox
        {
            ItemsSource = VideoWorld.CardKinds.Select(k => new { Id = k, Name = T("WorldKind." + k) }).ToArray(),
            DisplayMemberPath = "Name",
            SelectedValuePath = "Id",
            SelectedIndex = 1,
            MinHeight = 34
        };
        AutomationProperties.SetAutomationId(kinds, "VideoPrep.WorldAddKind"); AutomationProperties.SetName(kinds, T("WorldType")); list.Children.Add(kinds);
        var add = Action("WorldAdd", () => { VideoWorld.AddCard(p, (string)kinds.SelectedValue); Changed(); Checkpoint(); Render(); });
        add.IsEnabled = !ConceptBusy && !PendingConcept && world.Cards.Count < 64; list.Children.Add(add);
        var remove = Action("WorldRemove", () =>
        {
            VideoWorld.Capture(p); world.Cards.Remove(SelectedWorldCard);
            world.SelectedCardId = world.Cards[0].Id; Changed(); Checkpoint(); Render();
        });
        remove.IsEnabled = !ConceptBusy && !PendingConcept && world.Cards.Count > 1; list.Children.Add(remove); body.Children.Add(list);
        var editor = new StackPanel(); Grid.SetColumn(editor, 1); body.Children.Add(editor);
        var selected = SelectedWorldCard;
        editor.Children.Add(Text("WorldName"));
        var name = new ExampleTextInput(T("WorldName"), T("WorldNameExample"), "VideoPrep.WorldName"); name.Input.Text = selected.Name;
        name.Input.TextChanged += (_, _) => { selected.Name = name.Input.Text; UpdateCardContext(selected); Changed(); RefreshWorldHistory(); }; editor.Children.Add(name);
        editor.Children.Add(Text("WorldDescription"));
        var description = new ExampleTextInput(T("WorldDescription"), T("WorldExample." + selected.Kind), "VideoPrep.WorldDescription", multiline: true);
        description.Input.Text = selected.Text.IdeaText; LiterarySpellChecking.Enable(description.Input, InputLanguage);
        description.Input.TextChanged += (_, _) => { selected.Text.IdeaText = description.Input.Text; Changed(); RefreshWorldHistory(); };
        _ideaInput = description; editor.Children.Add(description);
        var actions = new DockPanel();
        var improve = ConceptIcon("WorldImprove", "✨", () => _ = ImproveWorldCardAsync());
        improve.IsEnabled = !ConceptBusy && !PendingConcept; DockPanel.SetDock(improve, Dock.Right); actions.Children.Add(improve);
        var history = new StackPanel { Orientation = Orientation.Horizontal };
        _worldUndo = ConceptIcon("WorldUndo", "←", () => { VideoWorld.Move(p, -1); Changed(); Checkpoint(); Render(); });
        _worldRedo = ConceptIcon("WorldRedo", "→", () => { VideoWorld.Move(p, 1); Changed(); Checkpoint(); Render(); });
        history.Children.Add(_worldUndo); history.Children.Add(_worldRedo);
        var i = world.History.FindIndex(v => v.Id == world.RevisionId);
        ((System.Windows.Controls.Button)history.Children[0]).IsEnabled = !ConceptBusy && !PendingConcept && i > 0;
        ((System.Windows.Controls.Button)history.Children[1]).IsEnabled = !ConceptBusy && !PendingConcept && i + 1 < world.History.Count;
        var version = Text("Version"); _worldVersion = version; version.Text = string.Format(T("Version"), i + 1, world.History.Count); history.Children.Add(version);
        actions.Children.Add(history); editor.Children.Add(actions);
        editor.Children.Add(Text("WorldFixed"));
        var fixedInput = new ExampleTextInput(T("WorldFixed"), T("WorldFixedExample"), "VideoPrep.WorldFixed"); fixedInput.Input.Text = selected.Fixed;
        LiterarySpellChecking.Enable(fixedInput.Input, InputLanguage);
        fixedInput.Input.TextChanged += (_, _) => { selected.Fixed = fixedInput.Input.Text; UpdateCardContext(selected); Changed(); RefreshWorldHistory(); }; editor.Children.Add(fixedInput);
        if (_conceptBusy || PendingConcept)
        { editor.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync())); editor.Children.Add(Action("AiCancel", CancelConcept)); }
        page.Children.Add(body);
        RefreshWorldHistory();
    }
    private void RefreshWorldHistory()
    {
        if (Session.Project.CurrentStep != "world" || Session.Project.World is not { } w) return;
        var i = w.History.FindIndex(v => v.Id == w.RevisionId);
        var draft = i >= 0 && w.History[i].Cards != VideoWorld.SerializeCards(w.Cards);
        if (_worldUndo is not null) _worldUndo.IsEnabled = !ConceptBusy && !PendingConcept && (i > 0 || draft);
        if (_worldRedo is not null) _worldRedo.IsEnabled = !ConceptBusy && !PendingConcept && i + 1 < w.History.Count;
        if (_worldVersion is not null) _worldVersion.Text = string.Format(T("Version"), i + 1, w.History.Count);
    }
    private void UpdateCardContext(VideoWorldCard card)
    {
        card.Text.ContextText = VideoWorld.CardContext(Session.Project, card);
    }
    private void BuildWorldSourceState(StackPanel page)
    {
        if (VideoWorld.SourceCurrent(Session.Project)) return;
        page.Children.Add(Text("WorldStale"));
        page.Children.Add(Text("ReviewNextHint"));
    }
    private bool WorldReviewed()
    {
        if (!IsWorldStep || VideoWorld.SourceCurrent(Session.Project)) return true;
        _statusKey = "WorldStale"; RefreshStatus(); return false;
    }
    private async Task ImproveWorldCardAsync()
    {
        if (ConceptBusy || PendingConcept || !WorldReviewed()) return;
        UpdateCardContext(SelectedWorldCard);
        if (string.IsNullOrWhiteSpace(ActiveText.IdeaText)) { _statusKey = "WorldMissingDescription"; RefreshStatus(); return; }
        await ToggleConceptPanelAsync();
    }
}
