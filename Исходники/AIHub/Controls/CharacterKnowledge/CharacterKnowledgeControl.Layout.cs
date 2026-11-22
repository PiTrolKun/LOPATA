using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.CharacterKnowledge;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.CharacterKnowledge;

internal sealed partial class CharacterKnowledgeControl
{
    private ComboBox _baseSelector = null!;
    private Button _export = null!, _entriesTab = null!, _rulesTab = null!, _add = null!, _edit = null!, _variant = null!, _delete = null!;
    private ListBox _items = null!;
    private ContentControl _editor = null!;
    private TextBlock _editorTitle = null!, _status = null!, _goal = null!, _count = null!, _low = null!, _high = null!, _rag = null!;
    private Border _ragTick = null!;
    private ProgressBar _progress = null!;
    private WrapPanel _commitCommands = null!;
    private Control? _draftStart;
    private ItemRow? SelectedItem => _items.SelectedItem as ItemRow;

    private void BuildInterface()
    {
        _baseSelector = new ComboBox { MinWidth = 220, MaxWidth = 360, DisplayMemberPath = nameof(BaseRow.Label),
            SelectedValuePath = nameof(BaseRow.Id), Margin = new(0, 0, 8, 6) };
        Identify(_baseSelector, "Base");
        _baseSelector.SelectionChanged += (_, _) => OpenBase();
        _export = Command("Export", ExportBase);
        var baseCaption = Text(T("Base"));
        baseCaption.Margin = new(0, 8, 8, 6);
        var files = Row(baseCaption, _baseSelector, Command("CreateBase", CreateBase), Command("Import", ImportBase), _export);

        _entriesTab = Command("Entries", () => SelectSection(false));
        _rulesTab = Command("Rules", () => SelectSection(true));
        _add = Command("Add", StartNew);
        _edit = Command("Edit", () => StartFromSelection(false));
        _variant = Command("Variant", () => StartFromSelection(true));
        _delete = Command("Delete", RemoveSelected);
        _items = new ListBox { DisplayMemberPath = nameof(ItemRow.Label) };
        Identify(_items, "Items");
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, ScrollBarVisibility.Disabled);
        _items.SelectionChanged += (_, _) =>
        {
            if (_rendering) return;
            UpdateItemCommands();
            if (!IsCurrentDraft) ShowEditor();
        };
        var itemCommands = Row(_add, _edit, _variant, _delete);
        var sidebar = new DockPanel();
        DockPanel.SetDock(itemCommands, Dock.Bottom);
        sidebar.Children.Add(itemCommands);
        sidebar.Children.Add(_items);

        _editor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        _editorTitle = Text("", 19);
        _editorTitle.FontWeight = FontWeights.SemiBold;
        _editorTitle.Margin = new(0, 0, 0, 12);
        _commitCommands = Row(Command("Save", () => SaveDraft(false), primary: true),
            Command("SaveNext", () => SaveDraft(true)), Command("CloseDraft", CloseDraft));
        _commitCommands.Margin = new(0, 14, 0, 0);
        var detail = new DockPanel();
        DockPanel.SetDock(_editorTitle, Dock.Top);
        DockPanel.SetDock(_commitCommands, Dock.Bottom);
        detail.Children.Add(_editorTitle);
        detail.Children.Add(_commitCommands);
        detail.Children.Add(_editor);

        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 150, MaxWidth = 360 });
        workspace.ColumnDefinitions.Add(new() { Width = new(12) });
        workspace.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        workspace.Children.Add(Card(sidebar, 12));
        var detailCard = Card(detail, 16);
        Grid.SetColumn(detailCard, 2);
        workspace.Children.Add(detailCard);

        var title = Text(T("Title"), 24);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new(0, 0, 0, 12);
        var scale = BuildProgress();
        scale.Margin = new(0, 6, 0, 14);
        var tabs = Row(_entriesTab, _rulesTab);
        _status = Text("");
        _status.Margin = new(0, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        Identify(_status, "Status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var page = new DockPanel { Margin = new(8), LastChildFill = true, UseLayoutRounding = true };
        foreach (var control in new FrameworkElement[] { title, files, scale, tabs })
        {
            DockPanel.SetDock(control, Dock.Top);
            page.Children.Add(control);
        }
        DockPanel.SetDock(_status, Dock.Bottom);
        page.Children.Add(_status);
        page.Children.Add(workspace);
        Content = page;
    }

    private Border BuildProgress()
    {
        _goal = Text("", 16);
        _goal.FontWeight = FontWeights.SemiBold;
        _count = Text("");
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        Identify(_count, "Count");
        var heading = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        heading.Children.Add(_count);
        heading.Children.Add(_goal);

        _progress = new ProgressBar { Height = 9, Maximum = 1 };
        Identify(_progress, "Progress");
        var track = new Grid { Margin = new(0, 8, 0, 0) };
        _ragTick = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        _ragTick.SetResourceReference(Border.BackgroundProperty, "TextPrimaryBrush");
        track.Children.Add(_progress);
        track.Children.Add(AtRagMark(_ragTick));

        _low = Text("");
        _high = Text("");
        _rag = Text("rag");
        Identify(_rag, "Rag");
        _high.HorizontalAlignment = HorizontalAlignment.Right;
        var ticks = new Grid { Margin = new(0, 4, 0, 0) };
        ticks.Children.Add(_low);
        ticks.Children.Add(_high);
        ticks.Children.Add(AtRagMark(_rag));
        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(track);
        content.Children.Add(ticks);
        return Card(content, 12);
    }

    private static Grid AtRagMark(FrameworkElement marker)
    {
        var grid = new Grid { IsHitTestVisible = false };
        grid.ColumnDefinitions.Add(new() { Width = new(KnowledgeProgress.Rag, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new(KnowledgeProgress.FirstGoal - KnowledgeProgress.Rag, GridUnitType.Star) });
        Grid.SetColumn(marker, 1);
        grid.Children.Add(marker);
        return grid;
    }

    private void RefreshBases()
    {
        _rendering = true;
        try
        {
            var repeated = _bases.GroupBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(group => group.Select((item, index) => new BaseRow(item.BaseId,
                    index == 0 ? item.Name : $"{item.Name} ({index + 1})")));
            _baseSelector.ItemsSource = repeated.ToArray();
            _baseSelector.SelectedValue = _project?.Base.BaseId;
        }
        finally { _rendering = false; }
    }

    private void RefreshWorkspace(Guid? selected = null)
    {
        _rendering = true;
        try
        {
            _entriesTab.SetResourceReference(StyleProperty, _rules ? "SettingsActionButtonStyle" : "SettingsPrimaryActionStyle");
            _rulesTab.SetResourceReference(StyleProperty, _rules ? "SettingsPrimaryActionStyle" : "SettingsActionButtonStyle");
            _export.IsEnabled = _add.IsEnabled = _project is not null;
            var knowledge = _project?.Knowledge;
            ItemRow[] rows = knowledge is null ? [] : _rules
                ? knowledge.Rules.Select(rule => new ItemRow(rule.Id, Preview(rule.Condition))).ToArray()
                : knowledge.Entries.Select(entry => new ItemRow(entry.Id, Preview(entry.Input))).ToArray();
            _items.ItemsSource = rows;
            _items.SelectedItem = rows.FirstOrDefault(item => item.Id == selected);
            if (SelectedItem is not null) _items.ScrollIntoView(SelectedItem);
            UpdateItemCommands();

            var progress = new KnowledgeProgress(knowledge is null ? 0 : knowledge.Entries.Length + knowledge.Rules.Length);
            _goal.Text = T(progress.IsProductive ? "Productive" : "FirstGoal");
            _count.Text = string.Format(CultureInfo.CurrentCulture, T("Count"), progress.Total);
            _low.Text = progress.Start.ToString("N0", CultureInfo.CurrentCulture);
            _high.Text = progress.End.ToString("N0", CultureInfo.CurrentCulture);
            _progress.Value = progress.Fraction;
            _progress.SetResourceReference(ForegroundProperty, progress.IsProductive ? "PoetryThoughtActiveBrush" : "AccentBrush");
            _rag.Visibility = _ragTick.Visibility = progress.IsProductive ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _rendering = false; }
        ShowEditor();
    }

    private void SelectSection(bool rules)
    {
        if (_rules == rules) return;
        _rules = rules;
        RefreshWorkspace(IsCurrentDraft ? _draft?.TargetId : null);
    }

    private void UpdateItemCommands() => _edit.IsEnabled = _variant.IsEnabled = _delete.IsEnabled = SelectedItem is not null;

    private void RefreshStatus()
    {
        _status.Text = _statusKey is null ? "" : string.Format(CultureInfo.CurrentCulture, T(_statusKey), _statusArguments);
        _status.Visibility = _statusKey is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string Preview(string text)
    {
        var trimmed = text.Trim();
        var firstBreak = trimmed.IndexOfAny(['\r', '\n']);
        if (firstBreak >= 0) trimmed = trimmed[..firstBreak];
        return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
    }

    private sealed record BaseRow(Guid Id, string Label);
    private sealed record ItemRow(Guid Id, string Label);
}
