using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

/// <summary>Editable draft comparison. Copy/paste is deliberately ordinary keyboard/context-menu editing.</summary>
public sealed class MusicRhymeWindow : Window
{
    private readonly TextBox[] _editors = new TextBox[3];
    private readonly MusicRhymeColumnAnalysis[] _annotations = new MusicRhymeColumnAnalysis[3];
    private readonly TextBlock _candidates = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8), MaxHeight = 100 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _history = new() { MinWidth = 240, Margin = new(0, 0, 0, 10) };
    private readonly MusicRhymeController _owner;
    private readonly Func<string, string> _l;
    private string? _id;
    private bool _updating;
    public string Project { get; }
    public MusicRhymeWindow(MusicRhymeController owner, string project, Func<string, string> l, ResourceDictionary? hostResources = null)
    {
        MusicWishUi.PrepareWindow(this, l("Music.Rhyme.Title"), "Music.Rhyme.Workshop", 1250);
        if (hostResources is not null && !Resources.MergedDictionaries.Contains(hostResources))
            Resources.MergedDictionaries.Insert(0, hostResources);
        _owner = owner; Project = project; _l = l; Title = l("Music.Rhyme.Title"); Width = 1250; Height = 720; MinWidth = 700; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; UseLayoutRounding = true;
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "Music.Rhyme.Workshop");
        var root = new DockPanel { Margin = new(16) }; Content = root;
        DockPanel.SetDock(_history, Dock.Top); root.Children.Add(_history);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(_status);
        _candidates.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_candidates);
        var details = new Expander { Header = l("Music.Rhyme.Details"), Content = _details, Margin = new(0, 8, 0, 0) };
        details.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(details);
        var grid = new Grid(); root.Children.Add(grid);
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new());
            var title = new TextBlock { Text = l("Music.Rhyme.Column." + i), Margin = new(8, 4, 8, 8), FontSize = 18 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); Grid.SetColumn(title, i); grid.Children.Add(title);
            var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 17, IsUndoEnabled = true, UndoLimit = 1000, Padding = new(8) };
            editor.SetResourceReference(BackgroundProperty, "PanelBrush"); editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            editor.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(editor, "Music.Rhyme.Column." + i); _editors[i] = editor;
            var layer = new AdornerDecorator { Child = editor, Margin = new(4) };
            Grid.SetRow(layer, 1); Grid.SetColumn(layer, i); grid.Children.Add(layer);
            _annotations[i] = new(editor, layer.AdornerLayer, _owner);
            var column = i; editor.TextChanged += (_, _) => { if (!_updating && _id is not null) _owner.Edit(Project, _id, column, editor.Text); };
        }
        _history.SelectionChanged += (_, _) => { if (!_updating && _history.SelectedValue is string id) Show(id); };
        _owner.Changed += Refresh; Closed += (_, _) => { _owner.Changed -= Refresh; foreach (var item in _annotations) item.Dispose(); };
        Refresh();
    }
    private sealed record HistoryItem(string Id, string Name);
    public void Show(string? id)
    {
        _id = id; Refresh();
    }
    public void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        var items = _owner.HistoryFor(Project);
        if (items.Count == 0) { Close(); return; }
        var step = items.FirstOrDefault(s => s.Request.Id == _id) ?? items[^1]; _id = step.Request.Id;
        _updating = true;
        try
        {
            _history.ItemsSource = items.Select((s, i) => new HistoryItem(s.Request.Id, $"{i + 1} · {s.At.ToLocalTime():HH:mm:ss} · {_l("Music.Rhyme.Stage." + s.Stage)}")).ToArray();
            _history.DisplayMemberPath = "Name"; _history.SelectedValuePath = "Id"; _history.SelectedValue = _id;
            var columns = new[] { step.Original, step.Program, step.Model };
            _candidates.Text = _l("Music.Rhyme.ProgramHint") + "\n" + string.Join("\n", step.Suggestions.GroupBy(s => s.Line).Take(3)
                .Select(g => $"{_l("Music.Rhyme.Line")} {g.Key}: {string.Join(", ", g.Select(s => s.Word))}"));
            for (var i = 0; i < 3; i++)
            {
                var changed = _editors[i].Text != columns[i]; if (changed) _editors[i].Text = columns[i];
                _annotations[i].Refresh(changed);
            }
            if (!step.OriginalEdited && step.Analysis is { } initial)
                _annotations[0].Set(new(initial.Lines.Where(l => l.Start >= step.Request.Start && l.Start < step.Request.Start + step.Request.Length)
                    .Select(l => l with { Start = l.Start - step.Request.Start, EndingStart = l.EndingStart - step.Request.Start }).ToArray(), initial.Issues), step.Original);
            var error = step.Error;
            if (step.ModelReady && !step.ModelSkipped && MusicRhymeReply.Identical(step.Reply.Length > 0 ? step.Reply : step.Model, step.Request.Text))
                error = "Music.Rhyme.Unchanged";
            _status.Text = _l("Music.Rhyme.Stage." + step.Stage) + (error is null ? "" : " · " + _owner.Error(error));
            if (step.ProgramReady && MusicRhymeReply.Identical(step.ProgramDraft, step.Request.Text))
                _status.Text += " · " + _l("Music.Rhyme.ProgramUnchanged");
            if (step.ModelSkipped) _status.Text += " · " + _l("Music.Rhyme.NoIssues");
            _details.Text = string.Join("\n", step.Analysis?.Issues.Select(i => $"{_l("Music.Rhyme.Line")} {i.Line}: {_l("Music.Rhyme.Issue." + i.Kind)}") ?? [])
                + "\n\n" + string.Join("\n", step.Suggestions.Select(s => $"{_l("Music.Rhyme.Line")} {s.Line}: {s.Reading} · {s.Source}"))
                + "\n\n" + _l("Music.Rhyme.Prompt") + "\n" + step.Prompt + "\n\n" + step.Reference;
        }
        finally { _updating = false; }
    }
}
