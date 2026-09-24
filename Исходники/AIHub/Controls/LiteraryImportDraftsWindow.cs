using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace AIHub.Controls;

public sealed class LiteraryImportDraftsWindow : Window
{
    public LiteraryImportDraft? SelectedDraft { get; private set; }

    public LiteraryImportDraftsWindow(Window owner, IReadOnlyList<LiteraryImportDraft> drafts,
        Func<string, string> localize, Action<string> forget)
    {
        string L(string key) => localize("Literary.Import." + key);
        LiteraryPromptDialogUi.Configure(this, owner, L("RecentTitle"));
        Width = 780; Height = 440; MinWidth = 600; MinHeight = 320;
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var hint = LiteraryUi.Text(L("RecentHint"));
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var status = LiteraryUi.Text("");
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var list = new ListBox { Margin = new Thickness(0, 14, 0, 0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        root.Children.Add(list);
        void Select()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: LiteraryImportDraft draft }) return;
            SelectedDraft = draft; DialogResult = true;
        }
        var open = LiteraryUi.Button(L("RecentOpen"), Select, true);
        void Refresh()
        {
            open.IsEnabled = list.SelectedItem is ListBoxItem { Tag: LiteraryImportDraft };
            hint.Text = L(list.Items.Count == 0 ? "RecentEmpty" : "RecentHint");
        }
        list.SelectionChanged += (_, _) => Refresh();
        foreach (var draft in drafts)
        {
            var title = string.IsNullOrWhiteSpace(draft.ProjectName) ? L("RecentUnnamed") : draft.ProjectName;
            var work = string.IsNullOrWhiteSpace(draft.WorkTitle) ? "" : " · " + draft.WorkTitle;
            var item = new ListBoxItem
            {
                Tag = draft, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 9, 10, 9)
            };
            var row = new DockPanel(); item.Content = row;
            var remove = LiteraryImportIcons.Create("\uE74D", L("RecentRemove"), () =>
            {
                try
                {
                    forget(draft.Id);
                    list.Items.Remove(item);
                    if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                    status.Text = ""; Refresh();
                }
                catch (System.IO.IOException) { status.Text = L("RecentRemoveFailed"); }
                catch (UnauthorizedAccessException) { status.Text = L("RecentRemoveFailed"); }
            });
            remove.PreviewMouseDoubleClick += (_, e) => e.Handled = true;
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var stage = draft.Step switch { "dialogs" => "2", "analysis" or "preview" => "3", "works" => "4", "review" => "5", _ => "1" };
            var text = new StackPanel();
            text.Children.Add(LiteraryUi.Text(title + work, true));
            text.Children.Add(LiteraryUi.Text($"{L("RecentAccess")}: {draft.LastAccessed.ToLocalTime():dd.MM.yyyy HH:mm}  ·  {L("RecentStep")}: {L("Stage" + stage)}"));
            row.Children.Add(text); list.Items.Add(item);
        }
        list.SelectedIndex = drafts.Count > 0 ? 0 : -1;
        list.MouseDoubleClick += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is Button) return;
            Select();
        };
        Refresh();
        actions.Children.Add(open);
        actions.Children.Add(LiteraryUi.Button(localize("Common.Cancel"), Close));
    }
}
