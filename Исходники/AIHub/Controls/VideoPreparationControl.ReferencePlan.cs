using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void BuildReferencePlan(StackPanel page)
    {
        var p = Session.Project; var plan = VideoReferencePlans.Ensure(p);
        var busy = ConceptBusy || PendingConcept;
        BuildReferencePlanProgress(page);
        if (plan.Page == "ready")
        {
            BuildReferenceDrawingEntry(page, plan, busy);
            page.Children.Add(Action("RefPlanBackToPrompts", () => { plan.Page = "prompts"; Changed(); Checkpoint(); Render(); })); return;
        }
        if (plan.Page == "prompts") { BuildReferencePromptEditor(page, plan, busy); return; }
        page.Children.Add(Text("RefPlanMode", true));
        var modes = new WrapPanel();
        foreach (var mode in new[] { "assisted", "manual" })
        {
            var button = Action("RefPlanMode." + mode, () => { plan.Mode = mode; plan.Page = "list"; Changed(); Checkpoint(); Render(); });
            button.IsEnabled = !busy;
            if (plan.Mode == mode) button.SetResourceReference(System.Windows.Controls.Button.StyleProperty, "SettingsPrimaryActionStyle");
            modes.Children.Add(button);
        }
        page.Children.Add(modes);
        if (plan.Mode == "") return;
        if (plan.Mode == "assisted") BuildReferencePlanModels(page, plan, busy);
        else page.Children.Add(Text("RefPlanManualHint"));
        page.Children.Add(Text("RefPlanList", true)); page.Children.Add(Text("RefPlanListHint"));
        var actions = new WrapPanel();
        if (plan.Mode == "assisted")
        {
            var discover = Action("RefPlanDiscover", () => _ = PrepareReferencePlanAsync(true)); discover.IsEnabled = !busy; actions.Children.Add(discover);
        }
        var add = Action("RefPlanAdd", () => { VideoReferencePlans.Add(p); Changed(); Checkpoint(); Render(); });
        add.IsEnabled = !busy && plan.Items.Count < 128; actions.Children.Add(add); page.Children.Add(actions);
        foreach (var item in plan.Items.ToArray()) BuildReferencePlanItem(page, item, busy);
        if (plan.Items.Count == 0) page.Children.Add(Text("RefPlanEmpty"));
        foreach (var proposal in plan.Proposals.AsEnumerable().Reverse())
        {
            var raw = new System.Windows.Controls.TextBox { Text = proposal.Raw, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var expander = new Expander { Header = T("RefPlanRaw") + " · " + proposal.Model.Name, Content = raw, Margin = new Thickness(0, 6, 0, 6) };
            expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); page.Children.Add(expander);
        }
        var prepare = Action("RefPlanPrepare", () =>
        {
            if (plan.Mode == "assisted") _ = PrepareReferencePlanAsync(false);
            else if (plan.Items.Any(i => i.Selected && i.Name.Trim().Length > 0))
            { plan.Page = "prompts"; Changed(); Checkpoint(); Render(); }
            else { _statusKey = "RefPlanChooseItems"; RefreshStatus(); }
        }); prepare.IsEnabled = !busy && plan.Items.Any(i => i.Selected); page.Children.Add(prepare);
    }
    private void BuildReferencePlanModels(StackPanel page, VideoReferencePlan plan, bool busy)
    {
        page.Children.Add(Text("AiModel"));
        if (_conceptChoices.Count == 0)
        {
            var load = Action("RefPlanLoadModels", () => _ = LoadConceptChoicesAsync()); load.IsEnabled = !busy; page.Children.Add(load); return;
        }
        var selected = plan.ModelId.Length > 0 ? plan.ModelId : Session.Project.LastAssistantModelId.Length > 0
            ? Session.Project.LastAssistantModelId : VideoConceptModels.Default(_conceptChoices);
        var models = new ComboBox { ItemsSource = _conceptChoices, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            SelectedValue = selected, MinHeight = 34, IsEnabled = !busy };
        AutomationProperties.SetAutomationId(models, "VideoPrep.RefPlanModel"); AutomationProperties.SetName(models, T("AiModel"));
        models.SelectionChanged += (_, _) =>
        {
            if (models.SelectedValue is not string id) return;
            plan.ModelId = id; Session.Project.LastAssistantModelId = id; Changed(); Checkpoint(); ScheduleWarmup();
        };
        page.Children.Add(models);
    }
    private void BuildReferencePlanItem(StackPanel page, VideoReferencePrompt item, bool busy)
    {
        var p = Session.Project; var plan = p.References!.Plan!;
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 10), IsEnabled = !busy };
        var selected = new System.Windows.Controls.CheckBox { Content = T("RefPlanInclude"), IsChecked = item.Selected };
        selected.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(selected, "VideoPrep.RefPlanInclude." + item.Id);
        selected.Checked += (_, _) => { item.Selected = true; Changed(); Render(); };
        selected.Unchecked += (_, _) => { item.Selected = false; Changed(); Render(); }; row.Children.Add(selected);
        var name = new ExampleTextInput(T("RefPlanName"), T("RefPlanNameExample"), "VideoPrep.RefPlanName." + item.Id);
        name.Input.Text = item.Name; name.Input.MaxLength = 1024; LiterarySpellChecking.Enable(name.Input, InputLanguage);
        name.Input.TextChanged += (_, _) => { item.Name = name.Input.Text; Changed(); }; row.Children.Add(name);
        var details = new StackPanel();
        var expander = new Expander { Header = T("RefPlanItemDetails"), Content = details, Margin = new Thickness(0, 4, 0, 4) };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); row.Children.Add(expander);
        var targets = new[] { new ReferenceTarget("", T("RefGeneral")) }.Concat((p.World?.Cards ?? [])
            .Select(c => new ReferenceTarget(c.Id, c.Name.Length > 0 ? c.Name : T("WorldKind." + c.Kind)))).ToList();
        if (!targets.Any(t => t.Id == item.CardId)) targets.Add(new(item.CardId, T("RefMissingCard")));
        var target = new ComboBox { ItemsSource = targets, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = item.CardId, MinHeight = 34 };
        AutomationProperties.SetAutomationId(target, "VideoPrep.RefPlanTarget." + item.Id); AutomationProperties.SetName(target, T("RefTarget"));
        target.SelectionChanged += (_, _) => { item.CardId = (string?)target.SelectedValue ?? ""; Changed(); }; details.Children.Add(target);
        var purposes = new ComboBox { ItemsSource = VideoReferences.Purposes.Select(k => new ReferenceTarget(k, T("RefPurpose." + k))).ToArray(),
            DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = item.Purpose, MinHeight = 34 };
        AutomationProperties.SetAutomationId(purposes, "VideoPrep.RefPlanPurpose." + item.Id); AutomationProperties.SetName(purposes, T("RefPurpose"));
        purposes.SelectionChanged += (_, _) => { item.Purpose = (string?)purposes.SelectedValue ?? "general"; Changed(); }; details.Children.Add(purposes);
        var brief = new ExampleTextInput(T("RefPlanBrief"), T("RefPlanBriefExample"), "VideoPrep.RefPlanBrief." + item.Id, multiline: true);
        brief.Input.Text = item.Brief; brief.Input.MaxLength = 10000; brief.Input.Height = 70; LiterarySpellChecking.Enable(brief.Input, InputLanguage);
        brief.Input.TextChanged += (_, _) => { item.Brief = brief.Input.Text; Changed(); }; details.Children.Add(brief);
        var covered = Text("RefPlanCoverage"); covered.Text = string.Format(T("RefPlanCoverage"), p.References.Bindings.Count(b =>
            b.CardId == item.CardId && b.Purpose == item.Purpose && !VideoReferences.NeedsReview(p, b))); details.Children.Add(covered);
        var remove = Action("RefPlanRemove", () =>
        {
            plan.Items.Remove(item); if (plan.SelectedId == item.Id) plan.SelectedId = plan.Items.FirstOrDefault()?.Id ?? "";
            Changed(); Checkpoint(); Render();
        }); details.Children.Add(remove); page.Children.Add(row);
    }
    private void BuildReferencePlanProgress(StackPanel page)
    {
        if (!_conceptBusy && !PendingConcept) return;
        var progress = new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetAutomationId(progress, "VideoPrep.RefPlanProgress"); page.Children.Add(progress);
        var controls = new WrapPanel(); controls.Children.Add(Action("AiPauseResume", () => _ = PauseResumeConceptAsync()));
        controls.Children.Add(Action("AiCancel", CancelConcept)); page.Children.Add(controls);
    }
}
