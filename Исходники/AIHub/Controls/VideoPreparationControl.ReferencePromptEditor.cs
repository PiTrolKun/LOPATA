using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private void BuildReferencePromptEditor(StackPanel page, VideoReferencePlan plan, bool busy)
    {
        var p = Session.Project; var items = plan.Items.Where(i => i.Selected).ToArray();
        page.Children.Add(Text("RefPlanPrompts", true));
        var back = Action("RefPlanBackToList", () => { plan.Page = "list"; Changed(); Checkpoint(); Render(); });
        back.IsEnabled = !busy; page.Children.Add(back);
        if (items.Length == 0) { page.Children.Add(Text("RefPlanChooseItems")); return; }
        var item = items.FirstOrDefault(i => i.Id == plan.SelectedId) ?? items[0];
        System.Action refreshHistory = () => { };
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var entry in items)
        {
            var select = Action("RefPlanSelect", () => { plan.SelectedId = entry.Id; Changed(); Checkpoint(); Render(); });
            select.Content = (VideoReferencePlans.Ready(p, entry) ? "✓ " : "○ ") + entry.Name;
            AutomationProperties.SetAutomationId(select, "VideoPrep.RefPlanSelect." + entry.Id); list.Children.Add(select);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 600, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Top });
        var editor = new StackPanel(); Grid.SetColumn(editor, 1); body.Children.Add(editor); page.Children.Add(body);
        var heading = Text("RefPlanName", true); heading.Text = item.Name; editor.Children.Add(heading);
        var purpose = Text("RefPurpose"); purpose.Text = T("RefPurpose." + item.Purpose) + " · " + item.Brief; editor.Children.Add(purpose);
        var description = Text("RefCardDescription"); description.Text = string.Join("\n\n", (p.World?.Cards ?? [])
            .Where(c => item.CardId == "" || c.Id == item.CardId).Select(c => c.Name + "\n" + c.Text.IdeaText + "\n" + c.Fixed));
        var source = new Expander { Header = T("RefCardDescription"), Content = description };
        source.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); editor.Children.Add(source);
        editor.Children.Add(Text("RefPlanNative"));
        var native = new ExampleTextInput(T("RefPlanNative"), T("RefPlanNativeExample"), "VideoPrep.RefPlanNative", multiline: true);
        native.Input.Height = 160; native.Input.MaxLength = 20000; native.Input.Text = item.Native; native.Input.IsReadOnly = busy;
        LiterarySpellChecking.Enable(native.Input, InputLanguage);
        native.Input.TextChanged += (_, _) => { item.Native = native.Input.Text; Changed(); refreshHistory(); }; editor.Children.Add(native);
        editor.Children.Add(Text("RefPlanEnglish"));
        var english = new ExampleTextInput(T("RefPlanEnglish"), T("RefPlanEnglishExample"), "VideoPrep.RefPlanEnglish", multiline: true);
        english.Input.Height = 160; english.Input.MaxLength = 20000; english.Input.Text = item.English; english.Input.IsReadOnly = busy;
        LiterarySpellChecking.Enable(english.Input, "en-US");
        english.Input.TextChanged += (_, _) => { item.English = english.Input.Text; Changed(); refreshHistory(); }; editor.Children.Add(english);
        var tools = new WrapPanel();
        void Move(int direction) { if (VideoReferencePlans.Move(p, item, direction)) { Changed(); Checkpoint(); Render(); } }
        var previous = ConceptIcon("RefPlanPrevious", "←", () => Move(-1));
        var next = ConceptIcon("RefPlanNext", "→", () => Move(1));
        var index = item.History.FindIndex(v => v.Id == item.RevisionId);
        previous.IsEnabled = !busy && (index > 0 || index >= 0 && (item.Native != item.History[index].Native || item.English != item.History[index].English));
        next.IsEnabled = !busy && index >= 0 && index + 1 < item.History.Count;
        tools.Children.Add(previous); tools.Children.Add(next);
        var versions = Text("Version"); versions.Text = item.History.Count == 0 ? T("VersionEmpty") : string.Format(T("Version"), index + 1, item.History.Count);
        tools.Children.Add(versions);
        refreshHistory = () =>
        {
            var current = item.History.FindIndex(v => v.Id == item.RevisionId);
            var draft = current >= 0 && (item.Native != item.History[current].Native || item.English != item.History[current].English);
            previous.IsEnabled = !busy && (current > 0 || draft);
            next.IsEnabled = !busy && current >= 0 && current + 1 < item.History.Count;
            versions.Text = item.History.Count == 0 ? T("VersionEmpty") : string.Format(T("Version"), current + 1, item.History.Count);
        };
        native.Input.LostKeyboardFocus += (_, _) => refreshHistory();
        english.Input.LostKeyboardFocus += (_, _) => refreshHistory();
        var improve = ConceptIcon("RefPlanImprove", "✨", () => _ = ImproveReferencePromptAsync(item));
        improve.IsEnabled = !busy; tools.Children.Add(improve); editor.Children.Add(tools);
        if (index >= 0 && item.History[index].Model is { } model)
        { var used = Text("AiModel"); used.Text = model.Name; editor.Children.Add(used); }
        if (!VideoReferencePlans.Ready(p, item)) editor.Children.Add(Text("RefPlanReviewNeeded"));
        if (plan.Mode == "assisted")
        {
            var prepare = Action("RefPlanPrepareRemaining", () => _ = PrepareReferencePlanAsync(false)); prepare.IsEnabled = !busy; page.Children.Add(prepare);
        }
        var finish = Action("RefPlanFinish", FinishReferencePlan); finish.IsEnabled = !busy; page.Children.Add(finish);
    }
    private void FinishReferencePlan()
    {
        VideoReferencePlans.Capture(Session.Project);
        if (!VideoReferencePlans.Ready(Session.Project)) { _statusKey = "RefPlanReviewNeeded"; RefreshStatus(); return; }
        var plan = VideoReferencePlans.Ensure(Session.Project); plan.Page = "ready";
        Changed(); if (Checkpoint()) { _statusKey = "RefPlanBoundary"; Render(); }
    }
}
