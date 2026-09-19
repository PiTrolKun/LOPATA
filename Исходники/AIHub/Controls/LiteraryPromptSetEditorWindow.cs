using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed class LiteraryPromptSetEditorWindow : Window
{
    public LiteraryPromptSet? Result { get; private set; }

    public LiteraryPromptSetEditorWindow(Window? owner, LiteraryPromptSet original, IReadOnlyList<LiteraryPromptSet> existing,
        Func<string,string> l, Func<LiteraryPromptSet,string?> save, IReadOnlyList<PromptPairPreset>? legacy = null)
    {
        LiteraryPromptDialogUi.Configure(this, owner, l("PromptPairs.Editor"));
        Width = 1050; Height = 780; MinWidth = 680; MinHeight = 480;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var error = LiteraryUi.Text(""); footer.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; footer.Children.Add(buttons);
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(LiteraryUi.Text(l("PromptPairs.Name")));
        var name = LiteraryPromptDialogUi.Identify(new TextBox { Text = original.Name, Padding = new Thickness(8), Margin = new Thickness(0,5,0,8) }, "SetName");
        PromptDialogUi.Label(name, l("PromptPairs.Name")); heading.Children.Add(name);
        heading.Children.Add(LiteraryUi.Text(l("Studio.PromptSet.EditorHint")));
        var tree = new StackPanel();
        var scroll = new ScrollViewer { Content = tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        var defaults = LiteraryPromptSets.Defaults("");
        var inputs = new Dictionary<string,(TextBox Role, TextBox Action, Expander Group, Expander Parent)>();
        foreach (var role in new[] { "Advisor", "Writer" })
        {
            var actions = role == "Advisor" ? LiteraryStudioPrompts.Advisor : LiteraryStudioPrompts.Writer;
            var group = LiteraryPromptDialogUi.Identify(new Expander { Header = l("Studio.Role." + role), IsExpanded = false,
                Margin = new Thickness(0,8,0,4), Padding = new Thickness(8), FontWeight = FontWeights.SemiBold }, "Role." + role);
            var actionList = new StackPanel { Margin = new Thickness(12,4,0,0) }; group.Content = actionList; tree.Children.Add(group);
            foreach (var action in actions)
            {
                var fields = new StackPanel { Margin = new Thickness(10,0,8,4) };
                fields.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.Normal);
                var item = LiteraryPromptDialogUi.Identify(new Expander { Header = l("Studio.Action." + action.Id),
                    IsExpanded = false, Content = fields, Margin = new Thickness(0,4,0,4), Padding = new Thickness(4) }, "Action." + action.Id);
                actionList.Children.Add(item);
                var roleText = PromptDialogUi.TextArea(original.Actions[action.Id].Role);
                var actionText = PromptDialogUi.TextArea(original.Actions[action.Id].Action);
                fields.Children.Add(LiteraryPromptDialogUi.Area(this, roleText, defaults.Actions[action.Id].Role,
                    l("Studio.RolePrompt"), action.Id + ".Role", l));
                fields.Children.Add(LiteraryPromptDialogUi.Area(this, actionText, defaults.Actions[action.Id].Action,
                    l("Studio.ActionPrompt"), action.Id + ".Action", l));
                inputs[action.Id] = (roleText, actionText, item, group);
            }
        }
        LiteraryPromptSet Read() => original with { Name = name.Text.Trim(),
            Actions = inputs.ToDictionary(p => p.Key, p => new LiteraryActionPrompt(p.Value.Role.Text, p.Value.Action.Text)) };
        buttons.Children.Add(LiteraryPromptDialogUi.Button(l("PromptPairs.Save"), () =>
        {
            var candidate = Read();
            try
            {
                LiteraryPromptSets.Validate(existing.Where(p => p.Id != original.Id).Append(candidate).ToList());
                var failure = save(candidate);
                if (failure is not null) { error.Text = failure; return; }
                Result = candidate; DialogResult = true;
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or InvalidOperationException)
            {
                error.Text = PromptDialogUi.Error(ex, l);
                if (string.IsNullOrWhiteSpace(name.Text)) name.Focus();
                else foreach (var fields in inputs.Values)
                {
                    var invalid = string.IsNullOrWhiteSpace(fields.Role.Text) ? fields.Role : string.IsNullOrWhiteSpace(fields.Action.Text) ? fields.Action : null;
                    if (invalid is null) continue;
                    fields.Parent.IsExpanded = fields.Group.IsExpanded = true;
                    UpdateLayout(); invalid.Focus(); invalid.BringIntoView(); break;
                }
            }
        }, "SaveSet", true));
        buttons.Children.Add(LiteraryPromptDialogUi.Button(l("Common.Cancel"), Close, "CancelSet"));
        Closing += (_, e) =>
        {
            if (Result is null && (name.Text != original.Name || !LiteraryPromptSets.Same(Read(), original))
                && !PromptDialogUi.Confirm(this, l("PromptPairs.Discard"), Title)) e.Cancel = true;
        };
        var previous = (legacy ?? []).Where(p => LiteraryPromptSets.LegacyAction(p) is not null).ToArray();
        if (previous.Length > 0)
        {
            var import = new DockPanel { Margin = new Thickness(0,8,0,8) };
            var list = new ComboBox { MinWidth = 180, Margin = new Thickness(0,4,4,4) };
            foreach (var pair in previous) list.Items.Add(new ComboBoxItem
            { Content = l("Studio.Action." + LiteraryPromptSets.LegacyAction(pair)) + " · " + pair.Name, Tag = pair });
            list.SelectedIndex = 0; PromptDialogUi.Label(list, l("Studio.PromptSet.Legacy"));
            var insert = LiteraryPromptDialogUi.Button(l("Studio.PromptSet.Import"), () =>
            {
                if (list.SelectedItem is not ComboBoxItem { Tag: PromptPairPreset pair }) return;
                if (!PromptDialogUi.Confirm(this, l("Studio.PromptSet.ImportConfirm"), Title)) return;
                var fields = inputs[LiteraryPromptSets.LegacyAction(pair)!];
                fields.Role.SelectAll(); fields.Role.SelectedText = pair.AnalysisPrompt;
                fields.Action.SelectAll(); fields.Action.SelectedText = pair.ComposePrompt;
                fields.Parent.IsExpanded = fields.Group.IsExpanded = true;
                UpdateLayout(); fields.Role.Focus(); fields.Role.BringIntoView();
            }, "ImportLegacy");
            DockPanel.SetDock(insert, Dock.Right); import.Children.Add(insert); import.Children.Add(list); heading.Children.Add(import);
        }
    }
}
