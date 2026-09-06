using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class PromptPairManagerWindow : Window
{
    public List<PromptPairPreset> Items { get; private set; }

    public PromptPairManagerWindow(Window? owner, IReadOnlyList<PromptPairPreset> items,
        PromptPairPreset defaults, Func<string, string> localize,
        Func<IReadOnlyList<PromptPairPreset>, string?> save)
    {
        Items = items.ToList();
        PromptDialogUi.Configure(this, owner, localize("PromptPairs.Manage"));
        Width = 720; Height = 520;
        var root = new DockPanel { Margin = new Thickness(16) };
        var list = new ListBox { DisplayMemberPath = nameof(PromptPairPreset.Name) };
        PromptDialogUi.Label(list, localize("PromptPairs.Select"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        void Refresh(string? id)
        {
            list.ItemsSource = Items.Where(p => p.ContractId == defaults.ContractId).ToList();
            list.SelectedItem = Items.FirstOrDefault(p => p.Id == id);
        }
        string? Commit(List<PromptPairPreset> next)
        {
            try { PromptPairStore.Validate(next); }
            catch (System.IO.InvalidDataException ex) { return PromptDialogUi.Error(ex, localize); }
            var failure = save(next);
            if (failure is null) Items = next;
            return failure;
        }
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Rename"), () =>
        {
            if (list.SelectedItem is not PromptPairPreset selected) return;
            var name = PromptDialogUi.EditWishes(this, selected.Name, localize, localize("PromptPairs.Rename"));
            if (name is null) return;
            error.Text = Commit(Items.Select(p => p.Id == selected.Id ? p with { Name = name.Trim() } : p).ToList()) ?? string.Empty;
            Refresh(selected.Id);
        }));
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Edit"), () =>
        {
            if (list.SelectedItem is not PromptPairPreset selected) return;
            var editor = new PromptPairEditorWindow(this, selected, defaults, Items, localize,
                candidate => Commit(Items.Select(p => p.Id == candidate.Id ? candidate : p).ToList()));
            editor.ShowDialog(); Refresh(selected.Id);
        }));
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Delete"), () =>
        {
            if (list.SelectedItem is not PromptPairPreset selected
                || !PromptDialogUi.Confirm(this, localize("PromptPairs.DeleteConfirm") + "\n" + selected.Name, Title)) return;
            error.Text = Commit(Items.Where(p => p.Id != selected.Id).ToList()) ?? string.Empty;
            Refresh(null);
        }));
        void Move(int offset)
        {
            if (list.SelectedItem is not PromptPairPreset selected) return;
            var visible = Items.Where(p => p.ContractId == defaults.ContractId).ToList();
            var index = visible.FindIndex(p => p.Id == selected.Id);
            if (index + offset < 0 || index + offset >= visible.Count) return;
            var next = Items.ToList();
            var first = next.FindIndex(p => p.Id == selected.Id);
            var second = next.FindIndex(p => p.Id == visible[index + offset].Id);
            (next[first], next[second]) = (next[second], next[first]);
            error.Text = Commit(next) ?? string.Empty; Refresh(selected.Id);
        }
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Up"), () => Move(-1)));
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Down"), () => Move(1)));
        actions.Children.Add(PromptDialogUi.Button(localize("Common.Close"), Close));
        root.Children.Add(list); Content = root; Refresh(null);
    }
}
