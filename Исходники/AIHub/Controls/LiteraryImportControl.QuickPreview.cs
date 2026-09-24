using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AIHub.Services.LiteraryImport;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private IReadOnlyList<ImportPreviewGroup> _previewScan = [];
    private readonly HashSet<string> _selectedPreviewUnits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _splitPreviewGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedPreviewGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _editedPreviewTitles = new(StringComparer.Ordinal);
    private readonly List<(CheckBox Box, string[] UnitIds)> _previewChecks = [];
    private bool _refreshingPreviewChecks;

    private void OpenQuickPreview()
    {
        var dialogs = _conversations.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToArray();
        if (dialogs.Length == 0)
        {
            _status.Text = L("PreviewSelectDialog"); _status.Visibility = Visibility.Visible;
            return;
        }
        SaveDraft("dialogs");
        var workTitle = _draftWorkTitle.Text.Trim();
        _ = RunAsync(async ct =>
        {
            _session!.State.Conversations = dialogs;
            _session.Save();
            var scan = await Task.Run(() => ImportQuickPreview.Scan(_input!, dialogs,
                workTitle, ct), ct);
            _session.State.LastError = "";
            _session.Save();
            ShowQuickPreview(scan);
            SaveDraft("preview");
        });
    }

    private void ShowQuickPreview(IReadOnlyList<ImportPreviewGroup> scan)
    {
        _previewScan = scan;
        _showingDialogSelection = false; _showingQuickPreview = true;
        _body.Children.Clear(); _previewChecks.Clear();
        var layout = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(CreateTopics(2));
        var content = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        content.Children.Add(LiteraryUi.Text(L("PreviewTitle"), true));
        content.Children.Add(LiteraryUi.Text(L("PreviewHint")));

        var display = new List<(ImportPreviewGroup Group, string ParentKey, bool Split)>();
        foreach (var group in scan.Where(g => !g.IsOther))
        {
            if (_splitPreviewGroups.Contains(group.Key) && group.Variants.Count > 1)
            {
                display.AddRange(group.Variants.Select(v =>
                    (new ImportPreviewGroup(group.Key + "|" + v.Name, v.Name, false, [v], v.Characters), group.Key, true)));
            }
            else display.Add((group, group.Key, false));
        }
        foreach (var item in display.OrderByDescending(g => g.Group.Characters))
            content.Children.Add(CreatePreviewGroup(item.Group, item.ParentKey, item.Split));
        var other = scan.Single(g => g.IsOther);
        content.Children.Add(CreatePreviewGroup(other, other.Key, false));
        content.Children.Add(LiteraryUi.Text(_language == "ru"
            ? "Выберите части для полного анализа. Исходный экспорт останется без изменений."
            : "Select parts for full analysis. The source export will remain unchanged."));
        content.Children.Add(LiteraryUi.Button(L("Next"), StartFullAnalysis, true));
        Grid.SetColumn(content, 1); layout.Children.Add(content);
        _body.Children.Add(layout);
        RefreshPreviewChecks();
    }

    private Border CreatePreviewGroup(ImportPreviewGroup group, string parentKey, bool split)
    {
        var ids = GroupUnits(group);
        var suggested = !group.IsOther && group.Variants.Any(v => MatchesPreviewTitle(v.Name));
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var selected = SelectionCheck(ids);
        heading.Children.Add(selected);
        RegisterPreviewCheck(selected, ids);
        if (group.IsOther) heading.Children.Add(LiteraryUi.Text(L("PreviewOther"), true));
        else
        {
            var title = new TextBox
            {
                Text = _editedPreviewTitles.TryGetValue(group.Key, out var edited) ? edited : group.Name,
                MaxLength = 150, MinWidth = 230, MaxWidth = 500,
                Margin = new Thickness(6, 0, 10, 0), Padding = new Thickness(5)
            };
            title.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
            title.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            title.ToolTip = L("PreviewRename");
            title.TextChanged += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text)) _editedPreviewTitles.Remove(group.Key);
                else _editedPreviewTitles[group.Key] = title.Text.Trim();
                DraftChanged();
            };
            heading.Children.Add(title);
        }
        if (suggested) heading.Children.Add(PreviewTitleHint());
        heading.Children.Add(LiteraryUi.Text($"{group.Characters:N0} {L("PreviewCharacters")}"));
        var expander = new Expander { Header = heading, IsExpanded = false, Padding = new Thickness(0, 3, 0, 3) };
        var loaded = false;
        expander.Expanded += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            var inside = new StackPanel { Margin = new Thickness(25, 0, 0, 10) };
            if (!group.IsOther && group.Variants.Count > 1)
            {
                var notice = LiteraryUi.Text(_confirmedPreviewGroups.Contains(group.Key)
                    ? L("PreviewMergeConfirmed") : L("PreviewPossibleMerge"));
                notice.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                inside.Children.Add(notice);
                var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
                actions.Children.Add(LiteraryUi.Button(L("PreviewConfirmMerge"), () =>
                { _confirmedPreviewGroups.Add(group.Key); DraftChanged(); ShowQuickPreview(_previewScan); }));
                actions.Children.Add(LiteraryUi.Button(L("PreviewSplit"), () =>
                { _splitPreviewGroups.Add(group.Key); _confirmedPreviewGroups.Remove(group.Key);
                    DraftChanged(); ShowQuickPreview(_previewScan); }));
                inside.Children.Add(actions);
            }
            else if (split)
                inside.Children.Add(LiteraryUi.Button(L("PreviewRecombine"), () =>
                { _splitPreviewGroups.Remove(parentKey); DraftChanged(); ShowQuickPreview(_previewScan); }));
            foreach (var variant in group.Variants)
                inside.Children.Add(CreatePreviewVariant(variant));
            expander.Content = inside;
            RefreshPreviewChecks();
        };
        var border = new Border { Child = expander, CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty,
            suggested || !group.IsOther && group.Variants.Count > 1 ? "AccentBrush" : "LineBrush");
        if (suggested) PulsePreviewHint(border);
        return border;
    }

    private Expander CreatePreviewVariant(ImportPreviewVariant variant)
    {
        var ids = variant.Parts.SelectMany(p => p.UnitIds).ToArray();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var check = SelectionCheck(ids); header.Children.Add(check); RegisterPreviewCheck(check, ids);
        header.Children.Add(LiteraryUi.Text($"{variant.Name} · {variant.Characters:N0} {L("PreviewCharacters")}"));
        if (MatchesPreviewTitle(variant.Name)) header.Children.Add(PreviewTitleHint());
        var expander = new Expander { Header = header, Margin = new Thickness(0, 4, 0, 4) };
        if (MatchesPreviewTitle(variant.Name)) PulsePreviewHint(expander);
        var loaded = false;
        expander.Expanded += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            var rows = new StackPanel { Margin = new Thickness(22, 0, 0, 8) };
            var shown = 0;
            void AddPage()
            {
                var oldButton = rows.Children.OfType<System.Windows.Controls.Button>().LastOrDefault();
                if (oldButton?.Tag as string == "more") rows.Children.Remove(oldButton);
                foreach (var part in variant.Parts.Skip(shown).Take(80))
                {
                    var row = SelectionCheck(part.UnitIds);
                    row.Margin = new Thickness(0, 5, 0, 5);
                    var excerpt = LiteraryUi.Text($"{part.Conversation} · {part.Characters:N0} {L("PreviewCharacters")}\n{part.Excerpt}");
                    excerpt.MaxWidth = 680;
                    row.Content = excerpt;
                    var line = new WrapPanel();
                    line.Children.Add(row);
                    line.Children.Add(LiteraryUi.Button(L("PreviewReadPart"), () => ReadPreviewPart(part)));
                    rows.Children.Add(line); RegisterPreviewCheck(row, part.UnitIds);
                }
                shown = Math.Min(shown + 80, variant.Parts.Count);
                if (shown < variant.Parts.Count)
                {
                    var more = LiteraryUi.Button(L("PreviewMore"), AddPage);
                    more.Tag = "more"; rows.Children.Add(more);
                }
                RefreshPreviewChecks();
            }
            expander.Content = rows; AddPage();
        };
        return expander;
    }

    private static string[] GroupUnits(ImportPreviewGroup group) =>
        group.Variants.SelectMany(v => v.Parts).SelectMany(p => p.UnitIds).ToArray();

    private bool MatchesPreviewTitle(string title) => ImportWorkNames.MatchesDialogTitle(_draftWorkTitle.Text, title);

    private TextBlock PreviewTitleHint()
    {
        var hint = LiteraryUi.Text(" · " + L("PreviewSuggested"));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return hint;
    }

    private static void PulsePreviewHint(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.Loaded += (_, _) => element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, .45, TimeSpan.FromMilliseconds(360))
            { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
    }

    private void ReadPreviewPart(ImportPreviewPart part)
    {
        var units = _input!.Units.ToDictionary(u => u.Id);
        var source = string.Join(Environment.NewLine + Environment.NewLine,
            part.UnitIds.Where(units.ContainsKey).Select(id => units[id].Text));
        var dialog = new Window();
        LiteraryPromptDialogUi.Configure(dialog, Window.GetWindow(this), L("PreviewReadPart"));
        dialog.Width = 920; dialog.Height = 680; dialog.MinWidth = 620; dialog.MinHeight = 400;
        var panel = new DockPanel { Margin = new Thickness(20) };
        dialog.Content = panel;
        var heading = LiteraryUi.Text(part.Conversation + " · " + part.Characters.ToString("N0")
            + " " + L("PreviewCharacters"));
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var close = LiteraryUi.Button(_l("Common.Close"), dialog.Close);
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var text = new TextBox { Text = source, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        text.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(text);
        dialog.ShowDialog();
    }

    private CheckBox SelectionCheck(string[] ids) => new()
    {
        IsThreeState = true,
        IsChecked = ids.Length > 0 && ids.All(_selectedPreviewUnits.Contains) ? true
            : ids.Any(_selectedPreviewUnits.Contains) ? null : false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private void RegisterPreviewCheck(CheckBox box, string[] ids)
    {
        _previewChecks.Add((box, ids));
        void Set(bool value)
        {
            if (_refreshingPreviewChecks) return;
            foreach (var id in ids)
                if (value) _selectedPreviewUnits.Add(id); else _selectedPreviewUnits.Remove(id);
            RefreshPreviewChecks(); DraftChanged();
        }
        box.Checked += (_, _) => Set(true);
        box.Unchecked += (_, _) => Set(false);
    }

    private void RefreshPreviewChecks()
    {
        _refreshingPreviewChecks = true;
        try
        {
            foreach (var (box, ids) in _previewChecks)
                box.IsChecked = ids.Length > 0 && ids.All(_selectedPreviewUnits.Contains) ? true
                    : ids.Any(_selectedPreviewUnits.Contains) ? null : false;
        }
        finally { _refreshingPreviewChecks = false; }
    }
}
