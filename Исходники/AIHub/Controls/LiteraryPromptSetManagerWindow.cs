using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

public sealed class LiteraryPromptSetManagerWindow : Window
{
    public List<LiteraryPromptSet> Items { get; private set; }

    public LiteraryPromptSetManagerWindow(Window? owner, IReadOnlyList<LiteraryPromptSet> items, Func<string,string> l,
        Func<IReadOnlyList<LiteraryPromptSet>,string?> save, IReadOnlyList<PromptPairPreset>? legacy = null, string? selectedId = null)
    {
        Items = items.Select(p => p.Copy()).ToList();
        LiteraryPromptDialogUi.Configure(this, owner, l("PromptPairs.Manage"));
        Width = 860; Height = 560;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var list = LiteraryPromptDialogUi.Identify(new ListBox { DisplayMemberPath = nameof(LiteraryPromptSet.Name) }, "PresetList");
        PromptDialogUi.Label(list, l("PromptPairs.Select"));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var error = LiteraryUi.Text(""); footer.Children.Add(error);
        var actions = new WrapPanel(); footer.Children.Add(actions); root.Children.Add(list);
        void Refresh(string? id) { list.ItemsSource = Items; list.SelectedItem = Items.FirstOrDefault(p => p.Id == id); }
        string? Commit(List<LiteraryPromptSet> next)
        {
            try
            {
                LiteraryPromptSets.Validate(next);
                var failure = save(next);
                if (failure is null) Items = next;
                return failure;
            }
            catch (Exception ex) { return PromptDialogUi.Error(ex, l); }
        }
        var rename = LiteraryPromptDialogUi.Button(l("PromptPairs.Rename"), () =>
        {
            if (list.SelectedItem is not LiteraryPromptSet selected) return;
            var name = PromptDialogUi.EditWishes(this, selected.Name, l, l("PromptPairs.Rename"));
            if (name is null) return;
            error.Text = Commit(Items.Select(p => p.Id == selected.Id ? p with { Name = name.Trim() } : p).ToList()) ?? "";
            Refresh(selected.Id);
        }, "RenameSet");
        actions.Children.Add(rename);
        var edit = LiteraryPromptDialogUi.Button(l("PromptPairs.Edit"), () =>
        {
            if (list.SelectedItem is not LiteraryPromptSet selected) return;
            new LiteraryPromptSetEditorWindow(this, selected, Items, l,
                p => Commit(Items.Select(old => old.Id == p.Id ? p : old).ToList()), legacy).ShowDialog();
            Refresh(selected.Id);
        }, "EditSet");
        actions.Children.Add(edit);
        var delete = LiteraryPromptDialogUi.Button(l("PromptPairs.Delete"), () =>
        {
            if (list.SelectedItem is not LiteraryPromptSet selected
                || !PromptDialogUi.Confirm(this, l("PromptPairs.DeleteConfirm") + "\n" + selected.Name, Title)) return;
            error.Text = Commit(Items.Where(p => p.Id != selected.Id).ToList()) ?? "";
            Refresh(error.Text.Length == 0 ? null : selected.Id);
        }, "DeleteSet");
        actions.Children.Add(delete);
        void Move(int offset)
        {
            if (list.SelectedItem is not LiteraryPromptSet selected) return;
            var index = Items.FindIndex(p => p.Id == selected.Id);
            if (index + offset < 0 || index + offset >= Items.Count) return;
            var next = Items.ToList(); (next[index], next[index + offset]) = (next[index + offset], next[index]);
            error.Text = Commit(next) ?? ""; Refresh(selected.Id);
        }
        var up = LiteraryPromptDialogUi.Button(l("PromptPairs.Up"), () => Move(-1), "MoveUp"); actions.Children.Add(up);
        var down = LiteraryPromptDialogUi.Button(l("PromptPairs.Down"), () => Move(1), "MoveDown"); actions.Children.Add(down);
        actions.Children.Add(LiteraryPromptDialogUi.Button(l("Common.Close"), Close, "CloseManager"));
        void Availability()
        {
            rename.IsEnabled = edit.IsEnabled = delete.IsEnabled = list.SelectedItem is LiteraryPromptSet;
            up.IsEnabled = list.SelectedIndex > 0;
            down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < Items.Count - 1;
        }
        list.SelectionChanged += (_, _) => Availability(); Refresh(selectedId); Availability();
    }
}
