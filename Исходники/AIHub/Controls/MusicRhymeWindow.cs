using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

/// <summary>Editable draft comparison. Copy/paste is deliberately ordinary keyboard/context-menu editing.</summary>
public sealed class MusicRhymeWindow : Window
{
    private readonly TextBox[] _editors = new TextBox[3];
    private readonly MusicRhymeColumnAnalysis[] _annotations = new MusicRhymeColumnAnalysis[3];
    private readonly MusicRhymeLoadingPanel[] _loading = new MusicRhymeLoadingPanel[3];
    private readonly TextBlock _candidates = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8), MaxHeight = 100 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _history = new() { MinWidth = 140, Margin = new(0, 0, 0, 10) };
    private readonly DockPanel _results = new();
    private readonly Grid _body = new();
    private readonly Button _restart;
    private readonly MusicRhymeCreativeButton _creative;
    private MusicRhymeSettingsPanel? _settings;
    private readonly MusicRhymeController _owner;
    private readonly Func<string, string> _l;
    private string? _id;
    private bool _updating;
    private bool _restarting;
    public string Project { get; }
    public MusicRhymeWindow(MusicRhymeController owner, string project, Func<string, string> l, ResourceDictionary? hostResources = null)
    {
        MusicWishUi.PrepareWindow(this, l("Music.Rhyme.Title"), "Music.Rhyme.Workshop", 1250);
        if (hostResources is not null && !Resources.MergedDictionaries.Contains(hostResources))
            Resources.MergedDictionaries.Insert(0, hostResources);
        _owner = owner; Project = project; _l = l; Title = l("Music.Rhyme.Title"); Width = 1250; Height = 720; MinWidth = 700; MinHeight = 420;
        _creative = new(l);
        _creative.ToggledByUser += active => {
            if (_updating) return;
            _owner.SetCreativeMode(active);
            _creative.IsChecked = _owner.Options.CreativeMode;
        };
        WindowStartupLocation = WindowStartupLocation.CenterOwner; UseLayoutRounding = true;
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "Music.Rhyme.Workshop");
        AutomationProperties.SetAutomationId(_candidates, "Music.Rhyme.ProgramChanges");
        var root = new DockPanel { Margin = new(16) }; Content = root;
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_history); AutomationProperties.SetAutomationId(_history, "Music.Rhyme.Steps");
        _restart = new Button { Content = "↻", ToolTip = l("Music.Rhyme.Restart"), Padding = new(12, 5, 12, 5), Margin = new(8, 0, 0, 10) };
        AutomationProperties.SetName(_restart, l("Music.Rhyme.Restart"));
        AutomationProperties.SetAutomationId(_restart, "Music.Rhyme.Restart");
        _restart.Click += async (_, _) => await RestartAsync();
        Grid.SetColumn(_restart, 2); header.Children.Add(_restart);
        var settings = new Button { Content = "⚙", ToolTip = l("Music.Rhyme.Settings.Title"), Padding = new(12, 5, 12, 5), Margin = new(8, 0, 0, 10) };
        AutomationProperties.SetName(settings, l("Music.Rhyme.Settings.Title"));
        AutomationProperties.SetAutomationId(settings, "Music.Rhyme.Settings.Open");
        settings.Click += (_, _) => { if (_settings is null) ShowSettings(); else CloseSettings(); };
        Grid.SetColumn(settings, 3); header.Children.Add(settings); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(_body); _body.Children.Add(_results);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); _results.Children.Add(footer);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(_status);
        _candidates.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_candidates);
        var details = new Expander { Header = l("Music.Rhyme.Details"), Content = _details, Margin = new(0, 8, 0, 0) };
        details.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(details);
        var grid = new Grid(); _results.Children.Add(grid);
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new());
            var title = new TextBlock { Text = l("Music.Rhyme.Column." + i), Margin = new(8, 4, 8, 8), FontSize = 18,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            if (i == 2)
            {
                var heading = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
                heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                _creative.Margin = new(0, 0, 8, 4); Grid.SetColumn(_creative, 1);
                heading.Children.Add(title); heading.Children.Add(_creative);
                Grid.SetColumn(heading, i); grid.Children.Add(heading);
            }
            else { Grid.SetColumn(title, i); grid.Children.Add(title); }
            var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 17, IsUndoEnabled = true, UndoLimit = 1000, Padding = new(8) };
            editor.SetResourceReference(BackgroundProperty, "PanelBrush"); editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            editor.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(editor, "Music.Rhyme.Column." + i); _editors[i] = editor;
            var layer = new AdornerDecorator { Child = editor, Margin = new(4) };
            Grid.SetRow(layer, 1); Grid.SetColumn(layer, i); grid.Children.Add(layer);
            _annotations[i] = new(editor, layer.AdornerLayer, _owner);
            _loading[i] = new(i); Grid.SetRow(_loading[i], 1); Grid.SetColumn(_loading[i], i); grid.Children.Add(_loading[i]);
            var column = i; editor.TextChanged += (_, _) => { if (!_updating && _id is not null) _owner.Edit(Project, _id, column, editor.Text); };
        }
        _history.SelectionChanged += (_, _) => { if (!_updating && _history.SelectedValue is string id) Show(id); };
        _owner.Changed += Refresh; Closed += (_, _) => { _owner.Changed -= Refresh; _creative.Dispose(); foreach (var item in _annotations) item.Dispose(); };
        Refresh();
    }
    private sealed record HistoryItem(string Id, string Name);
    public void Show(string? id)
    {
        _id = id; Refresh();
    }
    public void ShowSettings()
    {
        if (_settings is not null) _body.Children.Remove(_settings);
        _settings = new(_owner, _l, CloseSettings); _results.Visibility = Visibility.Collapsed; _body.Children.Add(_settings);
        _restart.IsEnabled = false;
    }
    private void CloseSettings()
    {
        if (_settings is not null) _body.Children.Remove(_settings);
        _settings = null; _results.Visibility = Visibility.Visible; Refresh();
    }
    private async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true; _restart.IsEnabled = false;
        try { await _owner.RestartAsync(Project); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _status.Text = _owner.Error(error.Message); }
        finally { _restarting = false; _restart.IsEnabled = _settings is null; }
    }
    public void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        var items = _owner.HistoryFor(Project);
        _creative.IsChecked = _owner.Options.CreativeMode;
        _history.IsEnabled = items.Count > 0;
        _restart.IsEnabled = !_restarting && _settings is null;
        if (items.Count == 0)
        {
            _updating = true;
            try
            {
                _id = null; _history.ItemsSource = null; _candidates.Text = _details.Text = "";
                for (var column = 0; column < 3; column++)
                {
                    _editors[column].Text = column == 0 ? _owner.CurrentSource : "";
                    _annotations[column].Refresh(true);
                    var loading = MusicRhymeLoadingState.For(null, column, false, false);
                    _editors[column].IsEnabled = loading.MessageKey is null; _loading[column].Apply(loading, _l);
                }
            }
            finally { _updating = false; }
            _status.Text = _owner.LaunchError is { } launchError ? _owner.Error(launchError) : _l("Music.Rhyme.Settings.NoSteps");
            return;
        }
        var step = items.FirstOrDefault(s => s.Request.Id == _id) ?? items[^1]; _id = step.Request.Id;
        _updating = true;
        try
        {
            var tuning = _owner.Options.Tuning;
            _candidates.MaxHeight = tuning.Integer("history.summary_height");
            _details.MaxHeight = tuning.Integer("history.details_height");
            _history.ItemsSource = items.Select((s, i) => new HistoryItem(s.Request.Id, $"{i + 1} · {s.At.ToLocalTime():HH:mm:ss} · {StageName(s)}")).ToArray();
            _history.DisplayMemberPath = "Name"; _history.SelectedValuePath = "Id"; _history.SelectedValue = _id;
            _history.ToolTip = (_history.SelectedItem as HistoryItem)?.Name;
            var columns = new[] { step.Original, step.Program, step.Model };
            _candidates.Text = _l("Music.Rhyme.ProgramHint") + "\n" + string.Join("\n", step.ProgramChanges.Take(tuning.Integer("history.summary_limit"))
                .Select(c => $"{_l("Music.Rhyme.Line")} {c.Line}: {c.Before} → {c.After} · {ChangeReason(c)}"));
            for (var i = 0; i < 3; i++)
            {
                _editors[i].UndoLimit = tuning.Integer("history.undo_limit");
                _editors[i].MaxLength = tuning.Integer("history.column_limit");
                var changed = _editors[i].Text != columns[i]; if (changed) _editors[i].Text = columns[i];
                _annotations[i].Refresh(changed);
                var loading = MusicRhymeLoadingState.For(step, i, _owner.IsRunning(Project, _id), _owner.IsPaused(Project, _id));
                _editors[i].IsEnabled = loading.MessageKey is null; _loading[i].Apply(loading, _l);
            }
            if (!step.OriginalEdited && step.Analysis is { } initial)
                _annotations[0].Set(MusicRhymeEditorial.ForScope(step) with {
                    Lines = initial.Lines.Where(l => l.Start >= step.Request.Start && l.Start + l.Length <= step.Request.Start + step.Request.Length)
                        .Select(l => l with { Start = l.Start - step.Request.Start, EndingStart = l.EndingStart - step.Request.Start }).ToArray()
                }, step.Original);
            var error = step.Error;
            if (step.ModelReady && !step.ModelSkipped && MusicRhymeReply.Identical(step.Reply.Length > 0 ? step.Reply : step.Model, step.Request.Text))
                error = "Music.Rhyme.Unchanged";
            _status.Text = StageName(step) + (error is null ? "" : " · " + _owner.Error(error));
            if (step.ProgramReady && MusicRhymeReply.Identical(step.ProgramDraft, step.Request.Text))
                _status.Text += " · " + _l("Music.Rhyme.ProgramUnchanged");
            if (step.ProgramReady && step.Analysis is not null && step.ProgramChanges.Length > 0)
            {
                var fragments = MusicRhymeEditorial.ForScope(step).Fragments;
                var missing = fragments.Count(f => !step.ProgramChanges.Any(c => f.Lines.Contains(c.Line)));
                if (missing > 0) _status.Text += " · " + string.Format(_l("Music.Rhyme.ProgramCoverageMissing"), missing, fragments.Length);
            }
            if (step.ModelSkipped) _status.Text += " · " + _l("Music.Rhyme.NoIssues");
            if (_owner.LaunchError is { } launchError) _status.Text = _owner.Error(launchError);
            _details.Text = string.Join("\n", step.Analysis is null ? [] : MusicRhymeEditorial.ForScope(step).Fragments
                .Select(f => $"{_l("Music.Rhyme.Line")} {string.Join(", ", f.Lines)}: {MusicRhymeEditorial.Explain(f, _l)}"))
                + "\n\n" + string.Join("\n", step.ProgramChanges.Select(c => $"{_l("Music.Rhyme.Line")} {c.Line}: {c.Before}\n→ {c.After} · {ChangeReason(c)}"))
                + "\n\n" + string.Join("\n", step.Suggestions.Select(s => $"{_l("Music.Rhyme.Line")} {s.Line}: {s.Reading} · {s.Source} · {s.Relation} · {s.SoundScore:F2}"))
                + "\n\n" + _l("Music.Rhyme.Prompt") + "\n" + step.Prompt + "\n\n" + step.Reference;
            if (step.CreativeSummary.Length > 0)
                _details.Text += "\n\n" + _l("Music.Rhyme.Creative.Summary") + "\n" + step.CreativeSummary;
        }
        finally { _updating = false; }
    }
    private string ChangeReason(MusicRhymeProgramChange change) => _l("Music.Rhyme.Change." + change.Reason)
        + (!change.GrammarKnown && change.Relation.Length > 0 ? " · " + _l("Music.Rhyme.GrammarUnverified") : "");
    private string StageName(MusicRhymeStep step) => _l(!step.GenerationRequested && step.ProgramReady && !step.ModelReady && step.Error is null
        ? "Music.Rhyme.ProgramReady" : "Music.Rhyme.Stage." + step.Stage);
}
