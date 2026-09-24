using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private readonly HashSet<string> _selectedWorkUnits = new(StringComparer.Ordinal);
    private readonly List<(CheckBox Box, string[] Ids)> _workChecks = [];
    private string _workSelectionKey = "";
    private TextBlock? _workSelectionSummary;
    private Button? _workContinueButton;

    private void PrepareWorkSelection()
    {
        var key = _session!.State.Id + "/" + ImportSession.Hash(JsonSerializer.Serialize(_groupingOriginal ?? _first, ImportJson.Options));
        if (_workSelectionKey != key)
        {
            _workSelectionKey = key;
            _selectedWorkUnits.Clear();
            _selectedWorkUnits.UnionWith(_first!.Where(d => d.Project.Length == 0 ||
                d.Project == _session.State.SelectedProject).Select(d => d.Id));
        }
        _selectedWorkUnits.IntersectWith(_first!.Select(d => d.Id));
        _workChecks.Clear();
    }

    private FrameworkElement CreateWorkTree()
    {
        PrepareWorkSelection();
        var root = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 12, 0, 6), LastChildFill = true };
        var inspect = LiteraryImportIcons.Create("\uE721", L("EditGroups"), () =>
        {
            var dialog = new LiteraryImportGroupingWindow(Window.GetWindow(this), _input!, _first!, _l);
            if (dialog.ShowDialog() == true) SaveWorkGrouping(dialog.Decisions);
        });
        inspect.IsEnabled = _session!.State.PlannedPath.Length == 0 && _groupingOriginal is not null;
        DockPanel.SetDock(inspect, Dock.Right); heading.Children.Add(inspect);
        heading.Children.Add(LiteraryUi.Text(L("ChooseWorkParts"), true)); root.Children.Add(heading);
        root.Children.Add(LiteraryUi.Text(L("WorkSelectionHint")));
        var groups = ImportWorkSelection.Groups(_input!, _first!);
        foreach (var group in groups) root.Children.Add(CreateWorkGroup(group));
        _workSelectionSummary = LiteraryUi.Text("");
        _workSelectionSummary.Margin = new Thickness(0, 14, 0, 10);
        root.Children.Add(_workSelectionSummary);
        RefreshWorkChecks();
        return root;
    }

    private Border CreateWorkGroup(ImportPreviewGroup group)
    {
        var ids = GroupUnits(group);
        var title = group.IsOther ? L("PreviewOther") : group.Name;
        var suggested = !group.IsOther && group.Variants.Any(v => MatchesPreviewTitle(v.Name));
        var heading = WorkHeading(title, group.Characters, ids);
        var expander = WorkExpander(heading);
        var loaded = false;
        expander.Expanded += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            var inside = new StackPanel { Margin = new Thickness(24, 6, 0, 4) };
            if (group.Variants.Count > 1)
            {
                inside.Children.Add(LiteraryUi.Text(L("WorkVariantsHint")));
                foreach (var variant in group.Variants)
                {
                    var variantIds = variant.Parts.SelectMany(p => p.UnitIds).ToArray();
                    var branch = WorkExpander(WorkHeading(variant.Name, variant.Characters, variantIds));
                    branch.Margin = new Thickness(0, 4, 0, 4);
                    var variantLoaded = false;
                    branch.Expanded += (_, _) =>
                    {
                        if (variantLoaded) return;
                        variantLoaded = true;
                        branch.Content = CreateWorkParts(variant.Parts);
                    };
                    inside.Children.Add(branch);
                }
            }
            else inside.Children.Add(CreateWorkParts(group.Variants[0].Parts));
            expander.Content = inside;
            RefreshWorkChecks();
        };
        var items = new StackPanel(); items.Children.Add(expander);
        if (suggested)
        {
            var hint = PreviewTitleHint(); hint.Margin = new Thickness(28, 3, 0, 0);
            items.Children.Add(hint);
        }
        if (group.IsOther)
            items.Children.Add(LiteraryUi.Text(L("OtherWorkSelectionHint")));
        var border = new Border { Child = items, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, suggested || group.Variants.Count > 1 ? "AccentBrush" : "LineBrush");
        if (suggested) PulsePreviewHint(border);
        return border;
    }

    private FrameworkElement WorkHeading(string title, long characters, string[] ids)
    {
        var row = new DockPanel { LastChildFill = true };
        var check = WorkCheck(ids, title);
        DockPanel.SetDock(check, Dock.Left); row.Children.Add(check);
        var text = new StackPanel();
        var name = LiteraryUi.Text(title); name.FontWeight = FontWeights.SemiBold;
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.Children.Add(name);
        text.Children.Add(LiteraryUi.Text(string.Format(L("WorkGroupSize"), characters, ids.Length)));
        row.Children.Add(text);
        return row;
    }

    private static Expander WorkExpander(FrameworkElement heading)
    {
        var expander = new Expander { Header = heading, IsExpanded = false,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Padding = new Thickness(0, 2, 0, 2) };
        // The standard header measures without a width limit; constrain it so long titles wrap.
        expander.SizeChanged += (_, e) => heading.MaxWidth = Math.Max(40, e.NewSize.Width - 38);
        return expander;
    }

    private FrameworkElement CreateWorkParts(IReadOnlyList<ImportPreviewPart> parts)
    {
        var rows = new StackPanel { Margin = new Thickness(14, 4, 0, 4) };
        var shown = 0;
        Button? more = null;
        void AddPage()
        {
            if (more is not null) rows.Children.Remove(more);
            foreach (var part in parts.Skip(shown).Take(40))
            {
                var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), LastChildFill = true };
                var read = LiteraryImportIcons.Create("\uE890", L("PreviewReadPart"), () => ReadPreviewPart(part));
                DockPanel.SetDock(read, Dock.Right); row.Children.Add(read);
                var check = WorkCheck(part.UnitIds, part.Excerpt);
                DockPanel.SetDock(check, Dock.Left); row.Children.Add(check);
                var text = new StackPanel();
                text.Children.Add(LiteraryUi.Text(part.Excerpt));
                text.Children.Add(LiteraryUi.Text($"{part.Conversation} · {part.Characters:N0} {L("PreviewCharacters")}"));
                row.Children.Add(text); rows.Children.Add(row);
            }
            shown = Math.Min(shown + 40, parts.Count);
            if (shown < parts.Count)
            {
                more = LiteraryUi.Button(L("PreviewMore"), AddPage); rows.Children.Add(more);
            }
            RefreshWorkChecks();
        }
        AddPage();
        return rows;
    }

    private CheckBox WorkCheck(string[] ids, string label)
    {
        var box = new CheckBox { IsThreeState = true, Margin = new Thickness(4, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = _session!.State.PlannedPath.Length == 0 };
        AutomationProperties.SetName(box, label);
        _workChecks.Add((box, ids));
        box.Click += (_, _) =>
        {
            var select = !ids.All(_selectedWorkUnits.Contains);
            foreach (var id in ids)
                if (select) _selectedWorkUnits.Add(id); else _selectedWorkUnits.Remove(id);
            RefreshWorkChecks(); DraftChanged();
        };
        return box;
    }

    private void RefreshWorkChecks()
    {
        foreach (var (box, ids) in _workChecks)
            box.IsChecked = ids.All(_selectedWorkUnits.Contains) ? true : ids.Any(_selectedWorkUnits.Contains) ? null : false;
        if (_workSelectionSummary is not null)
            _workSelectionSummary.Text = string.Format(L("WorkSelectionCount"), _selectedWorkUnits.Count);
        if (_workContinueButton is not null) _workContinueButton.IsEnabled = _selectedWorkUnits.Count > 0;
    }
}
