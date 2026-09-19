using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

/// <summary>Project selection is a durable snapshot; the library stores complete named sets.</summary>
public sealed class LiteraryStudioPromptWindow : Window
{
    private sealed record Choice(LiteraryPromptSet Set, string Label)
    { public override string ToString() => Label; }

    public LiteraryStudioPromptWindow(Window owner, LiteraryStudioState state, Func<string,string> l, Func<bool> persist,
        PromptPairStore? presetStore = null, LiteraryPromptSetStore? setStore = null)
    {
        LiteraryPromptDialogUi.Configure(this, owner, l("Studio.Prompts"));
        Width = 850; Height = 380; MinWidth = 620; MinHeight = 340;
        var store = setStore ?? LiteraryPromptSetStore.ForUser();
        var settings = LiteraryPromptSets.Read(state, l("Studio.PromptSet.LegacyCurrent"));
        var items = new List<LiteraryPromptSet>();
        IReadOnlyList<PromptPairPreset> legacy = [];
        var failed = false; var suppress = false; var loadError = "";
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var close = LiteraryPromptDialogUi.Button(l("Common.Close"), Close, "ClosePrompts");
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(body);
        var modes = new WrapPanel(); body.Children.Add(modes);
        var standard = LiteraryPromptDialogUi.Button(l("PromptPairs.Standard"), () => { }, "StandardMode");
        var custom = LiteraryPromptDialogUi.Button(l("PromptPairs.Custom"), () => { }, "CustomMode");
        modes.Children.Add(standard); modes.Children.Add(custom);
        body.Children.Add(LiteraryUi.Text(l("Studio.PromptSet.Hint")));
        var customPanel = new StackPanel { Margin = new Thickness(0,12,0,0) }; body.Children.Add(customPanel);
        var row = new DockPanel(); customPanel.Children.Add(row);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
        var choice = LiteraryPromptDialogUi.Identify(new ComboBox { Margin = new Thickness(0,4,8,4), MinWidth = 200 }, "PromptSetChoice");
        PromptDialogUi.Label(choice, l("PromptPairs.Select")); row.Children.Add(choice);
        var create = LiteraryPromptDialogUi.Button("+", () => { }, "CreateSet");
        PromptDialogUi.Label(create, l("PromptPairs.Create")); buttons.Children.Add(create);
        var manage = LiteraryPromptDialogUi.Button("⚙", () => { }, "ManageSets");
        PromptDialogUi.Label(manage, l("PromptPairs.Manage")); buttons.Children.Add(manage);
        var status = LiteraryUi.Text(""); customPanel.Children.Add(status);
        var copy = LiteraryPromptDialogUi.Button(l("Studio.PromptSet.SaveCopy"), () => { }, "SaveProjectCopy");
        copy.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; customPanel.Children.Add(copy);
        var error = LiteraryUi.Text(""); body.Children.Add(error);
        var legacyError = LiteraryUi.Text(""); body.Children.Add(legacyError);
        standard.Click += (_, _) => Change(settings with { Custom = false });
        custom.Click += (_, _) => Change(settings with { Custom = true });
        create.Click += (_, _) => EditNew(null);
        manage.Click += (_, _) => Manage();
        copy.Click += (_, _) => EditNew(settings.Selected);

        bool Reload()
        {
            try { items = store.Load(); failed = false; loadError = error.Text = ""; return true; }
            catch (Exception ex) { failed = true; loadError = error.Text = PromptDialogUi.Error(ex, l); return false; }
        }
        string? Commit(IReadOnlyList<LiteraryPromptSet> next)
        {
            try { store.Save(next); items = next.Select(p => p.Copy()).ToList(); return null; }
            catch (Exception ex) { return PromptDialogUi.Error(ex, l); }
        }
        void Change(LiteraryPromptSettings next)
        {
            try
            {
                if (LiteraryPromptSets.Apply(state, next, persist)) { settings = next; error.Text = loadError; }
                else error.Text = l("Paragraph.SaveError");
            }
            catch (Exception ex) { error.Text = PromptDialogUi.Error(ex, l); }
            Refresh();
        }
        void Refresh()
        {
            suppress = true;
            try
            {
                standard.SetResourceReference(StyleProperty, settings.Custom ? "SecondaryButtonStyle" : "PrimaryButtonStyle");
                custom.SetResourceReference(StyleProperty, settings.Custom ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
                customPanel.Visibility = settings.Custom ? Visibility.Visible : Visibility.Collapsed;
                var options = items.Select(p => new Choice(p, p.Name)).ToList();
                Choice? selected = null;
                if (settings.Selected is { } snapshot)
                {
                    selected = options.FirstOrDefault(p => LiteraryPromptSets.Same(p.Set, snapshot));
                    if (selected is null)
                    {
                        selected = new(snapshot, snapshot.Name + " · " + l("Studio.PromptSet.ProjectCopy"));
                        options.Add(selected);
                    }
                }
                choice.ItemsSource = options; choice.SelectedItem = selected;
                var detached = settings.Selected is { } current && !items.Any(p => LiteraryPromptSets.Same(p, current));
                status.Text = settings.Selected is null ? l("PromptPairs.Empty") : detached
                    ? l("Studio.PromptSet.CopyHint") : string.Format(l("Studio.PromptSet.Used"), settings.Selected.Name);
                copy.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
                create.IsEnabled = manage.IsEnabled = copy.IsEnabled = !failed;
            }
            finally { suppress = false; }
        }
        string NewName(string basis)
        {
            var name = basis; var n = 2;
            while (items.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = basis + " (" + n++ + ")";
            return name;
        }
        void EditNew(LiteraryPromptSet? source)
        {
            if (!Reload()) { Refresh(); return; }
            var candidate = source?.Copy() ?? LiteraryPromptSets.Defaults(l("Studio.MyPreset"));
            candidate = candidate with { Id = Guid.NewGuid().ToString("N"), Name = NewName(candidate.Name) };
            var editor = new LiteraryPromptSetEditorWindow(this, candidate, items, l, p => Commit(items.Append(p).ToList()), legacy);
            if (editor.ShowDialog() == true) Change(new() { Custom = true, Selected = editor.Result });
            else Refresh();
        }
        void Manage()
        {
            if (!Reload()) { Refresh(); return; }
            var before = items.FirstOrDefault(p => p.Id == settings.Selected?.Id);
            var matched = before is not null && settings.Selected is { } snapshot && LiteraryPromptSets.Same(before, snapshot);
            new LiteraryPromptSetManagerWindow(this, items, l, Commit, legacy, settings.Selected?.Id).ShowDialog();
            var after = items.FirstOrDefault(p => p.Id == settings.Selected?.Id);
            if (matched && after is not null && before is not null && !LiteraryPromptSets.Same(before, after))
                Change(settings with { Selected = after });
            else Refresh();
        }
        choice.SelectionChanged += (_, _) =>
        { if (!suppress && choice.SelectedItem is Choice selected) Change(settings with { Custom = true, Selected = selected.Set }); };
        Reload();
        try { legacy = (presetStore ?? PromptPairStore.ForUser()).Load().Where(p => LiteraryPromptSets.LegacyAction(p) is not null).ToArray(); }
        catch (Exception ex) { legacyError.Text = l("Studio.PromptSet.Legacy") + ": " + PromptDialogUi.Error(ex, l); }
        Refresh();
    }
}
