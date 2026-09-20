using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using SelectionMode = System.Windows.Controls.SelectionMode;

namespace AIHub.Controls;

public sealed class LiteraryImportGroupingWindow : Window
{
    public ImportDecision[] Decisions { get; private set; }
    public LiteraryImportGroupingWindow(Window owner, ImportInput input, ImportDecision[] decisions, Func<string, string> l)
    {
        Decisions = decisions.ToArray();
        string L(string key) => l("Literary.Import." + key);
        LiteraryPromptDialogUi.Configure(this, owner, L("EditGroups"));
        Width = 1000; Height = 730; MinWidth = 650; MinHeight = 450;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var hint = LiteraryUi.Text(L("GroupsHint")); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var target = new System.Windows.Controls.ComboBox { IsEditable = true, Margin = new Thickness(0, 6, 0, 6), MaxWidth = 600, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MinWidth = 300 };
        foreach (var name in decisions.Select(d => d.Project).Where(p => p.Length > 0).Distinct()) target.Items.Add(name);
        footer.Children.Add(LiteraryUi.Text(L("AssignWork"))); footer.Children.Add(target);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) }); root.Children.Add(grid);
        var list = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(GroupRow.Label), Margin = new Thickness(0, 10, 10, 0) };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); grid.Children.Add(list);
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        preview.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); preview.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        list.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(preview, 1); grid.Children.Add(preview);
        var byId = input.Units.ToDictionary(u => u.Id);
        void Reload()
        {
            list.ItemsSource = Decisions.Select((d, i) => (d, i, u: byId[d.Id]))
                .GroupBy(p => (p.u.Conversation, p.u.Message, p.d.Project))
                .Select((g, n) => new GroupRow($"{n + 1}. {(g.Key.Project.Length == 0 ? L("SharedGroup") : g.Key.Project)} · {g.Count()}",
                    g.Select(p => p.i).ToArray(), string.Concat(g.Select(p => p.u.Text)))).ToArray();
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is GroupRow row) preview.Text = row.Text; };
        actions.Children.Add(LiteraryUi.Button(L("AssignSelected"), () =>
        {
            var name = target.Text.Trim(); if (name.Length > 150) return;
            var indices = list.SelectedItems.Cast<GroupRow>().SelectMany(g => g.Indices).ToArray();
            foreach (var i in indices) Decisions[i] = Decisions[i] with { Project = name };
            if (name.Length > 0 && !target.Items.Contains(name)) target.Items.Add(name);
            Reload();
        }));
        actions.Children.Add(LiteraryUi.Button(L("SaveGroups"), () => { DialogResult = true; }, true));
        actions.Children.Add(LiteraryUi.Button(l("Common.Cancel"), Close)); Reload();
    }
    private sealed record GroupRow(string Label, int[] Indices, string Text);
}
